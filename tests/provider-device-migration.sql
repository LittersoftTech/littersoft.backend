-- Disposable databases only. Execute from the repository root using sqlcmd.
-- Simulates legacy same-device registrations, then runs the actual deployment twice.
SET NOCOUNT ON;
SET XACT_ABORT ON;
CREATE TABLE #DeviceMigrationFixture(AuthA UNIQUEIDENTIFIER, AuthB UNIQUEIDENTIFIER, DeviceId NVARCHAR(200));
INSERT #DeviceMigrationFixture VALUES(NEWID(),NEWID(),CONCAT(N'migration-',NEWID()));
DROP INDEX IF EXISTS [UX_ProviderDeviceTokens_ActiveDeviceId] ON [Provider].[ProviderDeviceTokens];
INSERT Provider.ProviderAuthIdentities(ProviderAuthIdentityId,FirebaseUserId,AuthProvider,Email)
SELECT AuthA,CONVERT(NVARCHAR(36),AuthA),N'EmailPassword',CONCAT(AuthA,N'@example.invalid') FROM #DeviceMigrationFixture
UNION ALL SELECT AuthB,CONVERT(NVARCHAR(36),AuthB),N'EmailPassword',CONCAT(AuthB,N'@example.invalid') FROM #DeviceMigrationFixture;
INSERT Provider.ProviderDeviceTokens(ProviderAuthIdentityId,FcmToken,DeviceId,LastSeenAtUtc)
SELECT AuthA,CONCAT(N'old-',AuthA),DeviceId,DATEADD(DAY,-1,SYSUTCDATETIME()) FROM #DeviceMigrationFixture
UNION ALL SELECT AuthB,CONCAT(N'new-',AuthB),DeviceId,SYSUTCDATETIME() FROM #DeviceMigrationFixture;
GO
:r database/Pawfront.Database/Deployment/DeployAll.sql
:r database/Pawfront.Database/Deployment/DeployAll.sql
IF EXISTS (SELECT 1 FROM Provider.ProviderDeviceTokens t JOIN #DeviceMigrationFixture f ON t.ProviderAuthIdentityId=f.AuthA)
    THROW 50001,'Deployment retained the previous account token for a shared phone.',1;
IF NOT EXISTS (SELECT 1 FROM Provider.ProviderDeviceTokens t JOIN #DeviceMigrationFixture f ON t.ProviderAuthIdentityId=f.AuthB WHERE t.IsActive=1)
    THROW 50001,'Deployment removed the most recent phone owner.',1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'Provider.ProviderDeviceTokens') AND name=N'UX_ProviderDeviceTokens_ActiveDeviceId')
    THROW 50001,'Deployment failed to add active-device uniqueness.',1;
DELETE t FROM Provider.ProviderDeviceTokens t JOIN #DeviceMigrationFixture f ON t.ProviderAuthIdentityId IN(f.AuthA,f.AuthB);
DELETE a FROM Provider.ProviderAuthIdentities a JOIN #DeviceMigrationFixture f ON a.ProviderAuthIdentityId IN(f.AuthA,f.AuthB);
DROP TABLE #DeviceMigrationFixture;
PRINT 'PASS legacy shared-device migration and repeated deployment';
