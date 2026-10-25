-- Run after Providers.IsDeleted exists. Also embedded in DeployAll.sql.
-- Repair accounts deleted by older deployments without changing their IDs or history.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

UPDATE a
SET [FirebaseUserId] = N'deleted:' + CONVERT(NVARCHAR(36), p.[ProviderId]),
    [FirebaseTenantId] = NULL,
    [Email] = N'deleted+' + CONVERT(NVARCHAR(36), p.[ProviderId]) + N'@deleted.invalid',
    [IsEmailVerified] = 0,
    [DisplayName] = NULL,
    [FirebasePhoneNumber] = NULL,
    [PhotoUrl] = NULL,
    [UpdatedAtUtc] = SYSUTCDATETIME()
FROM [Provider].[ProviderAuthIdentities] AS a
JOIN [Provider].[Providers] AS p ON p.[ProviderAuthIdentityId] = a.[ProviderAuthIdentityId]
WHERE p.[IsDeleted] = 1
  AND (a.[FirebaseUserId] <> N'deleted:' + CONVERT(NVARCHAR(36), p.[ProviderId])
    OR a.[Email] <> N'deleted+' + CONVERT(NVARCHAR(36), p.[ProviderId]) + N'@deleted.invalid'
    OR a.[IsEmailVerified] <> 0 OR a.[FirebaseTenantId] IS NOT NULL
    OR a.[DisplayName] IS NOT NULL OR a.[FirebasePhoneNumber] IS NOT NULL OR a.[PhotoUrl] IS NOT NULL);

UPDATE [Provider].[Providers]
SET [MobileCountryCode] = N'+00',
    [MobileNumber] = N'DEL' + LEFT(REPLACE(CONVERT(NVARCHAR(36), [ProviderId]), N'-', N''), 29),
    [MobileVerifiedAtUtc] = NULL,
    [IsActive] = 0,
    [UpdatedAtUtc] = SYSUTCDATETIME()
WHERE [IsDeleted] = 1
  AND ([MobileCountryCode] <> N'+00'
    OR [MobileNumber] <> N'DEL' + LEFT(REPLACE(CONVERT(NVARCHAR(36), [ProviderId]), N'-', N''), 29)
    OR [MobileVerifiedAtUtc] IS NOT NULL OR [IsActive] <> 0);

-- Replace old unfiltered or single-column indexes atomically. Inactive accounts
-- still own their number; only permanently deleted accounts release it.
IF EXISTS (
    SELECT 1 FROM sys.indexes AS i
    WHERE i.[object_id] = OBJECT_ID(N'[Provider].[Providers]')
      AND i.[name] = N'UX_Providers_MobileNumber'
      AND (i.[is_unique] = 0 OR i.[has_filter] = 0
        OR REPLACE(i.[filter_definition], N' ', N'') <> N'([IsDeleted]=(0))'
        OR (SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), c.[name]), N',')
                       WITHIN GROUP (ORDER BY ic.[key_ordinal])
            FROM sys.index_columns AS ic
            JOIN sys.columns AS c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
            WHERE ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id]
              AND ic.[key_ordinal] > 0) <> N'MobileCountryCode,MobileNumber'))
    DROP INDEX [UX_Providers_MobileNumber] ON [Provider].[Providers];

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [object_id] = OBJECT_ID(N'[Provider].[Providers]') AND [name] = N'UX_Providers_MobileNumber')
    CREATE UNIQUE INDEX [UX_Providers_MobileNumber]
        ON [Provider].[Providers] ([MobileCountryCode], [MobileNumber])
        WHERE [IsDeleted] = 0;

COMMIT TRANSACTION;
