-- Moves a night-stay booking to a new lifecycle status and writes an audit row,
-- atomically. Mirror of [Booking].[UpdateBookingStatus]. The acting party
-- (@Actor = 'Provider' | 'Parent') and @ActorId come from the authenticated
-- route, never the client body. Enforces:
--   * the actor is a party to the booking            (THROW 51241)
--   * the status is one the actor may set            (THROW 51242)
--   * the booking is not already terminal            (THROW 51243)
--   * the status actually changes                    (THROW 51244)
-- Other THROWs: 51240 booking not found, 51245 invalid actor/status value.
--
-- Settable per actor:
--   Provider -> CONFIRMED, COMPLETED, APPROVAL_NEEDED, PROVIDER_CANCELLED
--   Parent   -> APPROVAL_NEEDED, COMPLETED, PARENT_CANCELLED
-- Terminal states (no further change): COMPLETED, PROVIDER_CANCELLED, PARENT_CANCELLED.
CREATE OR ALTER PROCEDURE [Booking].[UpdateNightStayBookingStatus]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @NewStatus NVARCHAR(32),
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

    IF @NewStatus NOT IN (N'CREATED', N'CONFIRMED', N'COMPLETED', N'APPROVAL_NEEDED',
                          N'PROVIDER_CANCELLED', N'PARENT_CANCELLED')
    BEGIN
        THROW 51245, 'Unknown booking status.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(32);
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
            AND @NewStatus NOT IN (N'CONFIRMED', N'COMPLETED', N'APPROVAL_NEEDED', N'PROVIDER_CANCELLED'))
       OR (@Actor = N'Parent'
            AND @NewStatus NOT IN (N'APPROVAL_NEEDED', N'COMPLETED', N'PARENT_CANCELLED'))
    BEGIN
        THROW 51242, 'This status is not permitted for this actor.', 1;
    END

    IF @CurrentStatus IN (N'COMPLETED', N'PROVIDER_CANCELLED', N'PARENT_CANCELLED')
    BEGIN
        THROW 51243, 'Booking is in a terminal state and cannot change.', 1;
    END

    IF @CurrentStatus = @NewStatus
    BEGIN
        THROW 51244, 'Booking is already in the requested status.', 1;
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
