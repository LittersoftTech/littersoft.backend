-- Stages a check-in/check-out change proposal on a live multi-night booking and
-- moves it to MODIFICATION_REQUEST_BY_{PARENT|PROVIDER}. Mirror of
-- [Booking].[RequestBookingModification]. THROWs: 51260 not found, 51261
-- forbidden, 51262 not in a modifiable state, 51263 a proposal is already open,
-- 51271 the modification window has closed.
--
-- Either party may open a proposal through the current check-in date.
-- Drop-off time does not shorten this date-based window (THROW 51271).
--
-- The @Acknowledged* params carry the provider's CURRENT terms as shown to and
-- confirmed by the requester when they had drifted since the stay was created;
-- they are staged here and applied on accept only. A stay adds the offering's
-- drop-off / pick-up times to the acknowledged set.
CREATE OR ALTER PROCEDURE [Booking].[RequestNightStayBookingModification]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @ProposedCheckInDate DATE,
    @ProposedCheckOutDate DATE,
    @Note NVARCHAR(500) = NULL,
    @HasAcknowledgedTerms BIT = 0,
    @AcknowledgedPricePerNight DECIMAL(10, 2) = NULL,
    @AcknowledgedCancellationPolicyHours INT = NULL,
    @AcknowledgedDropOffTime TIME(0) = NULL,
    @AcknowledgedPickUpTime TIME(0) = NULL,
    @AcknowledgedAddressLine NVARCHAR(500) = NULL,
    @AcknowledgedCity NVARCHAR(200) = NULL,
    @AcknowledgedZipCode NVARCHAR(32) = NULL,
    @AcknowledgedLatitude DECIMAL(9, 6) = NULL,
    @AcknowledgedLongitude DECIMAL(9, 6) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @CheckInDate DATE;
    DECLARE @DropOffTime TIME(0);
    -- Only used to turn the proposed check-out DATE into an instant for the
    -- notification's timezone conversion.
    DECLARE @PickUpTime TIME(0);

    IF @ProposedCheckInDate < CAST(SYSUTCDATETIME() AS DATE) OR @ProposedCheckOutDate <= @ProposedCheckInDate
        THROW 51421, 'Check-in cannot be before today and check-out must follow check-in.', 1;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @ProviderId = [ProviderId], @PetParentId = [PetParentId],
           @CheckInDate = [CheckInDate], @DropOffTime = [DropOffTime],
           @PickUpTime = [PickUpTime]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51260, 'Night stay booking was not found.', 1;
    END

    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51261, 'You are not a party to this booking.', 1;
    END

    -- Each requester gets one accepted change. Declined/expired proposals do not count.
    -- The booking row lock serializes requests and responses before reading history.
    IF EXISTS (
        SELECT 1 FROM [Booking].[NightStayBookingStatusHistory]
        WHERE [NightStayBookingId] = @NightStayBookingId
          AND ((@Actor = N'Parent' AND [ToStatus] = N'PROVIDER_ACCEPTED_MODIFICATION')
            OR (@Actor = N'Provider' AND [ToStatus] = N'PARENT_ACCEPTED_MODIFICATION')))
    BEGIN
        THROW 51991, 'This party has already had a modification accepted for this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION',
                              N'PARENT_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                              N'PARENT_DECLINED_MODIFICATION')
    BEGIN
        THROW 51262, 'A modification can only be requested on a confirmed booking.', 1;
    END

    -- Night stays retain their original check-in-day window, independent of drop-off time.
    DECLARE @RequestDeadline DATETIME2(7) = [Booking].[NightStayRequestExpiresAtUtc](@Now, @CheckInDate);
    IF @Now >= @RequestDeadline
        THROW 51271, 'A booking can no longer be modified after the check-in date.', 1;
    IF EXISTS (SELECT 1 FROM [Booking].[NightStayBookingModifications] WHERE [NightStayBookingId] = @NightStayBookingId)
    BEGIN
        THROW 51263, 'A modification request is already awaiting a response.', 1;
    END

    -- Captured so the notification's dedupe key scopes to THIS proposal, letting a
    -- later proposal on the same stay notify again.
    DECLARE @InsertedModification TABLE ([NightStayBookingModificationId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[NightStayBookingModifications]
        ([NightStayBookingId], [RequestedByActor], [RequestedByActorId],
         [ProposedCheckInDate], [ProposedCheckOutDate], [RequestNote],
         [HasAcknowledgedTerms], [AcknowledgedPricePerNight], [AcknowledgedCancellationPolicyHours],
         [AcknowledgedDropOffTime], [AcknowledgedPickUpTime],
         [AcknowledgedAddressLine], [AcknowledgedCity], [AcknowledgedZipCode],
         [AcknowledgedLatitude], [AcknowledgedLongitude], [CreatedAtUtc])
    OUTPUT inserted.[NightStayBookingModificationId] INTO @InsertedModification
    VALUES
        (@NightStayBookingId, @Actor, @ActorId, @ProposedCheckInDate, @ProposedCheckOutDate, @Note,
         ISNULL(@HasAcknowledgedTerms, 0), @AcknowledgedPricePerNight, @AcknowledgedCancellationPolicyHours,
         @AcknowledgedDropOffTime, @AcknowledgedPickUpTime,
         @AcknowledgedAddressLine, @AcknowledgedCity, @AcknowledgedZipCode,
         @AcknowledgedLatitude, @AcknowledgedLongitude, @Now);

    DECLARE @NewStatus NVARCHAR(48) =
        CASE WHEN @Actor = N'Provider' THEN N'MODIFICATION_REQUEST_BY_PROVIDER'
             ELSE N'MODIFICATION_REQUEST_BY_PARENT' END;

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = @NewStatus, [UpdatedAtUtc] = @Now
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

    -- The counterparty has to review it. Mirror of Booking.RequestBookingModification.
    DECLARE @ReqAudience NVARCHAR(16) =
        CASE WHEN @Actor = N'Provider' THEN N'PetParent' ELSE N'Provider' END;
    DECLARE @ReqType NVARCHAR(64) =
        CASE WHEN @Actor = N'Provider'
             THEN N'BOOKING_MODIFICATION_REQUESTED_BY_PROVIDER'
             ELSE N'BOOKING_MODIFICATION_REQUESTED_BY_PARENT' END;
    DECLARE @ReqDedupe NVARCHAR(64) =
        CAST((SELECT TOP 1 [NightStayBookingModificationId] FROM @InsertedModification) AS NVARCHAR(36));

    -- Both ends of the proposed stay as UTC instants, pinned to the booking's
    -- hand-over times so the renderer can localise them. A stay is proposed as a
    -- date range, so the copy's "new time" slot carries the new check-out DATE
    -- rather than a clock time — the renderer applies that for night-stay rows.
    DECLARE @ProposedStartUtc DATETIME2(0) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), COALESCE(@DropOffTime, CAST('00:00' AS TIME))),
                CAST(@ProposedCheckInDate AS DATETIME2(0)));
    DECLARE @ProposedCheckOutUtc DATETIME2(0) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), COALESCE(@PickUpTime, CAST('00:00' AS TIME))),
                CAST(@ProposedCheckOutDate AS DATETIME2(0)));

    -- How long the counterparty actually has, as the pair of readings the renderer
    -- turns into a length. Mirror of Booking.RequestBookingModification — see the
    -- note there for why a flat "24 hours" is wrong; the only difference is that a
    -- stay's review window ends at the end of the original check-in date.
    DECLARE @RequestedAtUtc DATETIME2(0) = @Now;
    DECLARE @ReviewByUtc DATETIME2(0) =
        CASE WHEN DATEADD(HOUR, 24, @Now) < @RequestDeadline
             THEN DATEADD(HOUR, 24, @Now)
             ELSE @RequestDeadline END;

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @NightStayBookingId,
        @IsNightStay = 1,
        @Audience = @ReqAudience,
        @NotificationType = @ReqType,
        @NewServiceStartUtc = @ProposedStartUtc,
        @NewCheckOutUtc = @ProposedCheckOutUtc,
        @ReviewByUtc = @ReviewByUtc,
        @RequestedAtUtc = @RequestedAtUtc,
        @DedupeSuffix = @ReqDedupe;

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
               [Booking].[NightStayRequestExpiresAtUtc]([Booking].[NightStayBookings].[CreatedAtUtc], [Booking].[NightStayBookings].[CheckInDate])
           WHEN [Booking].[NightStayBookings].[Status] IN (N'MODIFICATION_REQUEST_BY_PARENT',N'MODIFICATION_REQUEST_BY_PROVIDER') THEN
               (SELECT [Booking].[NightStayRequestExpiresAtUtc](m.[CreatedAtUtc], [Booking].[NightStayBookings].[CheckInDate])
                FROM [Booking].[NightStayBookingModifications] m WHERE m.[NightStayBookingId] = [Booking].[NightStayBookings].[NightStayBookingId])
           END AS [RequestExpiresAtUtc],
           (SELECT pay.[PaidAtUtc] FROM [Booking].[BookingPayments] pay
            WHERE pay.[BookingType]=N'NightStay' AND pay.[BookingId]=[Booking].[NightStayBookings].[NightStayBookingId]) AS [PaidAtUtc]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
