-- Stages a date/time change proposal on a live single-day booking and moves the
-- booking to MODIFICATION_REQUEST_BY_{PARENT|PROVIDER}. The proposed window's
-- validity (working hours, closures, duration) is checked by the Application
-- layer first; here we enforce party + state + the one-open-proposal rule, then
-- insert the staging row. THROWs: 51140 not found, 51141 forbidden, 51142 not in
-- a modifiable state, 51143 a proposal is already open, 51151 the modification
-- window has closed.
--
-- Requests require three hours of notice for both the original and proposed
-- start. Responses expire two hours before the earlier start or after 24 hours.
--
-- When the provider's terms have drifted since the booking was created and the
-- requester acknowledged the drift, @HasAcknowledgedTerms = 1 and the
-- @Acknowledged* params carry the CURRENT terms exactly as the requester was
-- shown them. They are staged alongside the schedule and applied by
-- [Booking].[RespondBookingModification] on accept — never here, so a declined
-- proposal leaves the booking's frozen terms untouched.
CREATE OR ALTER PROCEDURE [Booking].[RequestBookingModification]
    @BookingId UNIQUEIDENTIFIER,
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @ProposedBookingDate DATE,
    @ProposedStartTime TIME(0),
    @ProposedEndTime TIME(0),
    @Note NVARCHAR(500) = NULL,
    @HasAcknowledgedTerms BIT = 0,
    @AcknowledgedPricePerHour DECIMAL(10, 2) = NULL,
    @AcknowledgedCancellationPolicyHours INT = NULL,
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
    DECLARE @BookingDate DATE;
    DECLARE @StartTime TIME(0);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @ProviderId = [ProviderId], @PetParentId = [PetParentId],
           @BookingDate = [BookingDate], @StartTime = [StartTime]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51140, 'Booking was not found.', 1;
    END

    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51141, 'You are not a party to this booking.', 1;
    END

    -- Each requester gets one accepted change. Declined/expired proposals do not count.
    -- The booking row lock serializes requests and responses before reading history.
    IF EXISTS (
        SELECT 1 FROM [Booking].[BookingStatusHistory]
        WHERE [BookingId] = @BookingId
          AND ((@Actor = N'Parent' AND [ToStatus] = N'PROVIDER_ACCEPTED_MODIFICATION')
            OR (@Actor = N'Provider' AND [ToStatus] = N'PARENT_ACCEPTED_MODIFICATION')))
    BEGIN
        THROW 51990, 'This party has already had a modification accepted for this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION',
                              N'PARENT_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                              N'PARENT_DECLINED_MODIFICATION')
    BEGIN
        THROW 51142, 'A modification can only be requested on a confirmed booking.', 1;
    END

    -- The modification window closes 3 hours before the service starts (all
    -- times UTC), for either party's proposal.
    DECLARE @StartsAtUtc DATETIME2(7) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @StartTime),
                CAST(@BookingDate AS DATETIME2(7)));

    -- The proposal as a single UTC instant; the renderer localises it into the
    -- newServiceDate + newStartTime the copy quotes.
    DECLARE @ProposedStartUtc DATETIME2(0) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @ProposedStartTime),
                CAST(@ProposedBookingDate AS DATETIME2(0)));

    -- Both schedules must retain at least three hours of notice.
    IF @ProposedStartUtc < @StartsAtUtc SET @StartsAtUtc = @ProposedStartUtc;

    IF @Now > DATEADD(HOUR, -3, @StartsAtUtc)
    BEGIN
        THROW 51151, 'A booking can no longer be modified within 3 hours of the service start time.', 1;
    END

    IF EXISTS (SELECT 1 FROM [Booking].[BookingModifications] WHERE [BookingId] = @BookingId)
    BEGIN
        THROW 51143, 'A modification request is already awaiting a response.', 1;
    END

    -- Captured so the notification's dedupe key can be scoped to THIS proposal
    -- rather than to the booking (see the enqueue below).
    DECLARE @InsertedModification TABLE ([BookingModificationId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[BookingModifications]
        ([BookingId], [RequestedByActor], [RequestedByActorId],
         [ProposedBookingDate], [ProposedStartTime], [ProposedEndTime], [RequestNote], [CreatedAtUtc],
         [HasAcknowledgedTerms], [AcknowledgedPricePerHour], [AcknowledgedCancellationPolicyHours],
         [AcknowledgedAddressLine], [AcknowledgedCity], [AcknowledgedZipCode],
         [AcknowledgedLatitude], [AcknowledgedLongitude])
    OUTPUT inserted.[BookingModificationId] INTO @InsertedModification
    VALUES
        (@BookingId, @Actor, @ActorId, @ProposedBookingDate, @ProposedStartTime, @ProposedEndTime, @Note, @Now,
         ISNULL(@HasAcknowledgedTerms, 0), @AcknowledgedPricePerHour, @AcknowledgedCancellationPolicyHours,
         @AcknowledgedAddressLine, @AcknowledgedCity, @AcknowledgedZipCode,
         @AcknowledgedLatitude, @AcknowledgedLongitude);

    DECLARE @NewStatus NVARCHAR(48) =
        CASE WHEN @Actor = N'Provider' THEN N'MODIFICATION_REQUEST_BY_PROVIDER'
             ELSE N'MODIFICATION_REQUEST_BY_PARENT' END;

    UPDATE [Booking].[Bookings]
    SET [Status] = @NewStatus, [UpdatedAtUtc] = @Now
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

    -- The counterparty has to review it, so they are the one notified.
    DECLARE @ReqAudience NVARCHAR(16) =
        CASE WHEN @Actor = N'Provider' THEN N'PetParent' ELSE N'Provider' END;
    DECLARE @ReqType NVARCHAR(64) =
        CASE WHEN @Actor = N'Provider'
             THEN N'BOOKING_MODIFICATION_REQUESTED_BY_PROVIDER'
             ELSE N'BOOKING_MODIFICATION_REQUESTED_BY_PARENT' END;

    -- A booking can be modified more than once over its life, and each proposal
    -- is a distinct thing to review — so the dedupe key is scoped to the staging
    -- row rather than the booking, letting a later proposal notify again while
    -- still collapsing a retry of the same one.
    DECLARE @ReqDedupe NVARCHAR(64) =
        CAST((SELECT TOP 1 [BookingModificationId] FROM @InsertedModification) AS NVARCHAR(36));

    -- HOW LONG THE COUNTERPARTY ACTUALLY HAS. Two deadlines can end this proposal
    -- and [Booking].[RevertExpiredModificationRequests] enforces whichever arrives
    -- FIRST, so the notification has to quote the same one:
    --   * the 24-hour review window   — this request + 24h
    --   * the 2-hour pre-service cutoff — @StartsAtUtc - 2h
    -- Telling a provider "review within 24 hours" about a booking that starts in
    -- five hours promises a window that outlives the service; the proposal in fact
    -- dies in three. Both readings are on the SAME clock and derived from the same
    -- @Now the row is stamped with, so the span between them is exact whatever that
    -- clock turns out to be — which is what keeps this honest without dragging the
    -- product-wide UTC-vs-wall-clock question into it.
    --
    -- Sent as the PAIR, not a pre-computed span: the renderer owns user-facing copy
    -- (see NotificationDuration), and the app gets the deadline instant for a live
    -- countdown, which frozen text could never give it.
    DECLARE @RequestedAtUtc DATETIME2(0) = @Now;
    DECLARE @ReviewByUtc DATETIME2(0) =
        CASE WHEN DATEADD(HOUR, 24, @Now) < DATEADD(HOUR, -2, @StartsAtUtc)
             THEN DATEADD(HOUR, 24, @Now)
             ELSE DATEADD(HOUR, -2, @StartsAtUtc) END;

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @BookingId,
        @IsNightStay = 0,
        @Audience = @ReqAudience,
        @NotificationType = @ReqType,
        @NewServiceStartUtc = @ProposedStartUtc,
        @ReviewByUtc = @ReviewByUtc,
        @RequestedAtUtc = @RequestedAtUtc,
        @DedupeSuffix = @ReqDedupe;

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
