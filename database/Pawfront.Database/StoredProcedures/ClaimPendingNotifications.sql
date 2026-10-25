-- Claims a batch of notifications for dispatch and returns everything the
-- dispatcher needs in ONE round-trip.
--
-- Result set 1: the claimed notification rows.
-- Result set 2: the active FCM device tokens for the recipients in result set 1
--               (distinct per token) — pre-joined so the dispatcher never has to
--               issue an N+1 lookup per notification.
--
-- Claiming flips [Status] to 'Sending' and pushes [NextAttemptAtUtc] forward by
-- @LeaseMinutes. That doubles as a lease: a dispatcher that crashes mid-send
-- releases its rows automatically once the lease lapses, because the claim
-- predicate re-admits 'Sending' rows whose lease has expired. Combined with
-- READPAST this stays correct even if two ticks ever overlap.
CREATE OR ALTER PROCEDURE [Notification].[ClaimPendingNotifications]
    @BatchSize INT = 100,
    @MaxAttempts INT = 5,
    @LeaseMinutes INT = 5
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @BatchSize IS NULL OR @BatchSize < 1 SET @BatchSize = 100;
    IF @BatchSize > 500 SET @BatchSize = 500;
    IF @MaxAttempts IS NULL OR @MaxAttempts < 1 SET @MaxAttempts = 5;
    IF @LeaseMinutes IS NULL OR @LeaseMinutes < 1 SET @LeaseMinutes = 5;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    -- Re-check Events V3 relevance before the first delivery. A delayed notice
    -- must not describe a booking that was cancelled, a seat already filled, or
    -- an event that has since ended. Retired rows never enter either inbox.
    UPDATE n SET Status=N'Failed',UpdatedAtUtc=@Now,LastError=N'Superseded event notification.'
    FROM [Notification].[NotificationOutbox] n
    LEFT JOIN [Event].[Events] e ON e.EventId=TRY_CONVERT(UNIQUEIDENTIFIER,JSON_VALUE(CASE WHEN ISJSON(n.DataJson)=1 THEN n.DataJson ELSE N'{}' END,'$.eventId'))
    LEFT JOIN [Event].[EventBookings] b ON b.BookingId=TRY_CONVERT(UNIQUEIDENTIFIER,JSON_VALUE(CASE WHEN ISJSON(n.DataJson)=1 THEN n.DataJson ELSE N'{}' END,'$.eventBookingId'))
    OUTER APPLY (SELECT ISNULL(SUM(TicketCount),0) AS Reserved FROM [Event].[EventBookings]
        WHERE EventId=e.EventId AND Status IN (N'Confirmed',N'Attended')) seats
    WHERE n.Status=N'Pending' AND n.AttemptCount=0 AND n.NotificationType LIKE N'EVENT[_]%'
      AND (
        n.NotificationType IN (N'EVENT_TICKET_SOLD',N'EVENT_TICKET_CANCELLED',N'EVENT_BOOKING_CONFIRMED',N'EVENT_BOOKING_CANCELLED',N'EVENT_BOOKING_PAYMENT_SUCCEEDED',N'EVENT_BOOKING_PAYMENT_FAILED')
        OR (e.EventStatus<>N'Active' AND n.NotificationType NOT IN (N'EVENT_CANCELLED',N'EVENT_ISSUE_RESOLVED',N'EVENT_PROFILE_FLAGGED',N'EVENT_PROFILE_FLAG_REMOVED'))
        OR (n.NotificationType IN (N'EVENT_FIRST_BOOKING',N'EVENT_REMINDER_DAY_BEFORE',N'EVENT_REMINDER_STARTING_SOON') AND b.Status NOT IN (N'Confirmed',N'Attended'))
        OR (n.NotificationType=N'EVENT_EDITABLE_AGAIN' AND seats.Reserved>0)
        OR (n.NotificationType=N'EVENT_SOLD_OUT' AND seats.Reserved<e.MaximumCapacity)
        OR (n.NotificationType IN (N'EVENT_REMINDER_DAY_BEFORE',N'EVENT_HOST_REMINDER_DAY_BEFORE') AND @Now>=DATEADD(hour,-1,[Event].[EventStartUtc](e.StartDate,e.StartTime)))
        OR (n.NotificationType IN (N'EVENT_REMINDER_STARTING_SOON',N'EVENT_HOST_REMINDER_STARTING_SOON',N'EVENT_SEATS_AVAILABLE') AND @Now>=CASE WHEN n.NotificationType=N'EVENT_SEATS_AVAILABLE' THEN DATEADD(hour,-1,[Event].[EventStartUtc](e.StartDate,e.StartTime)) ELSE [Event].[EventStartUtc](e.StartDate,e.StartTime) END)
        OR (n.NotificationType=N'EVENT_SEATS_AVAILABLE' AND e.MaximumCapacity IS NOT NULL AND seats.Reserved>=e.MaximumCapacity)
        OR (n.NotificationType=N'EVENT_SEATS_AVAILABLE' AND EXISTS (SELECT 1 FROM [Event].[EventBookings] held
            WHERE held.EventId=e.EventId AND held.BookerAccountId=n.RecipientId AND held.Status IN (N'Confirmed',N'Attended')))
        OR (n.NotificationType IN (N'EVENT_HOST_REMINDER_DAY_BEFORE',N'EVENT_HOST_REMINDER_STARTING_SOON') AND seats.Reserved=0)
        OR (n.NotificationType IN (N'EVENT_LIVE',N'EVENT_LIVE_FREE') AND @Now>=[Event].[EventStartUtc](e.EndDate,e.EndTime))
        OR (n.NotificationType=N'EVENT_RECORD_REMAINING' AND @Now>=[Event].[EventStartUtc](DATEADD(day,1,e.EndDate),'00:00'))
        OR (n.NotificationType=N'EVENT_RECORD_REMAINING' AND NOT EXISTS (
            SELECT 1 FROM [Event].[EventBookingTickets] t JOIN [Event].[EventBookings] pending ON pending.BookingId=t.BookingId
            WHERE pending.EventId=e.EventId AND pending.Status IN (N'Confirmed',N'Attended') AND t.PaymentStatus=N'Pending'))
        OR (n.NotificationType=N'EVENT_PROFILE_FLAGGED' AND NOT EXISTS (SELECT 1 FROM [Event].[Reliability] r WHERE r.AccountId=n.RecipientId AND r.RedFlagExpiresAt>@Now))
      );

    -- Retire rows that exhausted their attempts while claimed — a dispatcher
    -- that crashed on the final attempt would otherwise sit in 'Sending'
    -- forever, since the claim predicate below excludes it.
    UPDATE [Notification].[NotificationOutbox]
    SET [Status] = N'Failed',
        [UpdatedAtUtc] = @Now,
        [LastError] = COALESCE([LastError], N'Dispatch lease expired after the final attempt.')
    WHERE [Status] = N'Sending'
      AND [AttemptCount] >= @MaxAttempts
      AND [NextAttemptAtUtc] <= @Now;

    DECLARE @Claimed TABLE
    (
        [NotificationId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        [Audience] NVARCHAR(16) NOT NULL,
        [RecipientId] UNIQUEIDENTIFIER NOT NULL,
        [NotificationType] NVARCHAR(64) NOT NULL,
        [EntityType] NVARCHAR(32) NULL,
        [EntityId] UNIQUEIDENTIFIER NULL,
        [DataJson] NVARCHAR(MAX) NULL,
        [ImageUrl] NVARCHAR(1000) NULL,
        [AttemptCount] INT NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
    );

    WITH [candidates] AS
    (
        SELECT TOP (@BatchSize) *
        FROM [Notification].[NotificationOutbox] WITH (READPAST, UPDLOCK, ROWLOCK)
        WHERE [Status] IN (N'Pending', N'Sending')
          AND [NextAttemptAtUtc] <= @Now
          AND [AttemptCount] < @MaxAttempts
        ORDER BY [CreatedAtUtc] ASC
    )
    UPDATE [candidates]
    SET [Status] = N'Sending',
        [AttemptCount] = [AttemptCount] + 1,
        [NextAttemptAtUtc] = DATEADD(MINUTE, @LeaseMinutes, @Now),
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[NotificationId],
           inserted.[Audience],
           inserted.[RecipientId],
           inserted.[NotificationType],
           inserted.[EntityType],
           inserted.[EntityId],
           inserted.[DataJson],
           inserted.[ImageUrl],
           inserted.[AttemptCount],
           inserted.[CreatedAtUtc]
    INTO @Claimed;

    -- Result set 1 — the work.
    SELECT c.[NotificationId],
           c.[Audience],
           c.[RecipientId],
           c.[NotificationType],
           c.[EntityType],
           c.[EntityId],
           c.[DataJson],
           c.[ImageUrl],
           c.[AttemptCount],
           c.[CreatedAtUtc],
           COALESCE(pa.[FirebaseUserId], pr.[FirebaseUserId]) AS [FirebaseUserId]
    FROM @Claimed c
    LEFT JOIN [Parent].[ParentAuthIdentities] pa
        ON c.[Audience] = N'PetParent' AND pa.[PetParentId] = c.[RecipientId]
    LEFT JOIN [Provider].[ProviderAuthIdentities] pr
        ON c.[Audience] = N'Provider' AND pr.[ProviderId] = c.[RecipientId]
    ORDER BY c.[CreatedAtUtc] ASC;

    -- Result set 2 — the devices to push to. DISTINCT because several claimed
    -- notifications commonly share one recipient.
    SELECT DISTINCT
           N'Provider' AS [Audience],
           t.[ProviderId] AS [RecipientId],
           t.[FcmToken],
           t.[DevicePlatform]
    FROM [Provider].[ProviderDeviceTokens] t
    INNER JOIN @Claimed c
        ON c.[Audience] = N'Provider'
       AND c.[RecipientId] = t.[ProviderId]
    WHERE t.[IsActive] = 1
      AND t.[ProviderId] IS NOT NULL

    UNION ALL

    SELECT DISTINCT
           N'PetParent' AS [Audience],
           t.[PetParentId] AS [RecipientId],
           t.[FcmToken],
           t.[DevicePlatform]
    FROM [Parent].[ParentDeviceTokens] t
    INNER JOIN @Claimed c
        ON c.[Audience] = N'PetParent'
       AND c.[RecipientId] = t.[PetParentId]
    WHERE t.[IsActive] = 1
      AND t.[PetParentId] IS NOT NULL;
END
