-- Registers or refreshes the calling pet parent's FCM device token.
--
-- WHY THIS EXISTS SEPARATELY FROM Parent.SaveParentAuthIdentity: an FCM token is
-- not stable. It rotates on reinstall, on app data being cleared, on some
-- restores, and whenever Firebase decides to. The sign-in flow only runs at sign
-- in, so without a dedicated endpoint a rotated token is never reported and the
-- device silently stops receiving notifications.
--
-- The owner is resolved from @FirebaseUserId (the JWT's sub/user_id), never from
-- the request body, so a caller can only ever register a token against
-- themselves.
--
-- STALE-TOKEN RETIREMENT: when @DeviceId is supplied, every OTHER active token
-- for that same physical device is deactivated — including one belonging to a
-- different account. One device holds one live token per app, so:
--   * a reinstall retires the pre-reinstall token immediately, instead of waiting
--     for FCM to report UNREGISTERED on the next send;
--   * if a second person signs in on the same handset, the previous account stops
--     receiving notifications there, rather than leaking them to whoever now
--     holds the device.
-- With no @DeviceId we cannot tell devices apart, so nothing is retired —
-- guessing would silently kill the user's other phones.
--
-- THROWs: 51225 parent auth identity not found (caller must sign in first).
CREATE OR ALTER PROCEDURE [Parent].[SaveParentDeviceToken]
    @FirebaseUserId NVARCHAR(128),
    @FcmToken NVARCHAR(2048),
    @DeviceId NVARCHAR(200) = NULL,
    @DevicePlatform NVARCHAR(32) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ParentAuthIdentityId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @RetiredCount INT = 0;

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK so a concurrent sign-in on the same identity serialises
    -- behind this rather than racing the upsert below.
    SELECT @ParentAuthIdentityId = [ParentAuthIdentityId],
           @PetParentId = [PetParentId]
    FROM [Parent].[ParentAuthIdentities] WITH (UPDLOCK, HOLDLOCK)
    WHERE [FirebaseUserId] = @FirebaseUserId;

    IF @ParentAuthIdentityId IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 51225, 'Pet parent auth identity was not found for the supplied Firebase user.', 1;
    END

    IF @DeviceId IS NOT NULL AND LEN(LTRIM(RTRIM(@DeviceId))) > 0
    BEGIN
        UPDATE [Parent].[ParentDeviceTokens]
        SET [IsActive] = 0,
            [UpdatedAtUtc] = @Now
        WHERE [DeviceId] = @DeviceId
          AND [FcmToken] <> @FcmToken
          AND [IsActive] = 1;

        SET @RetiredCount = @@ROWCOUNT;
    END

    IF EXISTS (
        SELECT 1
        FROM [Parent].[ParentDeviceTokens] WITH (UPDLOCK, HOLDLOCK)
        WHERE [FcmToken] = @FcmToken)
    BEGIN
        -- Re-registering an existing token: reassign it to this identity. A
        -- token can legitimately move between accounts on a shared device.
        UPDATE [Parent].[ParentDeviceTokens]
        SET [ParentAuthIdentityId] = @ParentAuthIdentityId,
            [PetParentId] = @PetParentId,
            [DeviceId] = COALESCE(@DeviceId, [DeviceId]),
            [DevicePlatform] = COALESCE(@DevicePlatform, [DevicePlatform]),
            [IsActive] = 1,
            [LastSeenAtUtc] = @Now,
            [UpdatedAtUtc] = @Now
        WHERE [FcmToken] = @FcmToken;
    END
    ELSE
    BEGIN
        INSERT INTO [Parent].[ParentDeviceTokens]
        (
            [ParentAuthIdentityId],
            [PetParentId],
            [FcmToken],
            [DeviceId],
            [DevicePlatform]
        )
        VALUES
        (
            @ParentAuthIdentityId,
            @PetParentId,
            @FcmToken,
            @DeviceId,
            @DevicePlatform
        );
    END

    COMMIT TRANSACTION;

    SELECT [ParentDeviceTokenId],
           [PetParentId],
           [DeviceId],
           [DevicePlatform],
           [IsActive],
           @RetiredCount AS [RetiredTokenCount],
           [LastSeenAtUtc],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[ParentDeviceTokens]
    WHERE [FcmToken] = @FcmToken;
END
