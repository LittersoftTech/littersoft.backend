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
--   * a no-show is only reportable 2+ HOURS after check-in + drop-off (THROW 51248)
--   * a booking left in CREATED for 24+ hours has expired, so the attempted
--     transition is rejected (THROW 51249) — the provider can no longer accept
--     it. This guard REJECTS ONLY; it does not write the EXPIRED status. Settling
--     abandoned bookings on a clock is the scheduled external job's job, and this
--     sproc no longer changes status on the basis of elapsed time.
--   * a stay still in CREATED with under 2 hours to check-in + drop-off has
--     expired for the same reason (THROW 51273) — also REJECT ONLY.
-- Other THROWs: 51240 booking not found, 51245 invalid actor/status value.
--
-- Engine-settable per actor (other statuses are reached via dedicated sprocs):
--   Provider -> CONFIRMED (from CREATED), PROVIDER_DECLINED (from CREATED),
--               COMPLETED (legacy /status shim, from IN_PROGRESS — ENDING
--               tolerated for legacy rows),
--               PROVIDER_CANCELLED, PARENT_NO_SHOW (from confirmed-equivalent or
--               START_JOB, 2 h after check-in)
--   Parent   -> PARENT_CANCELLED,
--               PROVIDER_NO_SHOW (from confirmed-equivalent or START_JOB, 2 h after check-in)
-- A cancel is blocked once the job is underway (IN_PROGRESS; the retired ENDING
-- kept for legacy rows) → THROW 51269.
-- Terminal states: COMPLETED, PROVIDER_DECLINED, PROVIDER_CANCELLED, PARENT_CANCELLED,
-- PARENT_NO_SHOW, PROVIDER_NO_SHOW.
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
                          N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                          N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
    BEGIN
        THROW 51245, 'Unknown or non-engine booking status.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @CheckInDate DATE;
    DECLARE @DropOffTime TIME(0);
    DECLARE @CreatedAtUtc DATETIME2(7);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status],
           @ProviderId = [ProviderId],
           @PetParentId = [PetParentId],
           @CheckInDate = [CheckInDate],
           @DropOffTime = [DropOffTime],
           @CreatedAtUtc = [CreatedAtUtc]
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

    -- A booking left pending (CREATED) for 24+ hours has expired: reject the
    -- attempted transition — the provider can no longer accept it.
    -- REJECT ONLY: the EXPIRED status is deliberately NOT written here. Status
    -- changes driven by elapsed time belong to the scheduled external job, which
    -- is the single writer for them; this guard just stops a late accept from
    -- slipping through before that job runs. The row therefore stays in CREATED
    -- until the job settles it.
    IF @CurrentStatus = N'CREATED' AND @Now >= DATEADD(HOUR, 24, @CreatedAtUtc)
    BEGIN
        THROW 51249, 'Booking has expired after 24 hours awaiting provider acceptance and can no longer change.', 1;
    END

    -- BR-53 (mirror of the single-day 51153 guard): a stay still in CREATED with
    -- under 2 hours to serviceStart — CheckInDate + DropOffTime for a stay — has
    -- expired; the provider is out of time to accept it. REJECT ONLY: the
    -- scheduled external job is the single writer of EXPIRED, so the row stays in
    -- CREATED until it runs.
    IF @CurrentStatus = N'CREATED'
       AND DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @DropOffTime),
                   CAST(@CheckInDate AS DATETIME2(7))) < DATEADD(HOUR, 2, @Now)
    BEGIN
        THROW 51273, 'Booking has expired: it was never accepted and the stay now begins in under 2 hours.', 1;
    END

    -- A no-show always names the OTHER party: the provider reports the parent's
    -- no-show, the parent reports the provider's.
    IF (@Actor = N'Provider'
            AND @NewStatus NOT IN (N'CONFIRMED', N'PROVIDER_DECLINED', N'COMPLETED', N'PROVIDER_CANCELLED', N'PARENT_NO_SHOW'))
       OR (@Actor = N'Parent'
            AND @NewStatus NOT IN (N'PARENT_CANCELLED', N'PROVIDER_NO_SHOW'))
    BEGIN
        THROW 51242, 'This status is not permitted for this actor.', 1;
    END

    IF @CurrentStatus IN (N'COMPLETED', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                          N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
    BEGIN
        THROW 51243, 'Booking is in a terminal state and cannot change.', 1;
    END

    IF @CurrentStatus = @NewStatus
    BEGIN
        THROW 51244, 'Booking is already in the requested status.', 1;
    END

    IF (@NewStatus = N'CONFIRMED'           AND @CurrentStatus <> N'CREATED')
       OR (@NewStatus = N'PROVIDER_DECLINED' AND @CurrentStatus <> N'CREATED')
       OR (@NewStatus = N'COMPLETED'        AND @CurrentStatus NOT IN (N'IN_PROGRESS', N'ENDING'))
       OR (@NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
           AND @CurrentStatus NOT IN (N'CONFIRMED',
                                      N'PROVIDER_ACCEPTED_MODIFICATION', N'PARENT_ACCEPTED_MODIFICATION',
                                      N'PROVIDER_DECLINED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                      N'START_JOB'))
    BEGIN
        THROW 51246, 'This transition is not allowed from the current status.', 1;
    END

    -- A cancel is allowed from any non-terminal state EXCEPT once the job is
    -- actively underway (IN_PROGRESS; the retired ENDING kept for legacy rows)
    -- — by then it runs to completion.
    IF @NewStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED')
       AND @CurrentStatus IN (N'IN_PROGRESS', N'ENDING')
    BEGIN
        THROW 51269, 'The job is already in progress and can no longer be cancelled.', 1;
    END

    -- A no-show can only be reported once the counterparty is actually late:
    -- 2 HOURS past the stay's scheduled check-in (check-in date + drop-off
    -- time; all times are UTC). A boarding hand-over is a slower affair than a
    -- single-day appointment, whose gate stays at 30 minutes — so a 09:00
    -- check-in is reportable from 11:00.
    --
    -- Neither party has to wait it out: if the stay is still unstarted when the
    -- check-in day ends, the scheduled external job settles it automatically at
    -- midnight UTC (START_JOB -> PARENT_NO_SHOW, since the provider was there
    -- and issued the code; anything else -> PROVIDER_NO_SHOW, since they never
    -- even tapped Start). That settlement no longer happens in this database.
    IF @NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
    BEGIN
        DECLARE @StartsAtUtc DATETIME2(7) =
            DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @DropOffTime),
                    CAST(@CheckInDate AS DATETIME2(7)));
        IF @Now < DATEADD(HOUR, 2, @StartsAtUtc)
        BEGIN
            THROW 51248, 'A no-show can only be reported 2 hours after the stay''s scheduled check-in.', 1;
        END
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

    -- Mirror of Booking.UpdateBookingStatus: notify the OTHER party, in this
    -- transaction, never the actor who tapped it.
    DECLARE @NotificationType NVARCHAR(64) =
        CASE @NewStatus
            WHEN N'CONFIRMED'          THEN N'BOOKING_ACCEPTED'
            WHEN N'PROVIDER_DECLINED'  THEN N'BOOKING_DECLINED'
            WHEN N'PROVIDER_CANCELLED' THEN N'BOOKING_CANCELLED_BY_PROVIDER'
            WHEN N'PARENT_CANCELLED'   THEN N'BOOKING_CANCELLED_BY_PARENT'
            WHEN N'COMPLETED'          THEN N'BOOKING_COMPLETED'
            WHEN N'PARENT_NO_SHOW'     THEN N'BOOKING_NO_SHOW_REPORTED'
            WHEN N'PROVIDER_NO_SHOW'   THEN N'BOOKING_NO_SHOW_REPORTED'
        END;

    IF @NotificationType IS NOT NULL
    BEGIN
        DECLARE @AbsentParty NVARCHAR(32) =
            CASE @NewStatus
                WHEN N'PARENT_NO_SHOW'   THEN N'the customer'
                WHEN N'PROVIDER_NO_SHOW' THEN N'the provider'
            END;

        DECLARE @Audience NVARCHAR(16) =
            CASE WHEN @Actor = N'Provider' THEN N'PetParent' ELSE N'Provider' END;

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @NightStayBookingId,
            @IsNightStay = 1,
            @Audience = @Audience,
            @NotificationType = @NotificationType,
            @AbsentParty = @AbsentParty;
    END

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
