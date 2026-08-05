-- Deactivates one of the calling pet parent's FCM device tokens — the sign-out
-- counterpart to Parent.SaveParentDeviceToken.
--
-- Without this, signing out leaves the token active and the handset keeps
-- receiving that account's notifications, which matters most on a shared or
-- resold device.
--
-- Scoped to the caller's own auth identity, so a caller cannot deactivate
-- somebody else's token even with a valid token string. A token that does not
-- exist, or is not the caller's, is reported identically (51226) — the two are
-- deliberately indistinguishable so this cannot be used to probe whether a token
-- is registered.
--
-- The row is kept with [IsActive] = 0 rather than deleted: [FcmToken] is UNIQUE,
-- so the same device signing back in is reactivated in place by the save sproc.
--
-- THROWs: 51226 device token not found for this caller.
CREATE OR ALTER PROCEDURE [Parent].[DeactivateParentDeviceToken]
    @FirebaseUserId NVARCHAR(128),
    @FcmToken NVARCHAR(2048)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ParentAuthIdentityId UNIQUEIDENTIFIER;

    SELECT @ParentAuthIdentityId = [ParentAuthIdentityId]
    FROM [Parent].[ParentAuthIdentities]
    WHERE [FirebaseUserId] = @FirebaseUserId;

    IF @ParentAuthIdentityId IS NULL
    BEGIN
        THROW 51226, 'Device token was not found for this account.', 1;
    END

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[ParentDeviceTokens]
        WHERE [FcmToken] = @FcmToken
          AND [ParentAuthIdentityId] = @ParentAuthIdentityId)
    BEGIN
        THROW 51226, 'Device token was not found for this account.', 1;
    END

    UPDATE [Parent].[ParentDeviceTokens]
    SET [IsActive] = 0,
        [UpdatedAtUtc] = @Now
    WHERE [FcmToken] = @FcmToken
      AND [ParentAuthIdentityId] = @ParentAuthIdentityId;

    SELECT [ParentDeviceTokenId],
           [PetParentId],
           [DeviceId],
           [DevicePlatform],
           [IsActive],
           0 AS [RetiredTokenCount],
           [LastSeenAtUtc],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[ParentDeviceTokens]
    WHERE [FcmToken] = @FcmToken
      AND [ParentAuthIdentityId] = @ParentAuthIdentityId;
END
