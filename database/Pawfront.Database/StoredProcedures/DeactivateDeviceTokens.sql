-- Deactivates FCM tokens that Firebase reported as permanently invalid
-- (UNREGISTERED — the app was uninstalled or the token rotated — or
-- INVALID_ARGUMENT / SENDER_ID_MISMATCH). Called by NotificationDispatchFunction
-- after each batch, so dead tokens stop being pushed to on the next tick.
--
-- Rows are flipped to [IsActive] = 0 rather than deleted: the row records that a
-- device once existed, and the UNIQUE constraint on [FcmToken] means a token that
-- comes back (same device re-registering) is reactivated in place by the
-- Save*AuthIdentity upsert.
--
-- One call cleans both tables; @Tokens carries the [Audience] discriminator.
CREATE OR ALTER PROCEDURE [Notification].[DeactivateDeviceTokens]
    @Tokens [Notification].[DeviceTokenList] READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ProviderCount INT = 0;
    DECLARE @ParentCount INT = 0;

    UPDATE t
    SET [IsActive] = 0,
        [UpdatedAtUtc] = @Now
    FROM [Provider].[ProviderDeviceTokens] t
    WHERE t.[IsActive] = 1
      AND EXISTS (
            SELECT 1 FROM @Tokens x
            WHERE x.[Audience] = N'Provider' AND x.[FcmToken] = t.[FcmToken]);

    SET @ProviderCount = @@ROWCOUNT;

    UPDATE t
    SET [IsActive] = 0,
        [UpdatedAtUtc] = @Now
    FROM [Parent].[ParentDeviceTokens] t
    WHERE t.[IsActive] = 1
      AND EXISTS (
            SELECT 1 FROM @Tokens x
            WHERE x.[Audience] = N'PetParent' AND x.[FcmToken] = t.[FcmToken]);

    SET @ParentCount = @@ROWCOUNT;

    SELECT @ProviderCount AS [DeactivatedProviderTokens],
           @ParentCount AS [DeactivatedParentTokens];
END
