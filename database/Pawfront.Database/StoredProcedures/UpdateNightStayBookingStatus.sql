-- Moves a night-stay booking to a new lifecycle status and writes an audit row,
-- atomically. Mirror of [Booking].[UpdateBookingStatus] — the engine behind the
-- simple "flip" transitions only (the start-with-OTP and modification flows have
-- their own sprocs). The acting party (@Actor = 'Provider' | 'Parent') and
-- @ActorId come from the authenticated route, never the client body. Enforces:
--   * the actor is a party to the booking            (THROW 51241)
--   * the status is one the actor may set            (THROW 51242)
--   * finished jobs only allow COMPLETED -> PAYMENT_NOT_RECEIVED (THROW 51243)
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
-- COMPLETED may move to PAYMENT_NOT_RECEIVED. Both allow payment through the
-- dedicated MarkNightStayBookingPaid procedure; other terminal states reject this engine.
-- A geolocation fix may be supplied and is written only for the NO-SHOW
-- transitions — see [Booking].[UpdateBookingStatus].
CREATE OR ALTER PROCEDURE [Booking].[UpdateNightStayBookingStatus]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @NewStatus NVARCHAR(48),
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @Note NVARCHAR(500) = NULL,
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL,
    @NoShowReason NVARCHAR(32) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @NoShowReason IS NOT NULL AND (@NoShowReason NOT IN (N'NotArrived',N'OtpNotShared')
        OR @Actor<>N'Provider' OR @NewStatus<>N'PARENT_NO_SHOW')
        THROW 51215, 'No-show reason is only valid on a provider report of customer absence.', 1;


    IF @Actor NOT IN (N'Provider', N'Parent')
    BEGIN
        THROW 51245, 'Actor must be Provider or Parent.', 1;
    END

    IF @NewStatus NOT IN (N'CONFIRMED', N'PROVIDER_DECLINED', N'COMPLETED', N'PAYMENT_NOT_RECEIVED',
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
    DECLARE @CheckOutDate DATE;
    DECLARE @DropOffTime TIME(0);
    DECLARE @CreatedAtUtc DATETIME2(7);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status],
           @ProviderId = [ProviderId],
           @PetParentId = [PetParentId],
           @CheckInDate = [CheckInDate],
           @CheckOutDate = [CheckOutDate],
           @DropOffTime = [DropOffTime],
           @CreatedAtUtc = [CreatedAtUtc]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    SET @Now = SYSUTCDATETIME(); -- Capture report time after acquiring the booking lock.

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

    -- Night stays remain acceptable through the check-in date. The expiry
    -- sweep owns the status change; this guard rejects a response after that date.
    IF @CurrentStatus = N'CREATED'
       AND @Now >= [Booking].[RequestExpiresAtUtc](@CreatedAtUtc, @CheckInDate, COALESCE(@DropOffTime,CAST('00:00' AS TIME)), NULL, NULL)
    BEGIN
        THROW 51273, 'Booking has expired: the acceptance deadline has passed.', 1;
    END

    -- A no-show always names the OTHER party: the provider reports the parent's
    -- no-show, the parent reports the provider's.
    IF (@Actor = N'Provider'
            AND @NewStatus NOT IN (N'CONFIRMED', N'PROVIDER_DECLINED', N'COMPLETED', N'PAYMENT_NOT_RECEIVED', N'PROVIDER_CANCELLED', N'PARENT_NO_SHOW'))
       OR (@Actor = N'Parent'
            AND @NewStatus NOT IN (N'PARENT_CANCELLED', N'PROVIDER_NO_SHOW'))
    BEGIN
        THROW 51242, 'This status is not permitted for this actor.', 1;
    END

    IF @CurrentStatus IN (N'COMPLETED', N'PAYMENT_NOT_RECEIVED', N'PAID', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                          N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'JOB_INCOMPLETE', N'OTP_MAX_ATTEMPTS_EXCEEDED')
       AND NOT (@CurrentStatus IN (N'COMPLETED', N'PAYMENT_NOT_RECEIVED') AND @NewStatus = N'PAYMENT_NOT_RECEIVED')
    BEGIN
        THROW 51243, 'Booking is in a terminal state and cannot change.', 1;
    END

    IF @CurrentStatus = @NewStatus AND @NewStatus <> N'PAYMENT_NOT_RECEIVED'
    BEGIN
        THROW 51244, 'Booking is already in the requested status.', 1;
    END

    IF (@NewStatus = N'CONFIRMED'           AND @CurrentStatus <> N'CREATED')
       OR (@NewStatus = N'PROVIDER_DECLINED' AND @CurrentStatus <> N'CREATED')
       OR (@NewStatus = N'PAYMENT_NOT_RECEIVED' AND @CurrentStatus NOT IN (N'COMPLETED', N'PAYMENT_NOT_RECEIVED'))
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
    -- single-day appointment, whose gate is its scheduled end — so a 09:00
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
            DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), COALESCE(@DropOffTime, CAST('00:00' AS TIME))),
                    CAST(@CheckInDate AS DATETIME2(7)));
        IF @Now < DATEADD(HOUR, 2, @StartsAtUtc)
        BEGIN
            THROW 51248, 'A no-show can only be reported 2 hours after the stay''s scheduled check-in.', 1;
        END
    END

    -- COMPLETED is reachable here through the legacy /status shim as well as
    -- through [Booking].[CompleteNightStayBooking], so an early pickup must
    -- release the remaining nights from BOTH paths — otherwise which endpoint
    -- the provider happened to tap would decide whether the nights came back.
    -- Same rule and clamps as the dedicated sproc; see it for the reasoning.
    DECLARE @Today DATE = CAST(@Now AS DATE);
    DECLARE @ActualCheckOutDate DATE = NULL;

    IF @NewStatus = N'COMPLETED' AND @Today < @CheckOutDate
    BEGIN
        SET @ActualCheckOutDate =
            CASE WHEN @Today <= @CheckInDate THEN DATEADD(DAY, 1, @CheckInDate) ELSE @Today END;
    END

    -- Capture on acceptance, in the same transaction as the status change.
    IF @NewStatus = N'CONFIRMED'
        UPDATE b
        SET [WorkingHoursOpensAtUtc] = w.[OpensAtUtc],
            [WorkingHoursClosesAtUtc] = w.[ClosesAtUtc]
        FROM [Booking].[NightStayBookings] b
        CROSS APPLY [Booking].[AcceptedWorkingHours](b.[ProviderId], b.[CheckInDate], b.[DropOffTime]) w
        WHERE b.[NightStayBookingId] = @NightStayBookingId;

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = @NewStatus,
        [CashNotReceivedAtUtc] = CASE WHEN @NewStatus = N'PAYMENT_NOT_RECEIVED' THEN @Now ELSE [CashNotReceivedAtUtc] END,
        [UpdatedAtUtc] = @Now,
        -- Only ever written on the COMPLETED transition; every other status
        -- leaves whatever is there alone.
        [ActualCheckOutDate] = CASE
            WHEN @NewStatus = N'COMPLETED' THEN @ActualCheckOutDate
            ELSE [ActualCheckOutDate]
        END,
        -- Mirror of Booking.UpdateBookingStatus: a no-show settles the payout as
        -- 'NO_PAYOUT' instead of leaving it reading 'Pending' forever.
        [PayoutStatus] = CASE
            WHEN @NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW') THEN N'NO_PAYOUT'
            ELSE [PayoutStatus]
        END,
        [CancelledAtUtc] = CASE
            WHEN @NewStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED') THEN @Now
            ELSE [CancelledAtUtc]
        END
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

    -- Where the reporting party was when they marked the counterparty absent.
    IF @NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
       AND @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[NightStayBookingLocationEvents]
            ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@NightStayBookingId, N'NoShowMarked', @Actor, @ActorId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    IF @NewStatus = N'PAYMENT_NOT_RECEIVED' AND @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[NightStayBookingLocationEvents]
            ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@NightStayBookingId, N'CashNotReceived', N'Provider', @ActorId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    -- Mirror of Booking.UpdateBookingStatus: notify the OTHER party, in this
    -- transaction, never the actor who tapped it.
    DECLARE @NotificationType NVARCHAR(64) =
        CASE @NewStatus
            WHEN N'CONFIRMED'          THEN N'BOOKING_ACCEPTED'
            WHEN N'PROVIDER_DECLINED'  THEN N'BOOKING_DECLINED'
            WHEN N'PROVIDER_CANCELLED' THEN N'BOOKING_CANCELLED_BY_PROVIDER'
            WHEN N'PARENT_CANCELLED'   THEN N'BOOKING_CANCELLED_BY_PARENT'
            WHEN N'COMPLETED'          THEN N'BOOKING_COMPLETED'
            WHEN N'PAYMENT_NOT_RECEIVED' THEN N'BOOKING_CASH_NOT_RECEIVED'
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

        DECLARE @ResolvedNoShowReason NVARCHAR(32) = COALESCE(@NoShowReason, CASE
            WHEN @NewStatus=N'PARENT_NO_SHOW' AND @CurrentStatus=N'START_JOB'
                 AND NOT EXISTS (SELECT 1 FROM [Booking].[NightStayBookingStatusHistory]
                     WHERE [NightStayBookingId]=@NightStayBookingId AND [Note]=N'Waiting for customer')
            THEN N'OtpNotShared' ELSE N'NotArrived' END);

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @NightStayBookingId,
            @IsNightStay = 1,
            @Audience = @Audience,
            @NotificationType = @NotificationType,
            @AbsentParty = @AbsentParty,
            @NoShowReason = @ResolvedNoShowReason;
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
           [PetId],
           [CashNotReceivedAtUtc], [CancellationReason],
           CAST(CASE WHEN EXISTS (SELECT 1 FROM [Booking].[NightStayBookingStatusHistory] mh WHERE mh.[NightStayBookingId] = [Booking].[NightStayBookings].[NightStayBookingId] AND mh.[ToStatus] = N'PARENT_ACCEPTED_MODIFICATION') THEN 1 ELSE 0 END AS BIT) AS [ProviderModificationUsed],
           CASE WHEN [Booking].[NightStayBookings].[Status] = N'CREATED' THEN
               [Booking].[RequestExpiresAtUtc]([Booking].[NightStayBookings].[CreatedAtUtc], [Booking].[NightStayBookings].[CheckInDate], COALESCE([Booking].[NightStayBookings].[DropOffTime],CAST('00:00' AS TIME)), NULL, NULL)
           WHEN [Booking].[NightStayBookings].[Status] IN (N'MODIFICATION_REQUEST_BY_PARENT',N'MODIFICATION_REQUEST_BY_PROVIDER') THEN
               (SELECT [Booking].[RequestExpiresAtUtc](m.[CreatedAtUtc], [Booking].[NightStayBookings].[CheckInDate], COALESCE([Booking].[NightStayBookings].[DropOffTime],CAST('00:00' AS TIME)), m.[ProposedCheckInDate], CASE WHEN m.[RequestedByActor]=N'Parent' AND m.[HasAcknowledgedTerms]=1 THEN COALESCE(m.[AcknowledgedDropOffTime],CAST('00:00' AS TIME)) ELSE COALESCE([Booking].[NightStayBookings].[DropOffTime],CAST('00:00' AS TIME)) END)
                FROM [Booking].[NightStayBookingModifications] m WHERE m.[NightStayBookingId] = [Booking].[NightStayBookings].[NightStayBookingId])
           END AS [RequestExpiresAtUtc],
           (SELECT pay.[PaidAtUtc] FROM [Booking].[BookingPayments] pay
            WHERE pay.[BookingType]=N'NightStay' AND pay.[BookingId]=[Booking].[NightStayBookings].[NightStayBookingId]) AS [PaidAtUtc]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
