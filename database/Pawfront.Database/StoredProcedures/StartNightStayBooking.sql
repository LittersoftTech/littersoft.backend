-- Provider taps "Start Job" on a multi-night booking: confirmed-equivalent →
-- START_JOB, issuing the parent-facing start-OTP atomically. Mirror of
-- [Booking].[StartBooking]: the stay's [CheckInDate] (the drop-off day — the
-- night-stay analogue of a single-day booking's service date) must be TODAY, and
-- the provider must be inside their own weekly working hours
-- ([Provider].[ProviderWeeklyAvailability]). The scheduled drop-off TIME is
-- deliberately not checked.
-- THROWs: 51251 not found, 51252 forbidden, 51253 not startable,
-- 51264 not the stay's check-in date, 51257 outside the provider's working hours.
-- Also records the provider's ARRIVAL geolocation — see [Booking].[StartBooking]
-- for the reasoning; here the arrival is the drop-off hand-over on the check-in day.
CREATE OR ALTER PROCEDURE [Booking].[StartNightStayBooking]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @NewCode NVARCHAR(6),
    @TtlMinutes INT = 10,
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
    DECLARE @CheckInDate DATE;
    DECLARE @SlotStartTime TIME(0);
    DECLARE @OpensAt DATETIME2(7), @ClosesAt DATETIME2(7);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId], @CheckInDate = [CheckInDate], @SlotStartTime = [DropOffTime],
           @OpensAt = [WorkingHoursOpensAtUtc], @ClosesAt = [WorkingHoursClosesAtUtc]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51251, 'Night stay booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51252, 'You are not the provider on this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION',
                              N'PARENT_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                              N'PARENT_DECLINED_MODIFICATION')
       AND NOT (@WaitForCustomer = 1 AND @CurrentStatus = N'START_JOB')
    BEGIN
        THROW 51253, 'Booking is not in a state the job can be started from.', 1;
    END

    -- The stay can only be started on its drop-off day. Checked before the
    -- working-hours gate so a provider who is open but looking at the wrong day
    -- gets the more specific error.
    IF @CheckInDate <> CAST(@Now AS DATE)
    BEGIN
        THROW 51264, 'The job can only be started on the day the booking is scheduled for.', 1;
    END

    -- Accepted hours survive weekly edits. Legacy rows resolve the booked day,
    -- never the weekday at the time of this request.
    IF @ClosesAt IS NULL
        SELECT @OpensAt = [OpensAtUtc], @ClosesAt = [ClosesAtUtc]
        FROM [Booking].[AcceptedWorkingHours](@ProviderId, @CheckInDate, @SlotStartTime);

    DECLARE @StartsAtUtc DATETIME2(7) = DATEADD(SECOND,
        DATEDIFF(SECOND, CAST('00:00' AS TIME), COALESCE(@SlotStartTime, CAST('00:00' AS TIME))),
        CAST(@CheckInDate AS DATETIME2(7)));
    IF (@Now < @OpensAt OR @Now > @ClosesAt)
        THROW 51257, 'The job can only be started within its accepted working hours.', 1;

    -- Waiting is available from the accepted opening until closing, including after
    -- the short booked slot has ended. No saved hours falls back to midnight UTC.
    IF @WaitForCustomer = 1
    BEGIN
        IF @Now < @OpensAt
           OR @Now >= COALESCE(@ClosesAt, DATEADD(DAY, 1, CAST(@CheckInDate AS DATETIME2(7))))
            THROW 51253, 'Waiting for the customer is only available within the accepted working hours.', 1;
    END

    -- A retry in START_JOB must not rotate the OTP or repeat arrival/push writes.
    IF @CurrentStatus <> N'START_JOB'
    BEGIN
    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'START_JOB', [UpdatedAtUtc] = @Now
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, N'START_JOB', N'Provider', @ProviderId, N'Job start requested; start code issued to parent');

    -- Where the provider was when they confirmed arrival.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[NightStayBookingLocationEvents]
            ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@NightStayBookingId, N'ArrivalConfirmed', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    UPDATE [Booking].[NightStayBookingStartOtps]
    SET [Status] = N'Expired'
    WHERE [NightStayBookingId] = @NightStayBookingId
      AND [Status] = N'Pending'
      AND [ExpiresAtUtc] <= @Now;

    IF NOT EXISTS (
        SELECT 1 FROM [Booking].[NightStayBookingStartOtps]
        WHERE [NightStayBookingId] = @NightStayBookingId
          AND [Status] = N'Pending'
          AND [ExpiresAtUtc] > @Now)
    BEGIN
        INSERT INTO [Booking].[NightStayBookingStartOtps]
            ([NightStayBookingId], [OtpCode], [ExpiresAtUtc])
        VALUES (@NightStayBookingId, @NewCode, DATEADD(MINUTE, @TtlMinutes, @Now));
    END

    -- Mirror of Booking.StartBooking: the parent is told to open their code.
    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @NightStayBookingId,
        @IsNightStay = 1,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_START_OTP_ISSUED';

    END

    IF @WaitForCustomer = 1 AND NOT EXISTS (
        SELECT 1 FROM [Booking].[NightStayBookingStatusHistory]
        WHERE [NightStayBookingId] = @NightStayBookingId AND [Note] = N'Waiting for customer')
        INSERT [Booking].[NightStayBookingStatusHistory]
            ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
        VALUES (@NightStayBookingId, N'START_JOB', N'START_JOB', N'Provider', @ProviderId, N'Waiting for customer');
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
