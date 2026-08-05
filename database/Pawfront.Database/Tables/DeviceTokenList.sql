-- Table type carrying FCM tokens that Firebase reported as permanently invalid
-- (UNREGISTERED / INVALID_ARGUMENT), sent from NotificationDispatchFunction to
-- Notification.DeactivateDeviceTokens.
--
-- [Audience] discriminates which table the token lives in — 'Provider' ->
-- [Provider].[ProviderDeviceTokens], 'PetParent' -> [Parent].[ParentDeviceTokens]
-- — so one call cleans up both.
-- Deliberately unkeyed: [FcmToken] is NVARCHAR(2048) (4096 bytes), well past the
-- 900-byte clustered index key limit a PRIMARY KEY would impose. Duplicates are
-- harmless here — the deactivation UPDATE is idempotent.
CREATE TYPE [Notification].[DeviceTokenList] AS TABLE
(
    [Audience] NVARCHAR(16) NOT NULL,
    [FcmToken] NVARCHAR(2048) NOT NULL
);
