-- Registers or refreshes the calling provider's FCM device token. Mirror of
-- Parent.SaveParentDeviceToken — see that sproc for the full rationale.
--
-- In short: an FCM token rotates (reinstall, cleared app data, restore, or
-- Firebase's own schedule) and the sign-in flow only runs at sign in, so without
-- this endpoint a rotated token is never reported and the device silently stops
-- receiving notifications.
--
-- The owner is resolved from @FirebaseUserId (the JWT's sub/user_id), never from
-- the request body.
--
-- STALE-TOKEN RETIREMENT: when @DeviceId is supplied, every OTHER active token
-- for that same physical device is deactivated — including one belonging to a
-- different account (those associations are removed) — so token rotation retires a
-- device that changes hands stops receiving the previous account's
-- notifications. With no @DeviceId nothing is retired, since we cannot tell
-- devices apart and guessing would kill the provider's other phones.
--
-- THROWs: 51004 provider auth identity not found (caller must sign in first).
CREATE OR ALTER PROCEDURE [Provider].[SaveProviderDeviceToken]
    @FirebaseUserId NVARCHAR(128),
    @FcmToken NVARCHAR(2048),
    @DeviceId NVARCHAR(200) = NULL,
    @DevicePlatform NVARCHAR(32) = NULL,
    @ReturnResult BIT = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @FcmToken = LTRIM(RTRIM(@FcmToken));
    SET @DeviceId = NULLIF(LTRIM(RTRIM(@DeviceId)), N'');
    IF NULLIF(@FcmToken, N'') IS NULL THROW 51007, 'FCM token is required.', 1;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ProviderAuthIdentityId UNIQUEIDENTIFIER;
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @RetiredCount INT = 0;

    BEGIN TRANSACTION;

    -- Serialize provider token registration with account deletion, including sign-in.
    DECLARE @TokenLockResult INT;
    EXEC @TokenLockResult = sys.sp_getapplock @Resource = N'Provider.DeviceTokenOwnership',
        @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
    IF @TokenLockResult < 0 THROW 51006, 'Device-token ownership is busy. Please retry.', 1;


    SELECT @ProviderAuthIdentityId = [ProviderAuthIdentityId],
           @ProviderId = [ProviderId]
    FROM [Provider].[ProviderAuthIdentities] WITH (UPDLOCK, HOLDLOCK)
    WHERE [FirebaseUserId] = @FirebaseUserId;

    IF @ProviderAuthIdentityId IS NULL OR EXISTS (SELECT 1 FROM [Provider].[Providers] WHERE [ProviderAuthIdentityId] = @ProviderAuthIdentityId AND [IsDeleted] = 1)
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 51004, 'Provider auth identity was not found for the supplied Firebase user.', 1;
    END

    IF @DeviceId IS NULL
        SELECT @DeviceId = [DeviceId] FROM [Provider].[ProviderDeviceTokens] WHERE [FcmToken] = @FcmToken;

    IF @DeviceId IS NOT NULL
    BEGIN
        -- A physical device must not remain associated with another account.
        DELETE FROM [Provider].[ProviderDeviceTokens]
        WHERE [DeviceId] = @DeviceId AND [FcmToken] <> @FcmToken
          AND [ProviderAuthIdentityId] <> @ProviderAuthIdentityId;
        SET @RetiredCount = @@ROWCOUNT;

        UPDATE [Provider].[ProviderDeviceTokens]
        SET [IsActive] = 0, [UpdatedAtUtc] = @Now
        WHERE [DeviceId] = @DeviceId AND [FcmToken] <> @FcmToken AND [IsActive] = 1;
        SET @RetiredCount = @RetiredCount + @@ROWCOUNT;
    END

    IF EXISTS (
        SELECT 1
        FROM [Provider].[ProviderDeviceTokens] WITH (UPDLOCK, HOLDLOCK)
        WHERE [FcmToken] = @FcmToken)
    BEGIN
        UPDATE [Provider].[ProviderDeviceTokens]
        SET [ProviderAuthIdentityId] = @ProviderAuthIdentityId,
            [ProviderId] = @ProviderId,
            [DeviceId] = COALESCE(@DeviceId, [DeviceId]),
            [DevicePlatform] = COALESCE(@DevicePlatform, [DevicePlatform]),
            [IsActive] = 1,
            [LastSeenAtUtc] = @Now,
            [UpdatedAtUtc] = @Now
        WHERE [FcmToken] = @FcmToken;
    END
    ELSE
    BEGIN
        INSERT INTO [Provider].[ProviderDeviceTokens]
        (
            [ProviderAuthIdentityId],
            [ProviderId],
            [FcmToken],
            [DeviceId],
            [DevicePlatform]
        )
        VALUES
        (
            @ProviderAuthIdentityId,
            @ProviderId,
            @FcmToken,
            @DeviceId,
            @DevicePlatform
        );
    END

    COMMIT TRANSACTION;

    IF @ReturnResult = 0 RETURN;

    SELECT [ProviderDeviceTokenId],
           [ProviderId],
           [DeviceId],
           [DevicePlatform],
           [IsActive],
           @RetiredCount AS [RetiredTokenCount],
           [LastSeenAtUtc],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Provider].[ProviderDeviceTokens]
    WHERE [FcmToken] = @FcmToken;
END
