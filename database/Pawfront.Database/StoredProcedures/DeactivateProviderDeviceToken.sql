-- Deactivates one of the calling provider's FCM device tokens — the sign-out
-- counterpart to Provider.SaveProviderDeviceToken. Mirror of
-- Parent.DeactivateParentDeviceToken.
--
-- Scoped to the caller's own auth identity, so a caller cannot deactivate
-- somebody else's token even with a valid token string. "Not found" and "not
-- yours" are reported identically (51005) so this cannot be used to probe
-- whether a token is registered.
--
-- The row is kept with [IsActive] = 0 rather than deleted: [FcmToken] is UNIQUE,
-- so the same device signing back in is reactivated in place by the save sproc.
--
-- THROWs: 51005 device token not found for this caller.
CREATE OR ALTER PROCEDURE [Provider].[DeactivateProviderDeviceToken]
    @FirebaseUserId NVARCHAR(128),
    @FcmToken NVARCHAR(2048)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ProviderAuthIdentityId UNIQUEIDENTIFIER;

    SELECT @ProviderAuthIdentityId = [ProviderAuthIdentityId]
    FROM [Provider].[ProviderAuthIdentities]
    WHERE [FirebaseUserId] = @FirebaseUserId;

    IF @ProviderAuthIdentityId IS NULL
    BEGIN
        THROW 51005, 'Device token was not found for this account.', 1;
    END

    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[ProviderDeviceTokens]
        WHERE [FcmToken] = @FcmToken
          AND [ProviderAuthIdentityId] = @ProviderAuthIdentityId)
    BEGIN
        THROW 51005, 'Device token was not found for this account.', 1;
    END

    UPDATE [Provider].[ProviderDeviceTokens]
    SET [IsActive] = 0,
        [UpdatedAtUtc] = @Now
    WHERE [FcmToken] = @FcmToken
      AND [ProviderAuthIdentityId] = @ProviderAuthIdentityId;

    SELECT [ProviderDeviceTokenId],
           [ProviderId],
           [DeviceId],
           [DevicePlatform],
           [IsActive],
           0 AS [RetiredTokenCount],
           [LastSeenAtUtc],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Provider].[ProviderDeviceTokens]
    WHERE [FcmToken] = @FcmToken
      AND [ProviderAuthIdentityId] = @ProviderAuthIdentityId;
END
