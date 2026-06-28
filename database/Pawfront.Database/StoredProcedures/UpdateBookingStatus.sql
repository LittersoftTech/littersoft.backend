-- Moves a booking to a new lifecycle status and writes an audit row, atomically.
-- This is the engine behind the simple "flip" transitions only — the dedicated
-- endpoints (/accept, /decline, /complete, /cancel) call it with a fixed target
-- status; the data-carrying flows (start-with-OTP, modification request/respond)
-- have their own sprocs. The acting party (@Actor = 'Provider' | 'Parent') and
-- @ActorId come from the authenticated route, never the client body. Enforces:
--   * the actor is a party to the booking            (THROW 51121)
--   * the status is one the actor may set            (THROW 51122)
--   * the booking is not already terminal            (THROW 51123)
--   * the status actually changes                    (THROW 51124)
--   * the transition is allowed from the current state (THROW 51126)
--   * COMPLETED requires >= 1 evidence photo         (THROW 51127)
-- Other THROWs: 51120 booking not found, 51125 invalid actor/status value.
--
-- Engine-settable per actor (other statuses are reached via dedicated sprocs):
--   Provider -> CONFIRMED (from CREATED), PROVIDER_DECLINED (from CREATED),
--               COMPLETED (from JOB_STARTED, evidence-gated), PROVIDER_CANCELLED
--   Parent   -> PARENT_CANCELLED
-- Terminal states (no further change): COMPLETED, PROVIDER_DECLINED,
-- PROVIDER_CANCELLED, PARENT_CANCELLED.
CREATE OR ALTER PROCEDURE [Booking].[UpdateBookingStatus]
    @BookingId UNIQUEIDENTIFIER,
    @NewStatus NVARCHAR(48),
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @Note NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Static validation of the inputs (defense-in-depth; the API validates too).
    IF @Actor NOT IN (N'Provider', N'Parent')
    BEGIN
        THROW 51125, 'Actor must be Provider or Parent.', 1;
    END

    IF @NewStatus NOT IN (N'CONFIRMED', N'PROVIDER_DECLINED', N'COMPLETED',
                          N'PROVIDER_CANCELLED', N'PARENT_CANCELLED')
    BEGIN
        THROW 51125, 'Unknown or non-engine booking status.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status],
           @ProviderId = [ProviderId],
           @PetParentId = [PetParentId]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51120, 'Booking was not found.', 1;
    END

    -- The actor must be a party to this booking.
    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51121, 'You are not a party to this booking.', 1;
    END

    -- The status must be one this actor is allowed to set via the engine.
    IF (@Actor = N'Provider'
            AND @NewStatus NOT IN (N'CONFIRMED', N'PROVIDER_DECLINED', N'COMPLETED', N'PROVIDER_CANCELLED'))
       OR (@Actor = N'Parent'
            AND @NewStatus NOT IN (N'PARENT_CANCELLED'))
    BEGIN
        THROW 51122, 'This status is not permitted for this actor.', 1;
    END

    -- A booking in a terminal state can't change further.
    IF @CurrentStatus IN (N'COMPLETED', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED', N'PARENT_CANCELLED')
    BEGIN
        THROW 51123, 'Booking is in a terminal state and cannot change.', 1;
    END

    IF @CurrentStatus = @NewStatus
    BEGIN
        THROW 51124, 'Booking is already in the requested status.', 1;
    END

    -- From-state rules for the engine transitions.
    IF (@NewStatus = N'CONFIRMED'        AND @CurrentStatus <> N'CREATED')
       OR (@NewStatus = N'PROVIDER_DECLINED' AND @CurrentStatus <> N'CREATED')
       OR (@NewStatus = N'COMPLETED'     AND @CurrentStatus <> N'JOB_STARTED')
    BEGIN
        THROW 51126, 'This transition is not allowed from the current status.', 1;
    END
    -- (PROVIDER_CANCELLED / PARENT_CANCELLED are allowed from any non-terminal state.)

    -- Completing a job requires the provider to have uploaded evidence first.
    IF @NewStatus = N'COMPLETED'
       AND NOT EXISTS (SELECT 1 FROM [Booking].[BookingEvidence] WHERE [BookingId] = @BookingId)
    BEGIN
        THROW 51127, 'Upload at least one evidence photo before completing the job.', 1;
    END

    UPDATE [Booking].[Bookings]
    SET [Status] = @NewStatus,
        [UpdatedAtUtc] = @Now,
        [CancelledAtUtc] = CASE
            WHEN @NewStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED') THEN @Now
            ELSE [CancelledAtUtc]
        END
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [ServiceItemCode],
           [Source],
           [CustomerName],
           [CustomerMobileCountryCode],
           [CustomerMobile],
           [AnimalType],
           [PetName],
           [ServiceLocation],
           [CustomerLocation],
           [PricePerHour],
           [JobNotes],
           [PetId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
