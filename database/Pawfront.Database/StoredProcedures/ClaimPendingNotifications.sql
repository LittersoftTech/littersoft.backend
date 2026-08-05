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
    SELECT [NotificationId],
           [Audience],
           [RecipientId],
           [NotificationType],
           [EntityType],
           [EntityId],
           [DataJson],
           [ImageUrl],
           [AttemptCount],
           [CreatedAtUtc]
    FROM @Claimed
    ORDER BY [CreatedAtUtc] ASC;

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
