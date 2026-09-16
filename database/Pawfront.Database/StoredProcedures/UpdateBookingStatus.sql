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
--   * a no-show is only reportable 30+ minutes after the scheduled start (THROW 51128)
--   * a booking left in CREATED for 24+ hours has expired, so the attempted
--     transition is rejected (THROW 51129) — the provider can no longer accept
--     it. This guard REJECTS ONLY; it does not write the EXPIRED status. Settling
--     abandoned bookings on a clock is the scheduled external job's job, and this
--     sproc no longer changes status on the basis of elapsed time.
--   * a booking still in CREATED with under 2 hours to the service has expired
--     for the same reason (THROW 51153) — also REJECT ONLY.
-- Other THROWs: 51120 booking not found, 51125 invalid actor/status value.
--
-- Engine-settable per actor (other statuses are reached via dedicated sprocs):
--   Provider -> CONFIRMED (from CREATED), PROVIDER_DECLINED (from CREATED),
--               COMPLETED (legacy /status shim, from IN_PROGRESS — ENDING
--               tolerated for legacy rows),
--               PROVIDER_CANCELLED, PARENT_NO_SHOW (from confirmed-equivalent or
--               START_JOB, 30 min after start)
--   Parent   -> PARENT_CANCELLED,
--               PROVIDER_NO_SHOW (from confirmed-equivalent or START_JOB, 30 min after start)
-- A cancel is blocked once the job is underway (IN_PROGRESS; the retired ENDING
-- kept for legacy rows) → THROW 51149.
-- Terminal states (no further change): COMPLETED, PROVIDER_DECLINED,
-- PROVIDER_CANCELLED, PARENT_CANCELLED, PARENT_NO_SHOW, PROVIDER_NO_SHOW.
--
-- A geolocation fix may be supplied, and is written only for the NO-SHOW
-- transitions — the one moment this engine handles that is being evidenced.
-- Every other status it serves (accept, decline, cancel, the legacy COMPLETED
-- shim) passes nothing and writes nothing. Both actors reach it: the provider
-- reporting PARENT_NO_SHOW and the parent reporting PROVIDER_NO_SHOW each record
-- their own position, so [CapturedByType] is simply @Actor.
CREATE OR ALTER PROCEDURE [Booking].[UpdateBookingStatus]
    @BookingId UNIQUEIDENTIFIER,
    @NewStatus NVARCHAR(48),
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @Note NVARCHAR(500) = NULL,
    -- The acting party's position. Only ever populated by the two no-show routes
    -- (and the legacy /status shim when it is used to set a no-show, which the API
    -- gates identically so that path cannot become a way to skip the capture).
    -- See [Booking].[StartBooking] for why these are defaulted to NULL.
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
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
                          N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                          N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
    BEGIN
        THROW 51125, 'Unknown or non-engine booking status.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @BookingDate DATE;
    DECLARE @StartTime TIME(0);
    DECLARE @EndTime TIME(0);
    DECLARE @CreatedAtUtc DATETIME2(7);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status],
           @ProviderId = [ProviderId],
           @PetParentId = [PetParentId],
           @BookingDate = [BookingDate],
           @StartTime = [StartTime],
           @EndTime = [EndTime],
           @CreatedAtUtc = [CreatedAtUtc]
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

    -- A booking left pending (CREATED) for 24+ hours has expired: reject the
    -- attempted transition — the provider can no longer accept it.
    -- REJECT ONLY: the EXPIRED status is deliberately NOT written here. Status
    -- changes driven by elapsed time belong to the scheduled external job, which
    -- is the single writer for them; this guard just stops a late accept from
    -- slipping through before that job runs. The row therefore stays in CREATED
    -- until the job settles it.
    IF @CurrentStatus = N'CREATED' AND @Now >= DATEADD(HOUR, 24, @CreatedAtUtc)
    BEGIN
        THROW 51129, 'Booking has expired after 24 hours awaiting provider acceptance and can no longer change.', 1;
    END

    -- BR-53: a booking still in CREATED with under 2 hours to the service has
    -- expired too — the provider is out of time to accept it, and the same
    -- cutoff (serviceStart - 2h) is the one BR-01 uses to refuse a fresh booking
    -- for that slot. REJECT ONLY, for the same reason as the guard above: the
    -- scheduled external job is the single writer of EXPIRED, so the row stays
    -- in CREATED until it runs. Without this guard the rule would only hold to
    -- the job's 5-minute granularity, and a provider could accept minutes before
    -- the service starts.
    IF @CurrentStatus = N'CREATED'
       AND DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @StartTime),
                   CAST(@BookingDate AS DATETIME2(7))) < DATEADD(HOUR, 2, @Now)
    BEGIN
        THROW 51153, 'Booking has expired: it was never accepted and the service now starts in under 2 hours.', 1;
    END

    -- The status must be one this actor is allowed to set via the engine.
    -- A no-show always names the OTHER party: the provider reports the parent's
    -- no-show, the parent reports the provider's.
    IF (@Actor = N'Provider'
            AND @NewStatus NOT IN (N'CONFIRMED', N'PROVIDER_DECLINED', N'COMPLETED', N'PROVIDER_CANCELLED', N'PARENT_NO_SHOW'))
       OR (@Actor = N'Parent'
            AND @NewStatus NOT IN (N'PARENT_CANCELLED', N'PROVIDER_NO_SHOW'))
    BEGIN
        THROW 51122, 'This status is not permitted for this actor.', 1;
    END

    -- A booking in a terminal state can't change further.
    IF @CurrentStatus IN (N'COMPLETED', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                          N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
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
       OR (@NewStatus = N'COMPLETED'     AND @CurrentStatus NOT IN (N'IN_PROGRESS', N'ENDING'))
       OR (@NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
           AND @CurrentStatus NOT IN (N'CONFIRMED',
                                      N'PROVIDER_ACCEPTED_MODIFICATION', N'PARENT_ACCEPTED_MODIFICATION',
                                      N'PROVIDER_DECLINED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                      N'START_JOB'))
    BEGIN
        THROW 51126, 'This transition is not allowed from the current status.', 1;
    END

    -- A cancel is allowed from any non-terminal state EXCEPT once the job is
    -- actively underway (IN_PROGRESS; the retired ENDING kept for legacy rows)
    -- — by then it runs to completion.
    IF @NewStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED')
       AND @CurrentStatus IN (N'IN_PROGRESS', N'ENDING')
    BEGIN
        THROW 51149, 'The job is already in progress and can no longer be cancelled.', 1;
    END

    -- A no-show can only be reported once the counterparty is actually late:
    -- 30 minutes past the booking's scheduled start (all times are UTC).
    IF @NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
    BEGIN
        DECLARE @StartsAtUtc DATETIME2(7) =
            DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @StartTime),
                    CAST(@BookingDate AS DATETIME2(7)));
        IF @Now < DATEADD(MINUTE, 30, @StartsAtUtc)
        BEGIN
            THROW 51128, 'A no-show can only be reported 30 minutes after the booking''s scheduled start.', 1;
        END
    END

    -- COMPLETED is reachable here through the legacy /status shim as well as
    -- through [Booking].[CompleteBooking], so an early finish must release the
    -- rest of the booked window from BOTH paths — otherwise which endpoint the
    -- provider happened to tap would decide whether the slot came back. Same
    -- rule and the same clamps as the dedicated sproc; see it for the reasoning.
    DECLARE @ActualEndTime TIME(0) = NULL;

    IF @NewStatus = N'COMPLETED' AND CAST(@Now AS DATE) = @BookingDate
    BEGIN
        DECLARE @NowTime TIME(0) = CAST(@Now AS TIME(0));
        IF @NowTime < @EndTime
        BEGIN
            SET @ActualEndTime = CASE WHEN @NowTime < @StartTime THEN @StartTime ELSE @NowTime END;
        END
    END

    UPDATE [Booking].[Bookings]
    SET [Status] = @NewStatus,
        [UpdatedAtUtc] = @Now,
        -- Only ever written on the COMPLETED transition; every other status
        -- leaves whatever is there alone (it is NULL for all of them anyway,
        -- since COMPLETED is terminal apart from the move to PAID).
        [ActualEndTime] = CASE
            WHEN @NewStatus = N'COMPLETED' THEN @ActualEndTime
            ELSE [ActualEndTime]
        END,
        -- A no-show ends the job with nobody having performed and nobody owing,
        -- so the payout is settled as 'NO_PAYOUT' rather than left reading
        -- 'Pending' forever. Terminal, and safe to overwrite unconditionally
        -- here: the from-state guards above only admit a no-show from a
        -- confirmed-equivalent status or START_JOB, none of which can already
        -- have been paid (PAID is only reachable from COMPLETED, and is itself
        -- terminal). EXPIRED is settled the same way by the sweep, which is its
        -- only writer.
        [PayoutStatus] = CASE
            WHEN @NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW') THEN N'NO_PAYOUT'
            ELSE [PayoutStatus]
        END,
        [CancelledAtUtc] = CASE
            WHEN @NewStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED') THEN @Now
            ELSE [CancelledAtUtc]
        END
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

    -- Where the reporting party was when they marked the counterparty absent.
    -- Written in the same transaction as the status flip: a no-show is terminal
    -- and frees capacity, so it must never be possible to have one on record with
    -- no idea where the person reporting it stood.
    IF @NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
       AND @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[BookingLocationEvents]
            ([BookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@BookingId, N'NoShowMarked', @Actor, @ActorId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    -- Notify the OTHER party, inside this transaction so the notification can
    -- never exist without the status change (or vice versa). The actor never gets
    -- one: they tapped it and saw the result — the "relevance rule".
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
        -- A no-show always names the absent party, which is what lets one
        -- template read correctly in both directions.
        DECLARE @AbsentParty NVARCHAR(32) =
            CASE @NewStatus
                WHEN N'PARENT_NO_SHOW'   THEN N'the customer'
                WHEN N'PROVIDER_NO_SHOW' THEN N'the provider'
            END;

        DECLARE @Audience NVARCHAR(16) =
            CASE WHEN @Actor = N'Provider' THEN N'PetParent' ELSE N'Provider' END;

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = 0,
            @Audience = @Audience,
            @NotificationType = @NotificationType,
            @AbsentParty = @AbsentParty;
    END

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
