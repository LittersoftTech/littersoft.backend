-- Moves a night-stay booking to a new lifecycle status and writes an audit row,
-- atomically. Mirror of [Booking].[UpdateBookingStatus] — the engine behind the
-- simple "flip" transitions only (the start-with-OTP and modification flows have
-- their own sprocs). The acting party (@Actor = 'Provider' | 'Parent') and
-- @ActorId come from the authenticated route, never the client body. Enforces:
--   * the actor is a party to the booking            (THROW 51241)
--   * the status is one the actor may set            (THROW 51242)
--   * the booking is not already terminal            (THROW 51243)
--   * the status actually changes                    (THROW 51244)
--   * the transition is allowed from the current state (THROW 51246)
--   * COMPLETED requires >= 1 evidence photo         (THROW 51247)
-- Other THROWs: 51240 booking not found, 51245 invalid actor/status value.
--
-- Engine-settable per actor (other statuses are reached via dedicated sprocs):
--   Provider -> CONFIRMED (from CREATED), PROVIDER_DECLINED (from CREATED),
--               COMPLETED (from JOB_STARTED, evidence-gated), PROVIDER_CANCELLED
--   Parent   -> PARENT_CANCELLED
-- Terminal states: COMPLETED, PROVIDER_DECLINED, PROVIDER_CANCELLED, PARENT_CANCELLED.
CREATE OR ALTER PROCEDURE [Booking].[UpdateNightStayBookingStatus]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @NewStatus NVARCHAR(48),
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @Note NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Actor NOT IN (N'Provider', N'Parent')
    BEGIN
        THROW 51245, 'Actor must be Provider or Parent.', 1;
    END

    IF @NewStatus NOT IN (N'CONFIRMED', N'PROVIDER_DECLINED', N'COMPLETED',
                          N'PROVIDER_CANCELLED', N'PARENT_CANCELLED')
    BEGIN
        THROW 51245, 'Unknown or non-engine booking status.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status],
           @ProviderId = [ProviderId],
           @PetParentId = [PetParentId]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51240, 'Night stay booking was not found.', 1;
    END

    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51241, 'You are not a party to this booking.', 1;
    END

    IF (@Actor = N'Provider'
            AND @NewStatus NOT IN (N'CONFIRMED', N'PROVIDER_DECLINED', N'COMPLETED', N'PROVIDER_CANCELLED'))
       OR (@Actor = N'Parent'
            AND @NewStatus NOT IN (N'PARENT_CANCELLED'))
    BEGIN
        THROW 51242, 'This status is not permitted for this actor.', 1;
    END

    IF @CurrentStatus IN (N'COMPLETED', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED', N'PARENT_CANCELLED')
    BEGIN
        THROW 51243, 'Booking is in a terminal state and cannot change.', 1;
    END

    IF @CurrentStatus = @NewStatus
    BEGIN
        THROW 51244, 'Booking is already in the requested status.', 1;
    END

    IF (@NewStatus = N'CONFIRMED'           AND @CurrentStatus <> N'CREATED')
       OR (@NewStatus = N'PROVIDER_DECLINED' AND @CurrentStatus <> N'CREATED')
       OR (@NewStatus = N'COMPLETED'        AND @CurrentStatus <> N'JOB_STARTED')
    BEGIN
        THROW 51246, 'This transition is not allowed from the current status.', 1;
    END

    IF @NewStatus = N'COMPLETED'
       AND NOT EXISTS (SELECT 1 FROM [Booking].[NightStayBookingEvidence] WHERE [NightStayBookingId] = @NightStayBookingId)
    BEGIN
        THROW 51247, 'Upload at least one evidence photo before completing the job.', 1;
    END

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = @NewStatus,
        [UpdatedAtUtc] = @Now,
        [CancelledAtUtc] = CASE
            WHEN @NewStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED') THEN @Now
            ELSE [CancelledAtUtc]
        END
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

    SELECT [NightStayBookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [CheckInDate],
           [CheckOutDate],
           [DropOffTime],
           [PickUpTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
