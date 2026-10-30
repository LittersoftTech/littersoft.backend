-- Provider taps "Start Job" on a single-day booking: a transition from a
-- confirmed-equivalent (live) state to START_JOB that ALSO issues the parent-facing
-- start-OTP (@NewCode, @TtlMinutes) atomically. Two gates, both on "right now" (UTC):
-- the booking's own [BookingDate] must be TODAY, and the provider must be inside
-- the accepted opening instant through its accepted closing
-- time. Later weekly edits do not change that stored working day.
-- The parent reads the issued code to the provider, who enters it via
-- [Booking].[VerifyBookingStartOtp] to move the job to IN_PROGRESS.
-- THROWs: 51131 not found, 51132 forbidden, 51133 not in a startable state,
-- 51144 not the booking's service date, 51137 outside the provider's working hours.
--
-- A CUSTOM WALK-IN ([Source] = 'Custom') takes a different path (2026-08-24): it
-- goes straight to IN_PROGRESS with no start-OTP, no service-date gate, no
-- working-hours gate and no push. Every one of those exists to protect a PARENT who
-- is waiting, and a walk-in has none — its [PetParentId] is NULL. Because the OTP is
-- readable ONLY through the parent host's ownership-filtered route, a walk-in could
-- never be verified, never reached IN_PROGRESS, and so never reached COMPLETED or
-- the provider's earnings; it sat at CONFIRMED until the no-show sweep settled it as
-- PROVIDER_NO_SHOW for work the provider had actually done. So 51144 and 51137
-- cannot fire on a walk-in, and the provider's next call is /complete, not
-- /start-job/verify.
--
-- This is also where the ARRIVAL geolocation lands. The provider answers "have you
-- arrived at the customer's location?" (ParentLocation bookings) or "has the
-- customer arrived?" (ProviderLocation bookings) and that answer is what puts them
-- on this call, so their fix is written here — inside the same transaction, so a
-- booking can never reach START_JOB with its arrival evidence lost to a separate
-- failed write. Which of the two questions was asked follows from the booking's own
-- [LocationType] and is therefore not stored again (or trusted from the client);
-- both record the single trigger 'ArrivalConfirmed'.
CREATE OR ALTER PROCEDURE [Booking].[StartBooking]
    @BookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @NewCode NVARCHAR(6),
    @TtlMinutes INT = 10,
    -- The provider's position when they confirmed arrival. Defaulted to NULL so
    -- the procedure stays callable without them (which is what lets the SQL be
    -- deployed before the API); the API itself rejects a start-job request that
    -- arrives without a usable fix, so in practice they are always supplied.
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL,
    @WaitForCustomer BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @BookingDate DATE;
    DECLARE @SlotStartTime TIME(0);
    DECLARE @OpensAt DATETIME2(7), @ClosesAt DATETIME2(7);
    DECLARE @Source NVARCHAR(16);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId], @BookingDate = [BookingDate], @SlotStartTime = [StartTime],
           @OpensAt = [WorkingHoursOpensAtUtc], @ClosesAt = [WorkingHoursClosesAtUtc],
           @Source = [Source]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51131, 'Booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51132, 'You are not the provider on this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION',
                              N'PARENT_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                              N'PARENT_DECLINED_MODIFICATION')
       AND NOT (@WaitForCustomer = 1 AND @CurrentStatus = N'START_JOB')
    BEGIN
        THROW 51133, 'Booking is not in a state the job can be started from.', 1;
    END

    -- The job can only be started on the day it is booked for. Checked before the
    -- working-hours gate so a provider who is open but looking at the wrong day
    -- gets the more specific error. Skipped for a Custom walk-in: the provider set
    -- that date themselves when recording the job, and one written up after the
    -- fact must still be completable rather than stuck forever.
    IF @Source <> N'Custom' AND @BookingDate <> CAST(@Now AS DATE)
    BEGIN
        THROW 51144, 'The job can only be started on the day the booking is scheduled for.', 1;
    END

    -- Accepted hours survive weekly edits. Legacy rows resolve the booked day,
    -- never the weekday at the time of this request.
    IF @ClosesAt IS NULL
        SELECT @OpensAt = [OpensAtUtc], @ClosesAt = [ClosesAtUtc]
        FROM [Booking].[AcceptedWorkingHours](@ProviderId, @BookingDate, @SlotStartTime);

    DECLARE @StartsAtUtc DATETIME2(7) = DATEADD(SECOND,
        DATEDIFF(SECOND, CAST('00:00' AS TIME), COALESCE(@SlotStartTime, CAST('00:00' AS TIME))),
        CAST(@BookingDate AS DATETIME2(7)));
    IF @Source <> N'Custom' AND (@Now < @OpensAt OR @Now > @ClosesAt)
        THROW 51137, 'The job can only be started within its accepted working hours.', 1;

    -- Waiting is available from the accepted opening until closing, including after
    -- the short booked slot has ended. No saved hours falls back to midnight UTC.
    IF @WaitForCustomer = 1 AND @Source = N'Custom'
        THROW 51133, 'A walk-in has no customer start-code flow.', 1;

    IF @WaitForCustomer = 1
    BEGIN
        IF @Now < @OpensAt
           OR @Now >= COALESCE(@ClosesAt, DATEADD(DAY, 1, CAST(@BookingDate AS DATETIME2(7))))
            THROW 51133, 'Waiting for the customer is only available within the accepted working hours.', 1;
    END

    -- A retry in START_JOB must not rotate the OTP or repeat arrival/push writes.
    IF @CurrentStatus <> N'START_JOB'
    BEGIN
    -- A Custom walk-in goes STRAIGHT to IN_PROGRESS, skipping START_JOB and the
    -- start-OTP entirely. The OTP is issued TO the parent and readable only through
    -- the parent host's ownership-filtered route — and a walk-in has no parent
    -- ([PetParentId] is NULL), so nobody could ever read it back. That is what used
    -- to strand these bookings at CONFIRMED forever: they never reached IN_PROGRESS,
    -- so they never reached COMPLETED, so they never reached the provider's
    -- earnings at all (this is why [PrivateJobCount] always read 0). A code the
    -- provider both issues and enters would prove nothing anyway.
    DECLARE @TargetStatus NVARCHAR(48) =
        CASE WHEN @Source = N'Custom' THEN N'IN_PROGRESS' ELSE N'START_JOB' END;

    UPDATE [Booking].[Bookings]
    SET [Status] = @TargetStatus, [UpdatedAtUtc] = @Now
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, @TargetStatus, N'Provider', @ProviderId,
         CASE WHEN @Source = N'Custom'
              THEN N'Walk-in job started by the provider (no start code: the booking has no pet parent)'
              ELSE N'Job start requested; start code issued to parent' END);

    -- Where the provider was when they confirmed arrival. Guarded rather than
    -- unconditional only so an older caller that does not pass a fix still works;
    -- the API supplies one on every request.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[BookingLocationEvents]
            ([BookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@BookingId, N'ArrivalConfirmed', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    -- Everything from here is parent-facing, so a walk-in skips it wholesale:
    -- there is no code to issue and nobody to notify (the push audience is
    -- 'PetParent', and enqueuing one against a NULL recipient would be a bug).
    IF @Source <> N'Custom'
    BEGIN
        -- Issue the start-OTP for the parent (reuse-while-valid).
        UPDATE [Booking].[BookingStartOtps]
        SET [Status] = N'Expired'
        WHERE [BookingId] = @BookingId
          AND [Status] = N'Pending'
          AND [ExpiresAtUtc] <= @Now;

        IF NOT EXISTS (
            SELECT 1 FROM [Booking].[BookingStartOtps]
            WHERE [BookingId] = @BookingId
              AND [Status] = N'Pending'
              AND [ExpiresAtUtc] > @Now)
        BEGIN
            INSERT INTO [Booking].[BookingStartOtps]
                ([BookingId], [OtpCode], [ExpiresAtUtc])
            VALUES (@BookingId, @NewCode, DATEADD(MINUTE, @TtlMinutes, @Now));
        END

        -- Tell the parent to open their start code. The provider tapped Start, so
        -- only the parent is notified. The code itself is deliberately NOT in the
        -- payload — a push is readable from a locked screen, and the whole point of
        -- the code is that the parent hands it over in person.
        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = 0,
            @Audience = N'PetParent',
            @NotificationType = N'BOOKING_START_OTP_ISSUED';
    END

    END

    IF @WaitForCustomer = 1 AND NOT EXISTS (
        SELECT 1 FROM [Booking].[BookingStatusHistory]
        WHERE [BookingId] = @BookingId AND [Note] = N'Waiting for customer')
        INSERT [Booking].[BookingStatusHistory]
            ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
        VALUES (@BookingId, N'START_JOB', N'START_JOB', N'Provider', @ProviderId, N'Waiting for customer');
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
           [PetId],
           [CashNotReceivedAtUtc], [CancellationReason],
           CAST(CASE WHEN EXISTS (SELECT 1 FROM [Booking].[BookingStatusHistory] mh WHERE mh.[BookingId] = [Booking].[Bookings].[BookingId] AND mh.[ToStatus] = N'PARENT_ACCEPTED_MODIFICATION') THEN 1 ELSE 0 END AS BIT) AS [ProviderModificationUsed],
           CASE WHEN [Booking].[Bookings].[Status] = N'CREATED' THEN
               [Booking].[RequestExpiresAtUtc]([Booking].[Bookings].[CreatedAtUtc], [Booking].[Bookings].[BookingDate], [Booking].[Bookings].[StartTime], NULL, NULL)
           WHEN [Booking].[Bookings].[Status] IN (N'MODIFICATION_REQUEST_BY_PARENT',N'MODIFICATION_REQUEST_BY_PROVIDER') THEN
               (SELECT [Booking].[RequestExpiresAtUtc](m.[CreatedAtUtc], [Booking].[Bookings].[BookingDate], [Booking].[Bookings].[StartTime], m.[ProposedBookingDate], m.[ProposedStartTime])
                FROM [Booking].[BookingModifications] m WHERE m.[BookingId] = [Booking].[Bookings].[BookingId])
           END AS [RequestExpiresAtUtc],
           (SELECT pay.[PaidAtUtc] FROM [Booking].[BookingPayments] pay
            WHERE pay.[BookingType]=N'SingleDay' AND pay.[BookingId]=[Booking].[Bookings].[BookingId]) AS [PaidAtUtc]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
