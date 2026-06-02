/*
================================================================================
  Pawfront — full database deployment script
--------------------------------------------------------------------------------
  Idempotent: safe to run repeatedly. Creates schema, tables, indexes, FKs,
  and stored procedures in the correct dependency order.

  Usage:
      sqlcmd -S <server> -d <database> -U <user> -P <password> -i DeployAll.sql
      (or paste into SSMS / Azure Data Studio and Execute)

  Notes:
    * GO is a batch separator (not T-SQL) — this file is meant for SSMS,
      Azure Data Studio, or sqlcmd. If you run it via ADO.NET, split on /^GO$/.
    * Stored procedures use CREATE OR ALTER, so they always reflect the latest
      version on re-run.
================================================================================
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

PRINT '--- Pawfront deployment starting ---';
GO

--------------------------------------------------------------------------------
-- 1. Schemas
--------------------------------------------------------------------------------
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = N'Provider')
BEGIN
    EXEC ('CREATE SCHEMA [Provider]');
    PRINT 'Created schema [Provider].';
END
ELSE
BEGIN
    PRINT 'Schema [Provider] already exists.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = N'Customer')
BEGIN
    EXEC ('CREATE SCHEMA [Customer]');
    PRINT 'Created schema [Customer].';
END
ELSE
BEGIN
    PRINT 'Schema [Customer] already exists.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = N'Event')
BEGIN
    EXEC ('CREATE SCHEMA [Event]');
    PRINT 'Created schema [Event].';
END
ELSE
BEGIN
    PRINT 'Schema [Event] already exists.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = N'Booking')
BEGIN
    EXEC ('CREATE SCHEMA [Booking]');
    PRINT 'Created schema [Booking].';
END
ELSE
BEGIN
    PRINT 'Schema [Booking] already exists.';
END
GO

--------------------------------------------------------------------------------
-- 2. Tables (created in FK-dependency order)
--------------------------------------------------------------------------------

-- 2.1 ProviderAuthIdentities ---------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ProviderAuthIdentities' AND [schema_id] = SCHEMA_ID(N'Provider'))
BEGIN
    CREATE TABLE [Provider].[ProviderAuthIdentities]
    (
        [ProviderAuthIdentityId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_ProviderAuthIdentities_ProviderAuthIdentityId] DEFAULT NEWSEQUENTIALID(),
        [ProviderId] UNIQUEIDENTIFIER NULL,
        [FirebaseUserId] NVARCHAR(128) NOT NULL,
        [FirebaseTenantId] NVARCHAR(128) NULL,
        [AuthProvider] NVARCHAR(32) NOT NULL,
        [FirebaseProviderId] NVARCHAR(64) NULL,
        [Email] NVARCHAR(320) NOT NULL,
        [IsEmailVerified] BIT NOT NULL
            CONSTRAINT [DF_ProviderAuthIdentities_IsEmailVerified] DEFAULT 0,
        [DisplayName] NVARCHAR(200) NULL,
        [FirebasePhoneNumber] NVARCHAR(32) NULL,
        [PhotoUrl] NVARCHAR(1000) NULL,
        [SignUpStatus] NVARCHAR(32) NOT NULL
            CONSTRAINT [DF_ProviderAuthIdentities_SignUpStatus] DEFAULT N'FirebaseAuthenticated',
        [LastSignedInAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderAuthIdentities_LastSignedInAtUtc] DEFAULT SYSUTCDATETIME(),
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderAuthIdentities_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderAuthIdentities_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ProviderAuthIdentities] PRIMARY KEY CLUSTERED ([ProviderAuthIdentityId] ASC),
        CONSTRAINT [UQ_ProviderAuthIdentities_FirebaseUserId] UNIQUE ([FirebaseUserId]),
        CONSTRAINT [CK_ProviderAuthIdentities_AuthProvider] CHECK ([AuthProvider] IN (N'Google', N'Apple', N'EmailPassword')),
        CONSTRAINT [CK_ProviderAuthIdentities_SignUpStatus] CHECK ([SignUpStatus] IN (N'FirebaseAuthenticated', N'ProviderProfileCompleted'))
    );
    PRINT 'Created table [Provider].[ProviderAuthIdentities].';
END
ELSE
BEGIN
    PRINT 'Table [Provider].[ProviderAuthIdentities] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_ProviderAuthIdentities_ProviderId'
      AND [object_id] = OBJECT_ID(N'[Provider].[ProviderAuthIdentities]'))
    CREATE UNIQUE INDEX [UX_ProviderAuthIdentities_ProviderId]
        ON [Provider].[ProviderAuthIdentities] ([ProviderId])
        WHERE [ProviderId] IS NOT NULL;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ProviderAuthIdentities_Email'
      AND [object_id] = OBJECT_ID(N'[Provider].[ProviderAuthIdentities]'))
    CREATE INDEX [IX_ProviderAuthIdentities_Email]
        ON [Provider].[ProviderAuthIdentities] ([Email]);
GO


-- 2.2 Providers ---------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Providers' AND [schema_id] = SCHEMA_ID(N'Provider'))
BEGIN
    CREATE TABLE [Provider].[Providers]
    (
        [ProviderId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_Providers_ProviderId] DEFAULT NEWSEQUENTIALID(),
        [ProviderAuthIdentityId] UNIQUEIDENTIFIER NOT NULL,
        [FirstName] NVARCHAR(100) NOT NULL,
        [LastName] NVARCHAR(100) NOT NULL,
        [Gender] NVARCHAR(32) NOT NULL,
        [MobileCountryCode] NVARCHAR(8) NOT NULL,
        [MobileNumber] NVARCHAR(32) NOT NULL,
        [DateOfBirth] DATE NOT NULL,
        [MobileVerifiedAtUtc] DATETIME2(7) NULL,
        [OnboardingStatus] NVARCHAR(32) NOT NULL
            CONSTRAINT [DF_Providers_OnboardingStatus] DEFAULT N'MobileVerificationPending',
        -- Master Active/Inactive switch. When 0, no new bookings can be created on
        -- ANY of this provider's services (Booking.CreateBooking enforces). Flipped
        -- via [Provider].[SetProviderActiveStatus]; the deactivation path rejects
        -- the toggle if future confirmed bookings still exist.
        [IsActive] BIT NOT NULL
            CONSTRAINT [DF_Providers_IsActive] DEFAULT 1,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Providers_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Providers_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_Providers] PRIMARY KEY CLUSTERED ([ProviderId] ASC),
        CONSTRAINT [UQ_Providers_ProviderAuthIdentityId] UNIQUE ([ProviderAuthIdentityId]),
        CONSTRAINT [FK_Providers_ProviderAuthIdentities_ProviderAuthIdentityId]
            FOREIGN KEY ([ProviderAuthIdentityId]) REFERENCES [Provider].[ProviderAuthIdentities] ([ProviderAuthIdentityId]),
        CONSTRAINT [CK_Providers_Gender] CHECK ([Gender] IN (N'Male', N'Female', N'NonBinary', N'Other', N'PreferNotToSay')),
        CONSTRAINT [CK_Providers_OnboardingStatus] CHECK ([OnboardingStatus] IN (N'MobileVerificationPending', N'MobileVerified'))
    );
    PRINT 'Created table [Provider].[Providers].';
END
ELSE
BEGIN
    PRINT 'Table [Provider].[Providers] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_Providers_MobileNumber'
      AND [object_id] = OBJECT_ID(N'[Provider].[Providers]'))
    CREATE UNIQUE INDEX [UX_Providers_MobileNumber]
        ON [Provider].[Providers] ([MobileCountryCode], [MobileNumber]);
GO

-- Add [IsActive] column to existing Providers tables (idempotent for upgrades).
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'IsActive'
      AND [object_id] = OBJECT_ID(N'[Provider].[Providers]'))
BEGIN
    ALTER TABLE [Provider].[Providers]
        ADD [IsActive] BIT NOT NULL
            CONSTRAINT [DF_Providers_IsActive] DEFAULT 1;
    PRINT 'Added column [Provider].[Providers].[IsActive].';
END
GO

-- Add the deferred back-FK on ProviderAuthIdentities now that Providers exists.
IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE [name] = N'FK_ProviderAuthIdentities_Providers_ProviderId'
      AND [parent_object_id] = OBJECT_ID(N'[Provider].[ProviderAuthIdentities]'))
BEGIN
    ALTER TABLE [Provider].[ProviderAuthIdentities]
        ADD CONSTRAINT [FK_ProviderAuthIdentities_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]);
    PRINT 'Added FK [FK_ProviderAuthIdentities_Providers_ProviderId].';
END
GO


-- 2.3 ProviderDeviceTokens ----------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ProviderDeviceTokens' AND [schema_id] = SCHEMA_ID(N'Provider'))
BEGIN
    CREATE TABLE [Provider].[ProviderDeviceTokens]
    (
        [ProviderDeviceTokenId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_ProviderDeviceTokens_ProviderDeviceTokenId] DEFAULT NEWSEQUENTIALID(),
        [ProviderAuthIdentityId] UNIQUEIDENTIFIER NOT NULL,
        [ProviderId] UNIQUEIDENTIFIER NULL,
        [FcmToken] NVARCHAR(2048) NOT NULL,
        [DeviceId] NVARCHAR(200) NULL,
        [DevicePlatform] NVARCHAR(32) NULL,
        [IsActive] BIT NOT NULL
            CONSTRAINT [DF_ProviderDeviceTokens_IsActive] DEFAULT 1,
        [LastSeenAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderDeviceTokens_LastSeenAtUtc] DEFAULT SYSUTCDATETIME(),
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderDeviceTokens_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderDeviceTokens_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ProviderDeviceTokens] PRIMARY KEY CLUSTERED ([ProviderDeviceTokenId] ASC),
        CONSTRAINT [UQ_ProviderDeviceTokens_FcmToken] UNIQUE ([FcmToken]),
        CONSTRAINT [FK_ProviderDeviceTokens_ProviderAuthIdentities_ProviderAuthIdentityId]
            FOREIGN KEY ([ProviderAuthIdentityId]) REFERENCES [Provider].[ProviderAuthIdentities] ([ProviderAuthIdentityId]),
        CONSTRAINT [FK_ProviderDeviceTokens_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
        CONSTRAINT [CK_ProviderDeviceTokens_DevicePlatform]
            CHECK ([DevicePlatform] IS NULL OR [DevicePlatform] IN (N'Android', N'iOS'))
    );
    PRINT 'Created table [Provider].[ProviderDeviceTokens].';
END
ELSE
BEGIN
    PRINT 'Table [Provider].[ProviderDeviceTokens] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ProviderDeviceTokens_ProviderId_IsActive'
      AND [object_id] = OBJECT_ID(N'[Provider].[ProviderDeviceTokens]'))
    CREATE INDEX [IX_ProviderDeviceTokens_ProviderId_IsActive]
        ON [Provider].[ProviderDeviceTokens] ([ProviderId], [IsActive]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ProviderDeviceTokens_ProviderAuthIdentityId_IsActive'
      AND [object_id] = OBJECT_ID(N'[Provider].[ProviderDeviceTokens]'))
    CREATE INDEX [IX_ProviderDeviceTokens_ProviderAuthIdentityId_IsActive]
        ON [Provider].[ProviderDeviceTokens] ([ProviderAuthIdentityId], [IsActive]);
GO


-- 2.4 ProviderMobileOtps ------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ProviderMobileOtps' AND [schema_id] = SCHEMA_ID(N'Provider'))
BEGIN
    CREATE TABLE [Provider].[ProviderMobileOtps]
    (
        [ProviderMobileOtpId] UNIQUEIDENTIFIER NOT NULL,
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [MobileCountryCode] NVARCHAR(8) NOT NULL,
        [MobileNumber] NVARCHAR(32) NOT NULL,
        [OtpCodeHash] VARBINARY(32) NOT NULL,
        [OtpCodeLastTwo] NVARCHAR(2) NOT NULL,
        [ValidationStatus] NVARCHAR(32) NOT NULL
            CONSTRAINT [DF_ProviderMobileOtps_ValidationStatus] DEFAULT N'Pending',
        [FailedAttemptCount] INT NOT NULL
            CONSTRAINT [DF_ProviderMobileOtps_FailedAttemptCount] DEFAULT 0,
        [DateSentUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderMobileOtps_DateSentUtc] DEFAULT SYSUTCDATETIME(),
        [DateValidatedUtc] DATETIME2(7) NULL,
        [ExpiresAtUtc] DATETIME2(7) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderMobileOtps_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderMobileOtps_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ProviderMobileOtps] PRIMARY KEY CLUSTERED ([ProviderMobileOtpId] ASC),
        CONSTRAINT [FK_ProviderMobileOtps_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
        CONSTRAINT [CK_ProviderMobileOtps_ValidationStatus]
            CHECK ([ValidationStatus] IN (N'Pending', N'Validated', N'Expired'))
    );
    PRINT 'Created table [Provider].[ProviderMobileOtps].';
END
ELSE
BEGIN
    PRINT 'Table [Provider].[ProviderMobileOtps] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ProviderMobileOtps_ProviderId_DateSentUtc'
      AND [object_id] = OBJECT_ID(N'[Provider].[ProviderMobileOtps]'))
    CREATE INDEX [IX_ProviderMobileOtps_ProviderId_DateSentUtc]
        ON [Provider].[ProviderMobileOtps] ([ProviderId], [DateSentUtc] DESC);
GO


-- 2.4b Provider.ProviderServices ---------------------------------------------
-- Catalog of services each provider offers (DayCare, NightStay, GroomingSession,
-- TrainingSession, VetAppointment). ServiceIds minted here are the keys closures,
-- bookings, and slot queries reference.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ProviderServices' AND [schema_id] = SCHEMA_ID(N'Provider'))
BEGIN
    CREATE TABLE [Provider].[ProviderServices]
    (
        [ServiceId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_ProviderServices_ServiceId] DEFAULT NEWSEQUENTIALID(),
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [ServiceCategory] NVARCHAR(64) NOT NULL,
        [SubCategory] NVARCHAR(64) NOT NULL,
        [ServiceType] NVARCHAR(64) NOT NULL,
        [IsActive] BIT NOT NULL
            CONSTRAINT [DF_ProviderServices_IsActive] DEFAULT 1,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderServices_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderServices_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ProviderServices] PRIMARY KEY CLUSTERED ([ServiceId] ASC),
        CONSTRAINT [FK_ProviderServices_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]) ON DELETE CASCADE,
        CONSTRAINT [UQ_ProviderServices_Provider_ServiceType] UNIQUE ([ProviderId], [ServiceType]),
        CONSTRAINT [CK_ProviderServices_ServiceCategory]
            CHECK ([ServiceCategory] IN (N'PetSitter', N'PetGroomer', N'PetTrainer', N'Vet')),
        CONSTRAINT [CK_ProviderServices_ServiceType]
            CHECK ([ServiceType] IN (N'DayCare', N'NightStay', N'GroomingSession', N'TrainingSession', N'VetAppointment')),
        CONSTRAINT [CK_ProviderServices_ServiceType_MatchesCategory] CHECK (
            ([ServiceCategory] = N'PetSitter'  AND [ServiceType] IN (N'DayCare', N'NightStay'))
            OR ([ServiceCategory] = N'PetGroomer' AND [ServiceType] = N'GroomingSession')
            OR ([ServiceCategory] = N'PetTrainer' AND [ServiceType] = N'TrainingSession')
            OR ([ServiceCategory] = N'Vet'        AND [ServiceType] = N'VetAppointment')
        )
    );
    PRINT 'Created table [Provider].[ProviderServices].';
END
ELSE
BEGIN
    PRINT 'Table [Provider].[ProviderServices] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ProviderServices_Provider_Active'
      AND [object_id] = OBJECT_ID(N'[Provider].[ProviderServices]'))
    CREATE INDEX [IX_ProviderServices_Provider_Active]
        ON [Provider].[ProviderServices] ([ProviderId], [IsActive])
        INCLUDE ([ServiceType], [ServiceCategory], [SubCategory]);
GO


-- 2.4c Provider.ServiceIdList table type --------------------------------------
-- Used by sprocs that accept an array of ServiceIds (e.g. closure batches).
-- Sent over from .NET as a SqlParameter with TypeName 'Provider.ServiceIdList'.
IF NOT EXISTS (
    SELECT 1 FROM sys.types AS t
    INNER JOIN sys.schemas AS s ON t.[schema_id] = s.[schema_id]
    WHERE t.[name] = N'ServiceIdList' AND s.[name] = N'Provider')
BEGIN
    CREATE TYPE [Provider].[ServiceIdList] AS TABLE
    (
        [ServiceId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY
    );
    PRINT 'Created type [Provider].[ServiceIdList].';
END
ELSE
BEGIN
    PRINT 'Type [Provider].[ServiceIdList] already exists.';
END
GO


-- 2.5a Retrofit: drop legacy composite UNIQUE (ProviderId, ServiceCategory) ---
-- and add UNIQUE(ProviderId) so a provider can only register ONE service.
-- Safe re-run: idempotent. Will FAIL if any provider already has > 1 row —
-- run the duplicate check + cleanup script in CLAUDE.md before retrofitting.
IF EXISTS (
    SELECT 1 FROM sys.objects
    WHERE [name] = N'UQ_ProviderServiceRegistrations_ProviderCategory'
      AND [parent_object_id] = OBJECT_ID(N'[Provider].[ProviderServiceRegistrations]'))
BEGIN
    ALTER TABLE [Provider].[ProviderServiceRegistrations]
        DROP CONSTRAINT [UQ_ProviderServiceRegistrations_ProviderCategory];
    PRINT 'Dropped legacy [UQ_ProviderServiceRegistrations_ProviderCategory].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.objects
    WHERE [name] = N'UQ_ProviderServiceRegistrations_Provider'
      AND [parent_object_id] = OBJECT_ID(N'[Provider].[ProviderServiceRegistrations]'))
BEGIN
    -- Only attempt to add when the table already exists; new installs include
    -- it inline in the CREATE TABLE block below.
    IF EXISTS (
        SELECT 1 FROM sys.tables
        WHERE [name] = N'ProviderServiceRegistrations' AND [schema_id] = SCHEMA_ID(N'Provider'))
    BEGIN
        ALTER TABLE [Provider].[ProviderServiceRegistrations]
            ADD CONSTRAINT [UQ_ProviderServiceRegistrations_Provider] UNIQUE ([ProviderId]);
        PRINT 'Added [UQ_ProviderServiceRegistrations_Provider].';
    END
END
GO


-- 2.5 ProviderServiceRegistrations --------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ProviderServiceRegistrations' AND [schema_id] = SCHEMA_ID(N'Provider'))
BEGIN
    CREATE TABLE [Provider].[ProviderServiceRegistrations]
    (
        [ProviderServiceRegistrationId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_ProviderServiceRegistrations_Id] DEFAULT NEWSEQUENTIALID(),
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [ServiceCategory] NVARCHAR(64) NOT NULL,
        [SubCategory] NVARCHAR(64) NOT NULL,
        [Latitude] DECIMAL(9, 6) NOT NULL,
        [Longitude] DECIMAL(9, 6) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderServiceRegistrations_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderServiceRegistrations_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ProviderServiceRegistrations] PRIMARY KEY CLUSTERED ([ProviderServiceRegistrationId] ASC),
        -- One registration per provider. A provider can only offer ONE category at a time.
        CONSTRAINT [UQ_ProviderServiceRegistrations_Provider]
            UNIQUE ([ProviderId]),
        CONSTRAINT [FK_ProviderServiceRegistrations_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
        CONSTRAINT [CK_ProviderServiceRegistrations_ServiceCategory]
            CHECK ([ServiceCategory] IN (N'PetSitter', N'PetGroomer', N'PetTrainer', N'PetAdoptionAndSale', N'Vet')),
        CONSTRAINT [CK_ProviderServiceRegistrations_Latitude]
            CHECK ([Latitude] BETWEEN -90 AND 90),
        CONSTRAINT [CK_ProviderServiceRegistrations_Longitude]
            CHECK ([Longitude] BETWEEN -180 AND 180)
    );
    PRINT 'Created table [Provider].[ProviderServiceRegistrations].';
END
ELSE
BEGIN
    PRINT 'Table [Provider].[ProviderServiceRegistrations] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ProviderServiceRegistrations_Category_SubCategory'
      AND [object_id] = OBJECT_ID(N'[Provider].[ProviderServiceRegistrations]'))
    CREATE INDEX [IX_ProviderServiceRegistrations_Category_SubCategory]
        ON [Provider].[ProviderServiceRegistrations] ([ServiceCategory], [SubCategory])
        INCLUDE ([ProviderId], [Latitude], [Longitude]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ProviderServiceRegistrations_Location'
      AND [object_id] = OBJECT_ID(N'[Provider].[ProviderServiceRegistrations]'))
    CREATE INDEX [IX_ProviderServiceRegistrations_Location]
        ON [Provider].[ProviderServiceRegistrations] ([Latitude], [Longitude])
        INCLUDE ([ProviderId], [ServiceCategory], [SubCategory]);
GO


-- 2.5c ProviderClosures -------------------------------------------------------
-- Fresh installs get the ServiceId column in the CREATE TABLE block.
-- Existing dev installs go through the migration block below (which wipes the
-- table, since closures without a ServiceId can't be retrofitted).
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ProviderClosures' AND [schema_id] = SCHEMA_ID(N'Provider'))
BEGIN
    CREATE TABLE [Provider].[ProviderClosures]
    (
        [ClosureId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_ProviderClosures_ClosureId] DEFAULT NEWSEQUENTIALID(),
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [ServiceId] UNIQUEIDENTIFIER NOT NULL,
        [StartDate] DATE NOT NULL,
        [EndDate] DATE NOT NULL,
        [StartTime] TIME(0) NULL,
        [EndTime] TIME(0) NULL,
        [Reason] NVARCHAR(500) NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderClosures_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ProviderClosures] PRIMARY KEY CLUSTERED ([ClosureId] ASC),
        CONSTRAINT [FK_ProviderClosures_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]) ON DELETE CASCADE,
        CONSTRAINT [FK_ProviderClosures_ProviderServices_ServiceId]
            FOREIGN KEY ([ServiceId]) REFERENCES [Provider].[ProviderServices] ([ServiceId]),
        CONSTRAINT [CK_ProviderClosures_DateOrder] CHECK ([EndDate] >= [StartDate]),
        CONSTRAINT [CK_ProviderClosures_Time_BothOrNeither] CHECK (
            ([StartTime] IS NULL AND [EndTime] IS NULL)
            OR ([StartTime] IS NOT NULL AND [EndTime] IS NOT NULL AND [StartTime] < [EndTime])
        ),
        CONSTRAINT [CK_ProviderClosures_PartialDayIsSingleDate] CHECK (
            [StartTime] IS NULL OR [StartDate] = [EndDate]
        )
    );
    PRINT 'Created table [Provider].[ProviderClosures].';
END
ELSE
BEGIN
    PRINT 'Table [Provider].[ProviderClosures] already exists.';
END
GO

-- Migration: add ServiceId to legacy ProviderClosures rows. Existing data is
-- wiped because closures pre-dating per-service semantics can't be retrofitted
-- to a specific ServiceId (the table is dev-only per the deploy assumption).
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ProviderClosures' AND [schema_id] = SCHEMA_ID(N'Provider'))
AND NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'ServiceId'
      AND [object_id] = OBJECT_ID(N'[Provider].[ProviderClosures]'))
BEGIN
    PRINT 'Migrating [Provider].[ProviderClosures] to per-service schema (wiping legacy rows).';
    DELETE FROM [Provider].[ProviderClosures];

    ALTER TABLE [Provider].[ProviderClosures]
        ADD [ServiceId] UNIQUEIDENTIFIER NOT NULL;

    ALTER TABLE [Provider].[ProviderClosures]
        ADD CONSTRAINT [FK_ProviderClosures_ProviderServices_ServiceId]
            FOREIGN KEY ([ServiceId]) REFERENCES [Provider].[ProviderServices] ([ServiceId]);
END
GO

-- Drop the legacy provider-only range index if it exists (replaced by per-service indexes).
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ProviderClosures_Provider_Range'
      AND [object_id] = OBJECT_ID(N'[Provider].[ProviderClosures]'))
BEGIN
    -- Recreate it shortly below to include [ServiceId]; SQL Server doesn't support
    -- altering included columns in-place without drop+create.
    DROP INDEX [IX_ProviderClosures_Provider_Range] ON [Provider].[ProviderClosures];
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ProviderClosures_Service_Range'
      AND [object_id] = OBJECT_ID(N'[Provider].[ProviderClosures]'))
BEGIN
    CREATE INDEX [IX_ProviderClosures_Service_Range]
        ON [Provider].[ProviderClosures] ([ServiceId], [StartDate], [EndDate])
        INCLUDE ([StartTime], [EndTime], [Reason]);
    PRINT 'Created index [IX_ProviderClosures_Service_Range].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ProviderClosures_Provider_Range'
      AND [object_id] = OBJECT_ID(N'[Provider].[ProviderClosures]'))
BEGIN
    CREATE INDEX [IX_ProviderClosures_Provider_Range]
        ON [Provider].[ProviderClosures] ([ProviderId], [StartDate], [EndDate])
        INCLUDE ([ServiceId], [StartTime], [EndTime], [Reason]);
    PRINT 'Created index [IX_ProviderClosures_Provider_Range].';
END
GO


-- 2.5b ProviderWeeklyAvailability --------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ProviderWeeklyAvailability' AND [schema_id] = SCHEMA_ID(N'Provider'))
BEGIN
    CREATE TABLE [Provider].[ProviderWeeklyAvailability]
    (
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [DayOfWeek] TINYINT NOT NULL,
        [IsOpen] BIT NOT NULL,
        [StartTime] TIME(0) NULL,
        [EndTime] TIME(0) NULL,
        [BreakStartTime] TIME(0) NULL,
        [BreakEndTime] TIME(0) NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderWeeklyAvailability_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderWeeklyAvailability_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ProviderWeeklyAvailability]
            PRIMARY KEY CLUSTERED ([ProviderId] ASC, [DayOfWeek] ASC),
        CONSTRAINT [FK_ProviderWeeklyAvailability_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]) ON DELETE CASCADE,
        CONSTRAINT [CK_ProviderWeeklyAvailability_DayOfWeek]
            CHECK ([DayOfWeek] BETWEEN 0 AND 6),
        CONSTRAINT [CK_ProviderWeeklyAvailability_Closed_NullTimes] CHECK (
            [IsOpen] = 1 OR (
                [StartTime] IS NULL AND [EndTime] IS NULL
                AND [BreakStartTime] IS NULL AND [BreakEndTime] IS NULL
            )
        ),
        CONSTRAINT [CK_ProviderWeeklyAvailability_Open_HasWindow] CHECK (
            [IsOpen] = 0 OR ([StartTime] IS NOT NULL AND [EndTime] IS NOT NULL)
        ),
        CONSTRAINT [CK_ProviderWeeklyAvailability_WindowOrder] CHECK (
            [StartTime] IS NULL OR [EndTime] IS NULL OR [StartTime] < [EndTime]
        ),
        CONSTRAINT [CK_ProviderWeeklyAvailability_Break_BothOrNeither] CHECK (
            ([BreakStartTime] IS NULL AND [BreakEndTime] IS NULL)
            OR ([BreakStartTime] IS NOT NULL AND [BreakEndTime] IS NOT NULL)
        ),
        CONSTRAINT [CK_ProviderWeeklyAvailability_BreakOrder] CHECK (
            [BreakStartTime] IS NULL OR [BreakEndTime] IS NULL
            OR [BreakStartTime] < [BreakEndTime]
        ),
        CONSTRAINT [CK_ProviderWeeklyAvailability_BreakInsideWindow] CHECK (
            [BreakStartTime] IS NULL OR [BreakEndTime] IS NULL
            OR ([BreakStartTime] >= [StartTime] AND [BreakEndTime] <= [EndTime])
        )
    );
    PRINT 'Created table [Provider].[ProviderWeeklyAvailability].';
END
ELSE
BEGIN
    PRINT 'Table [Provider].[ProviderWeeklyAvailability] already exists.';
END
GO


-- 2.6 ProviderPayoutMethods --------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ProviderPayoutMethods' AND [schema_id] = SCHEMA_ID(N'Provider'))
BEGIN
    CREATE TABLE [Provider].[ProviderPayoutMethods]
    (
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [PayoutMethod] NVARCHAR(32) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderPayoutMethods_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ProviderPayoutMethods] PRIMARY KEY CLUSTERED ([ProviderId] ASC, [PayoutMethod] ASC),
        CONSTRAINT [FK_ProviderPayoutMethods_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
        CONSTRAINT [CK_ProviderPayoutMethods_PayoutMethod]
            CHECK ([PayoutMethod] IN (N'Cash', N'Digital'))
    );
    PRINT 'Created table [Provider].[ProviderPayoutMethods].';
END
ELSE
BEGIN
    PRINT 'Table [Provider].[ProviderPayoutMethods] already exists.';
END
GO


-- 2.7 ProviderCancellationPolicies --------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ProviderCancellationPolicies' AND [schema_id] = SCHEMA_ID(N'Provider'))
BEGIN
    CREATE TABLE [Provider].[ProviderCancellationPolicies]
    (
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [MinimumHoursBeforeCancellation] INT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderCancellationPolicies_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderCancellationPolicies_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ProviderCancellationPolicies] PRIMARY KEY CLUSTERED ([ProviderId] ASC),
        CONSTRAINT [FK_ProviderCancellationPolicies_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
        CONSTRAINT [CK_ProviderCancellationPolicies_Hours]
            CHECK ([MinimumHoursBeforeCancellation] IS NULL
                   OR [MinimumHoursBeforeCancellation] IN (24, 48, 72, 96))
    );
    PRINT 'Created table [Provider].[ProviderCancellationPolicies].';
END
ELSE
BEGIN
    PRINT 'Table [Provider].[ProviderCancellationPolicies] already exists.';
END
GO


-- 2.8 Customer.PetParents ----------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'PetParents' AND [schema_id] = SCHEMA_ID(N'Customer'))
BEGIN
    CREATE TABLE [Customer].[PetParents]
    (
        [PetParentId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_PetParents_PetParentId] DEFAULT NEWSEQUENTIALID(),
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_PetParents_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_PetParents_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_PetParents] PRIMARY KEY CLUSTERED ([PetParentId] ASC)
    );
    PRINT 'Created table [Customer].[PetParents].';
END
ELSE
BEGIN
    PRINT 'Table [Customer].[PetParents] already exists.';
END
GO


-- 2.9 Customer.Pets ----------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Pets' AND [schema_id] = SCHEMA_ID(N'Customer'))
BEGIN
    CREATE TABLE [Customer].[Pets]
    (
        [PetId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_Pets_PetId] DEFAULT NEWSEQUENTIALID(),
        [PetParentId] UNIQUEIDENTIFIER NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Pets_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Pets_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_Pets] PRIMARY KEY CLUSTERED ([PetId] ASC),
        CONSTRAINT [FK_Pets_PetParents_PetParentId]
            FOREIGN KEY ([PetParentId]) REFERENCES [Customer].[PetParents] ([PetParentId])
    );
    PRINT 'Created table [Customer].[Pets].';
END
ELSE
BEGIN
    PRINT 'Table [Customer].[Pets] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Pets_PetParentId'
      AND [object_id] = OBJECT_ID(N'[Customer].[Pets]'))
    CREATE INDEX [IX_Pets_PetParentId]
        ON [Customer].[Pets] ([PetParentId]);
GO


-- 2.10 Event.Events ----------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Events' AND [schema_id] = SCHEMA_ID(N'Event'))
BEGIN
    CREATE TABLE [Event].[Events]
    (
        [EventId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_Events_EventId] DEFAULT NEWSEQUENTIALID(),
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [EventCategory] NVARCHAR(64) NOT NULL,
        [IsChildFriendly] BIT NOT NULL,
        [Title] NVARCHAR(200) NOT NULL,
        [Description] NVARCHAR(MAX) NOT NULL,
        [BannerImageUrl] NVARCHAR(1000) NULL,
        [EventType] NVARCHAR(32) NOT NULL,
        [StartDate] DATE NOT NULL,
        [EndDate] DATE NOT NULL,
        [StartTime] TIME(0) NOT NULL,
        [EndTime] TIME(0) NOT NULL,
        [ViewCount] INT NOT NULL
            CONSTRAINT [DF_Events_ViewCount] DEFAULT 0,
        [ShareCount] INT NOT NULL
            CONSTRAINT [DF_Events_ShareCount] DEFAULT 0,
        [InquiryCount] INT NOT NULL
            CONSTRAINT [DF_Events_InquiryCount] DEFAULT 0,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Events_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Events_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_Events] PRIMARY KEY CLUSTERED ([EventId] ASC),
        CONSTRAINT [FK_Events_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
        CONSTRAINT [CK_Events_EventCategory] CHECK ([EventCategory] IN (
            N'AdoptionAndRescue', N'PetTraining', N'Charity', N'Volunteering',
            N'HealthAndWellness', N'SocialAndCultural', N'OutdoorActivities', N'ParentEducation')),
        CONSTRAINT [CK_Events_EventType] CHECK ([EventType] IN (N'Physical', N'Online')),
        CONSTRAINT [CK_Events_DateRange] CHECK ([StartDate] <= [EndDate])
    );
    PRINT 'Created table [Event].[Events].';
END
ELSE
BEGIN
    PRINT 'Table [Event].[Events] already exists.';
END
GO

-- 2.10a Retrofit: add organiser-dashboard counter columns if missing -----------
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]') AND [name] = N'ViewCount')
BEGIN
    ALTER TABLE [Event].[Events]
        ADD [ViewCount] INT NOT NULL
            CONSTRAINT [DF_Events_ViewCount] DEFAULT 0;
    PRINT 'Added column [Event].[Events].[ViewCount].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]') AND [name] = N'ShareCount')
BEGIN
    ALTER TABLE [Event].[Events]
        ADD [ShareCount] INT NOT NULL
            CONSTRAINT [DF_Events_ShareCount] DEFAULT 0;
    PRINT 'Added column [Event].[Events].[ShareCount].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]') AND [name] = N'InquiryCount')
BEGIN
    ALTER TABLE [Event].[Events]
        ADD [InquiryCount] INT NOT NULL
            CONSTRAINT [DF_Events_InquiryCount] DEFAULT 0;
    PRINT 'Added column [Event].[Events].[InquiryCount].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Events_ProviderId_StartDate'
      AND [object_id] = OBJECT_ID(N'[Event].[Events]'))
    CREATE INDEX [IX_Events_ProviderId_StartDate]
        ON [Event].[Events] ([ProviderId], [StartDate] DESC);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Events_Category_StartDate'
      AND [object_id] = OBJECT_ID(N'[Event].[Events]'))
    CREATE INDEX [IX_Events_Category_StartDate]
        ON [Event].[Events] ([EventCategory], [StartDate] DESC)
        INCLUDE ([ProviderId], [Title], [EventType]);
GO


-- 2.10b Booking.Bookings ------------------------------------------------------
-- Fresh installs get the ServiceId column in the CREATE TABLE block.
-- Existing dev installs go through the migration block below (which wipes the
-- table, since bookings without a ServiceId can't be retrofitted).
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Bookings' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[Bookings]
    (
        [BookingId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_Bookings_BookingId] DEFAULT NEWSEQUENTIALID(),
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [PetParentId] UNIQUEIDENTIFIER NULL,
        [ServiceId] UNIQUEIDENTIFIER NOT NULL,
        [ServiceCategory] NVARCHAR(64) NOT NULL,
        [SubCategory] NVARCHAR(64) NOT NULL,
        [ServiceItemCode] NVARCHAR(64) NULL,
        [BookingDate] DATE NOT NULL,
        [StartTime] TIME(0) NOT NULL,
        [EndTime] TIME(0) NOT NULL,
        [Source] NVARCHAR(16) NOT NULL
            CONSTRAINT [DF_Bookings_Source] DEFAULT N'App',
        [CustomerName] NVARCHAR(200) NULL,
        [CustomerMobileCountryCode] NVARCHAR(8) NULL,
        [CustomerMobile] NVARCHAR(32) NULL,
        [AnimalType] NVARCHAR(32) NULL,
        [PetName] NVARCHAR(100) NULL,
        [ServiceLocation] NVARCHAR(32) NULL,
        [CustomerLocation] NVARCHAR(500) NULL,
        [PricePerHour] DECIMAL(10, 2) NULL,
        [JobNotes] NVARCHAR(2000) NULL,
        [Status] NVARCHAR(32) NOT NULL
            CONSTRAINT [DF_Bookings_Status] DEFAULT N'Confirmed',
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Bookings_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Bookings_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [CancelledAtUtc] DATETIME2(7) NULL,

        CONSTRAINT [PK_Bookings] PRIMARY KEY CLUSTERED ([BookingId] ASC),
        CONSTRAINT [FK_Bookings_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
        CONSTRAINT [FK_Bookings_PetParents_PetParentId]
            FOREIGN KEY ([PetParentId]) REFERENCES [Customer].[PetParents] ([PetParentId]),
        CONSTRAINT [FK_Bookings_ProviderServices_ServiceId]
            FOREIGN KEY ([ServiceId]) REFERENCES [Provider].[ProviderServices] ([ServiceId]),
        CONSTRAINT [CK_Bookings_TimeOrder] CHECK ([StartTime] < [EndTime]),
        CONSTRAINT [CK_Bookings_Status]
            CHECK ([Status] IN (N'Confirmed', N'Cancelled', N'Completed', N'NoShow')),
        CONSTRAINT [CK_Bookings_CancelledRequiresTimestamp] CHECK (
            ([Status] = N'Cancelled' AND [CancelledAtUtc] IS NOT NULL)
            OR ([Status] <> N'Cancelled')
        ),
        CONSTRAINT [CK_Bookings_Source]
            CHECK ([Source] IN (N'App', N'Custom')),
        CONSTRAINT [CK_Bookings_AnimalType]
            CHECK ([AnimalType] IS NULL
                OR [AnimalType] IN (N'Dog', N'Cat', N'Hamster', N'GuineaPig')),
        CONSTRAINT [CK_Bookings_ServiceLocation]
            CHECK ([ServiceLocation] IS NULL
                OR [ServiceLocation] IN (N'MyLocation', N'CustomerLocation')),
        CONSTRAINT [CK_Bookings_PricePerHour_NonNegative]
            CHECK ([PricePerHour] IS NULL OR [PricePerHour] >= 0),
        CONSTRAINT [CK_Bookings_SourceShape] CHECK
        (
            ([Source] = N'App'
                AND [PetParentId] IS NOT NULL
                AND [CustomerName] IS NULL
                AND [CustomerMobileCountryCode] IS NULL
                AND [CustomerMobile] IS NULL
                AND [AnimalType] IS NULL
                AND [PetName] IS NULL
                AND [ServiceLocation] IS NULL
                AND [CustomerLocation] IS NULL
                AND [PricePerHour] IS NULL)
         OR ([Source] = N'Custom'
                AND [PetParentId] IS NULL
                AND [CustomerName] IS NOT NULL
                AND [CustomerMobileCountryCode] IS NOT NULL
                AND [CustomerMobile] IS NOT NULL
                AND [AnimalType] IS NOT NULL
                AND [PetName] IS NOT NULL
                AND [ServiceLocation] IS NOT NULL
                AND [PricePerHour] IS NOT NULL)
        ),
        CONSTRAINT [CK_Bookings_CustomerLocationShape] CHECK
        (
            ([ServiceLocation] = N'CustomerLocation' AND [CustomerLocation] IS NOT NULL)
         OR ([ServiceLocation] = N'MyLocation'       AND [CustomerLocation] IS NULL)
         OR ([ServiceLocation] IS NULL               AND [CustomerLocation] IS NULL)
        )
    );
    PRINT 'Created table [Booking].[Bookings].';
END
ELSE
BEGIN
    PRINT 'Table [Booking].[Bookings] already exists.';
END
GO

-- Migration: add ServiceId to legacy Bookings rows. Existing data is wiped
-- because bookings pre-dating per-service semantics can't be retrofitted to a
-- specific ServiceId (the table is dev-only per the deploy assumption).
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Bookings' AND [schema_id] = SCHEMA_ID(N'Booking'))
AND NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'ServiceId'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
BEGIN
    PRINT 'Migrating [Booking].[Bookings] to per-service schema (wiping legacy rows).';
    DELETE FROM [Booking].[Bookings];

    ALTER TABLE [Booking].[Bookings]
        ADD [ServiceId] UNIQUEIDENTIFIER NOT NULL;

    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [FK_Bookings_ProviderServices_ServiceId]
            FOREIGN KEY ([ServiceId]) REFERENCES [Provider].[ProviderServices] ([ServiceId]);
END
GO

-- Add [ServiceItemCode] to existing Bookings tables (idempotent for upgrades).
-- Nullable so existing non-grooming bookings stay valid; only PetGroomer
-- bookings populate it. Resolves which menu item under the GroomingSession
-- service this booking is for.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'ServiceItemCode'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
BEGIN
    ALTER TABLE [Booking].[Bookings]
        ADD [ServiceItemCode] NVARCHAR(64) NULL;
    PRINT 'Added column [Booking].[Bookings].[ServiceItemCode].';
END
GO

-- Drop the legacy IX_Bookings_Provider_Date_Status if it lacks ServiceId in INCLUDE.
-- Same dance as ProviderClosures: SQL Server can't alter INCLUDE in place.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Bookings_Provider_Date_Status'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes AS i
    INNER JOIN sys.index_columns AS ic
        ON i.[object_id] = ic.[object_id] AND i.[index_id] = ic.[index_id]
    INNER JOIN sys.columns AS c
        ON ic.[object_id] = c.[object_id] AND ic.[column_id] = c.[column_id]
    WHERE i.[object_id] = OBJECT_ID(N'[Booking].[Bookings]')
      AND i.[name] = N'IX_Bookings_Provider_Date_Status'
      AND c.[name] = N'ServiceId')
BEGIN
    DROP INDEX [IX_Bookings_Provider_Date_Status] ON [Booking].[Bookings];
END
GO

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Bookings_PetParent_Status'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes AS i
    INNER JOIN sys.index_columns AS ic
        ON i.[object_id] = ic.[object_id] AND i.[index_id] = ic.[index_id]
    INNER JOIN sys.columns AS c
        ON ic.[object_id] = c.[object_id] AND ic.[column_id] = c.[column_id]
    WHERE i.[object_id] = OBJECT_ID(N'[Booking].[Bookings]')
      AND i.[name] = N'IX_Bookings_PetParent_Status'
      AND c.[name] = N'ServiceId')
BEGIN
    DROP INDEX [IX_Bookings_PetParent_Status] ON [Booking].[Bookings];
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Bookings_Service_Date_Status'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
    CREATE INDEX [IX_Bookings_Service_Date_Status]
        ON [Booking].[Bookings] ([ServiceId], [BookingDate], [Status])
        INCLUDE ([StartTime], [EndTime], [BookingId], [PetParentId], [ProviderId]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Bookings_Provider_Date_Status'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
    CREATE INDEX [IX_Bookings_Provider_Date_Status]
        ON [Booking].[Bookings] ([ProviderId], [BookingDate], [Status])
        INCLUDE ([ServiceId], [StartTime], [EndTime], [BookingId], [PetParentId]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Bookings_PetParent_Status'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
    CREATE INDEX [IX_Bookings_PetParent_Status]
        ON [Booking].[Bookings] ([PetParentId], [Status])
        INCLUDE ([ServiceId], [BookingDate], [StartTime], [EndTime], [ProviderId]);
GO

-- Migration: add private/custom-job columns + the [Source] discriminator to an
-- existing [Booking].[Bookings] table. Idempotent. PetParentId must also be made
-- nullable so Source='Custom' rows can omit it.
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Bookings' AND [schema_id] = SCHEMA_ID(N'Booking'))
AND NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'Source'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
BEGIN
    PRINT 'Adding private-job columns + [Source] discriminator to [Booking].[Bookings].';

    ALTER TABLE [Booking].[Bookings]
        ADD [Source] NVARCHAR(16) NOT NULL
                CONSTRAINT [DF_Bookings_Source] DEFAULT N'App',
            [CustomerName] NVARCHAR(200) NULL,
            [CustomerMobileCountryCode] NVARCHAR(8) NULL,
            [CustomerMobile] NVARCHAR(32) NULL,
            [AnimalType] NVARCHAR(32) NULL,
            [PetName] NVARCHAR(100) NULL,
            [ServiceLocation] NVARCHAR(32) NULL,
            [CustomerLocation] NVARCHAR(500) NULL,
            [PricePerHour] DECIMAL(10, 2) NULL,
            [JobNotes] NVARCHAR(2000) NULL;
END
GO

-- Drop the PetParentId NOT NULL constraint (if present) so Custom rows can be NULL.
IF EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE [name] = N'PetParentId'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]')
      AND is_nullable = 0)
BEGIN
    PRINT 'Relaxing [Booking].[Bookings].[PetParentId] to NULLABLE.';
    ALTER TABLE [Booking].[Bookings]
        ALTER COLUMN [PetParentId] UNIQUEIDENTIFIER NULL;
END
GO

-- Add CHECK constraints (idempotent).
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Bookings_Source')
BEGIN
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_Source]
            CHECK ([Source] IN (N'App', N'Custom'));
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Bookings_AnimalType')
BEGIN
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_AnimalType]
            CHECK ([AnimalType] IS NULL
                OR [AnimalType] IN (N'Dog', N'Cat', N'Hamster', N'GuineaPig'));
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Bookings_ServiceLocation')
BEGIN
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_ServiceLocation]
            CHECK ([ServiceLocation] IS NULL
                OR [ServiceLocation] IN (N'MyLocation', N'CustomerLocation'));
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Bookings_PricePerHour_NonNegative')
BEGIN
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_PricePerHour_NonNegative]
            CHECK ([PricePerHour] IS NULL OR [PricePerHour] >= 0);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Bookings_SourceShape')
BEGIN
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_SourceShape] CHECK
        (
            ([Source] = N'App'
                AND [PetParentId] IS NOT NULL
                AND [CustomerName] IS NULL
                AND [CustomerMobileCountryCode] IS NULL
                AND [CustomerMobile] IS NULL
                AND [AnimalType] IS NULL
                AND [PetName] IS NULL
                AND [ServiceLocation] IS NULL
                AND [CustomerLocation] IS NULL
                AND [PricePerHour] IS NULL)
         OR ([Source] = N'Custom'
                AND [PetParentId] IS NULL
                AND [CustomerName] IS NOT NULL
                AND [CustomerMobileCountryCode] IS NOT NULL
                AND [CustomerMobile] IS NOT NULL
                AND [AnimalType] IS NOT NULL
                AND [PetName] IS NOT NULL
                AND [ServiceLocation] IS NOT NULL
                AND [PricePerHour] IS NOT NULL)
        );
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Bookings_CustomerLocationShape')
BEGIN
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_CustomerLocationShape] CHECK
        (
            ([ServiceLocation] = N'CustomerLocation' AND [CustomerLocation] IS NOT NULL)
         OR ([ServiceLocation] = N'MyLocation'       AND [CustomerLocation] IS NULL)
         OR ([ServiceLocation] IS NULL               AND [CustomerLocation] IS NULL)
        );
END
GO


-- 2.11 Event.EventAmenities --------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'EventAmenities' AND [schema_id] = SCHEMA_ID(N'Event'))
BEGIN
    CREATE TABLE [Event].[EventAmenities]
    (
        [EventId] UNIQUEIDENTIFIER NOT NULL,
        [Amenity] NVARCHAR(64) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_EventAmenities_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_EventAmenities] PRIMARY KEY CLUSTERED ([EventId] ASC, [Amenity] ASC),
        CONSTRAINT [FK_EventAmenities_Events_EventId]
            FOREIGN KEY ([EventId]) REFERENCES [Event].[Events] ([EventId]) ON DELETE CASCADE,
        CONSTRAINT [CK_EventAmenities_Amenity] CHECK ([Amenity] IN (
            N'FreeParking', N'PaidParking', N'Restrooms', N'DrinkingWater',
            N'FoodAndBeverage', N'SeatingAreas', N'FirstAidBooth', N'None'))
    );
    PRINT 'Created table [Event].[EventAmenities].';
END
ELSE
BEGIN
    PRINT 'Table [Event].[EventAmenities] already exists.';
END
GO


-- 2.12 Event.EventBookings ----------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'EventBookings' AND [schema_id] = SCHEMA_ID(N'Event'))
BEGIN
    CREATE TABLE [Event].[EventBookings]
    (
        [BookingId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_EventBookings_BookingId] DEFAULT NEWSEQUENTIALID(),
        [EventId] UNIQUEIDENTIFIER NOT NULL,
        [BookerName] NVARCHAR(200) NOT NULL,
        [BookerEmail] NVARCHAR(320) NOT NULL,
        [BookerMobile] NVARCHAR(32) NULL,
        [TicketCount] INT NOT NULL,
        [PaymentMethod] NVARCHAR(32) NOT NULL,
        [PaymentStatus] NVARCHAR(32) NOT NULL
            CONSTRAINT [DF_EventBookings_PaymentStatus] DEFAULT N'Pending',
        [PaymentReference] NVARCHAR(200) NULL,
        [TotalAmount] DECIMAL(18, 2) NOT NULL
            CONSTRAINT [DF_EventBookings_TotalAmount] DEFAULT (0),
        [Status] NVARCHAR(32) NOT NULL
            CONSTRAINT [DF_EventBookings_Status] DEFAULT N'Confirmed',
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_EventBookings_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_EventBookings_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [CancelledAtUtc] DATETIME2(7) NULL,

        CONSTRAINT [PK_EventBookings] PRIMARY KEY CLUSTERED ([BookingId] ASC),
        CONSTRAINT [FK_EventBookings_Events_EventId]
            FOREIGN KEY ([EventId]) REFERENCES [Event].[Events] ([EventId]),
        CONSTRAINT [CK_EventBookings_TicketCount] CHECK ([TicketCount] >= 1),
        CONSTRAINT [CK_EventBookings_TotalAmount] CHECK ([TotalAmount] >= 0),
        CONSTRAINT [CK_EventBookings_PaymentMethod]
            CHECK ([PaymentMethod] IN (N'CreditCard', N'Twint')),
        CONSTRAINT [CK_EventBookings_PaymentStatus]
            CHECK ([PaymentStatus] IN (N'Pending', N'Paid', N'Failed')),
        CONSTRAINT [CK_EventBookings_Status]
            CHECK ([Status] IN (N'Confirmed', N'Cancelled')),
        CONSTRAINT [CK_EventBookings_CancelledRequiresTimestamp] CHECK (
            ([Status] = N'Cancelled' AND [CancelledAtUtc] IS NOT NULL)
            OR ([Status] <> N'Cancelled')
        )
    );
    PRINT 'Created table [Event].[EventBookings].';
END
ELSE
BEGIN
    PRINT 'Table [Event].[EventBookings] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_EventBookings_Event_Status'
      AND [object_id] = OBJECT_ID(N'[Event].[EventBookings]'))
    CREATE INDEX [IX_EventBookings_Event_Status]
        ON [Event].[EventBookings] ([EventId], [Status])
        INCLUDE ([TicketCount], [BookingId], [BookerEmail], [PaymentStatus], [CreatedAtUtc]);
GO


-- 2.13 Event.EventBookingTickets ----------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'EventBookingTickets' AND [schema_id] = SCHEMA_ID(N'Event'))
BEGIN
    CREATE TABLE [Event].[EventBookingTickets]
    (
        [TicketId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_EventBookingTickets_TicketId] DEFAULT NEWSEQUENTIALID(),
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [EventId] UNIQUEIDENTIFIER NOT NULL,
        [TicketNumber] INT NOT NULL,
        [AttendeeName] NVARCHAR(200) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_EventBookingTickets_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_EventBookingTickets] PRIMARY KEY CLUSTERED ([TicketId] ASC),
        CONSTRAINT [FK_EventBookingTickets_EventBookings_BookingId]
            FOREIGN KEY ([BookingId]) REFERENCES [Event].[EventBookings] ([BookingId])
            ON DELETE CASCADE,
        CONSTRAINT [FK_EventBookingTickets_Events_EventId]
            FOREIGN KEY ([EventId]) REFERENCES [Event].[Events] ([EventId]),
        CONSTRAINT [CK_EventBookingTickets_TicketNumber] CHECK ([TicketNumber] >= 1),
        CONSTRAINT [UQ_EventBookingTickets_Booking_Number]
            UNIQUE ([BookingId], [TicketNumber])
    );
    PRINT 'Created table [Event].[EventBookingTickets].';
END
ELSE
BEGIN
    PRINT 'Table [Event].[EventBookingTickets] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_EventBookingTickets_Booking'
      AND [object_id] = OBJECT_ID(N'[Event].[EventBookingTickets]'))
    CREATE INDEX [IX_EventBookingTickets_Booking]
        ON [Event].[EventBookingTickets] ([BookingId])
        INCLUDE ([TicketNumber], [AttendeeName], [EventId]);
GO


-- 2.14 Event.EventBookingAttendeeNames table type -----------------------------
-- TVP used by [Event].[CreateEventBooking]. Sent from .NET as a SqlParameter
-- with TypeName 'Event.EventBookingAttendeeNames'.
IF NOT EXISTS (
    SELECT 1 FROM sys.types AS t
    INNER JOIN sys.schemas AS s ON t.[schema_id] = s.[schema_id]
    WHERE t.[name] = N'EventBookingAttendeeNames' AND s.[name] = N'Event')
BEGIN
    CREATE TYPE [Event].[EventBookingAttendeeNames] AS TABLE
    (
        [TicketNumber] INT NOT NULL PRIMARY KEY,
        [AttendeeName] NVARCHAR(200) NOT NULL
    );
    PRINT 'Created type [Event].[EventBookingAttendeeNames].';
END
ELSE
BEGIN
    PRINT 'Type [Event].[EventBookingAttendeeNames] already exists.';
END
GO


--------------------------------------------------------------------------------
-- 3. Stored procedures (CREATE OR ALTER — always reflects latest version)
--------------------------------------------------------------------------------

-- 3.1 SaveProviderAuthIdentity ------------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[SaveProviderAuthIdentity]
    @FirebaseUserId NVARCHAR(128),
    @FirebaseTenantId NVARCHAR(128) = NULL,
    @AuthProvider NVARCHAR(32),
    @FirebaseProviderId NVARCHAR(64) = NULL,
    @Email NVARCHAR(320),
    @IsEmailVerified BIT,
    @DisplayName NVARCHAR(200) = NULL,
    @FirebasePhoneNumber NVARCHAR(32) = NULL,
    @PhotoUrl NVARCHAR(1000) = NULL,
    @FcmToken NVARCHAR(2048) = NULL,
    @DeviceId NVARCHAR(200) = NULL,
    @DevicePlatform NVARCHAR(32) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ProviderAuthIdentityId UNIQUEIDENTIFIER;
    DECLARE @ProviderId UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @ProviderAuthIdentityId = [ProviderAuthIdentityId]
    FROM [Provider].[ProviderAuthIdentities] WITH (UPDLOCK, HOLDLOCK)
    WHERE [FirebaseUserId] = @FirebaseUserId;

    IF @ProviderAuthIdentityId IS NULL
    BEGIN
        INSERT INTO [Provider].[ProviderAuthIdentities]
        (
            [FirebaseUserId], [FirebaseTenantId], [AuthProvider], [FirebaseProviderId],
            [Email], [IsEmailVerified], [DisplayName], [FirebasePhoneNumber], [PhotoUrl]
        )
        VALUES
        (
            @FirebaseUserId, @FirebaseTenantId, @AuthProvider, @FirebaseProviderId,
            @Email, @IsEmailVerified, @DisplayName, @FirebasePhoneNumber, @PhotoUrl
        );

        SELECT @ProviderAuthIdentityId = [ProviderAuthIdentityId],
               @ProviderId = [ProviderId]
        FROM [Provider].[ProviderAuthIdentities]
        WHERE [FirebaseUserId] = @FirebaseUserId;
    END
    ELSE
    BEGIN
        UPDATE [Provider].[ProviderAuthIdentities]
        SET [FirebaseTenantId] = @FirebaseTenantId,
            [AuthProvider] = @AuthProvider,
            [FirebaseProviderId] = @FirebaseProviderId,
            [Email] = @Email,
            [IsEmailVerified] = @IsEmailVerified,
            [DisplayName] = @DisplayName,
            [FirebasePhoneNumber] = @FirebasePhoneNumber,
            [PhotoUrl] = @PhotoUrl,
            [LastSignedInAtUtc] = SYSUTCDATETIME(),
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ProviderAuthIdentityId] = @ProviderAuthIdentityId;

        SELECT @ProviderId = [ProviderId]
        FROM [Provider].[ProviderAuthIdentities]
        WHERE [ProviderAuthIdentityId] = @ProviderAuthIdentityId;
    END

    IF @FcmToken IS NOT NULL AND LEN(LTRIM(RTRIM(@FcmToken))) > 0
    BEGIN
        IF EXISTS (
            SELECT 1
            FROM [Provider].[ProviderDeviceTokens] WITH (UPDLOCK, HOLDLOCK)
            WHERE [FcmToken] = @FcmToken
        )
        BEGIN
            UPDATE [Provider].[ProviderDeviceTokens]
            SET [ProviderAuthIdentityId] = @ProviderAuthIdentityId,
                [ProviderId] = @ProviderId,
                [DeviceId] = @DeviceId,
                [DevicePlatform] = @DevicePlatform,
                [IsActive] = 1,
                [LastSeenAtUtc] = SYSUTCDATETIME(),
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [FcmToken] = @FcmToken;
        END
        ELSE
        BEGIN
            INSERT INTO [Provider].[ProviderDeviceTokens]
            ([ProviderAuthIdentityId], [ProviderId], [FcmToken], [DeviceId], [DevicePlatform])
            VALUES
            (@ProviderAuthIdentityId, @ProviderId, @FcmToken, @DeviceId, @DevicePlatform);
        END
    END

    SELECT [ProviderAuthIdentityId],
           [ProviderId],
           [FirebaseUserId],
           [AuthProvider],
           [FirebaseProviderId],
           [Email],
           [IsEmailVerified],
           [DisplayName],
           [FirebasePhoneNumber],
           [PhotoUrl],
           [FirebaseTenantId],
           [SignUpStatus],
           [LastSignedInAtUtc],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Provider].[ProviderAuthIdentities]
    WHERE [FirebaseUserId] = @FirebaseUserId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[SaveProviderAuthIdentity].';
GO


-- 3.2 CompleteProviderProfile -------------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[CompleteProviderProfile]
    @ProviderAuthIdentityId UNIQUEIDENTIFIER,
    @FirstName NVARCHAR(100),
    @LastName NVARCHAR(100),
    @Gender NVARCHAR(32),
    @MobileCountryCode NVARCHAR(8),
    @MobileNumber NVARCHAR(32),
    @DateOfBirth DATE
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ProviderId UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @ProviderId = [ProviderId]
    FROM [Provider].[ProviderAuthIdentities] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderAuthIdentityId] = @ProviderAuthIdentityId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51001, 'Provider auth identity was not found.', 1;
    END

    IF @ProviderId IS NULL
    BEGIN
        INSERT INTO [Provider].[Providers]
        ([ProviderAuthIdentityId], [FirstName], [LastName], [Gender],
         [MobileCountryCode], [MobileNumber], [DateOfBirth])
        VALUES
        (@ProviderAuthIdentityId, @FirstName, @LastName, @Gender,
         @MobileCountryCode, @MobileNumber, @DateOfBirth);

        SELECT @ProviderId = [ProviderId]
        FROM [Provider].[Providers]
        WHERE [ProviderAuthIdentityId] = @ProviderAuthIdentityId;

        UPDATE [Provider].[ProviderAuthIdentities]
        SET [ProviderId] = @ProviderId,
            [SignUpStatus] = N'ProviderProfileCompleted',
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ProviderAuthIdentityId] = @ProviderAuthIdentityId;

        UPDATE [Provider].[ProviderDeviceTokens]
        SET [ProviderId] = @ProviderId,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ProviderAuthIdentityId] = @ProviderAuthIdentityId;
    END

    SELECT [ProviderId],
           [ProviderAuthIdentityId],
           [FirstName],
           [LastName],
           [Gender],
           [MobileCountryCode],
           [MobileNumber],
           [DateOfBirth],
           [MobileVerifiedAtUtc],
           [OnboardingStatus],
           [IsActive],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[CompleteProviderProfile].';
GO


-- 3.2b GetProviderProfile -----------------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[GetProviderProfile]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ProviderId],
           [ProviderAuthIdentityId],
           [FirstName],
           [LastName],
           [Gender],
           [MobileCountryCode],
           [MobileNumber],
           [DateOfBirth],
           [MobileVerifiedAtUtc],
           [OnboardingStatus],
           [IsActive],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;
END;
GO
PRINT 'Created/updated [Provider].[GetProviderProfile].';
GO


-- 3.2c GetProviderByFirebaseUid -----------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[GetProviderByFirebaseUid]
    @FirebaseUserId NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    -- Resolves a Firebase user id (sub/user_id claim) to the persisted provider
    -- identity, so the mobile app can re-hydrate state after a reinstall (which
    -- wipes local storage). LEFT JOIN to Providers: the auth identity may exist
    -- without a profile row (mid-onboarding state), in which case ProviderId and
    -- the profile columns come back NULL. Empty result set means no auth identity
    -- exists for this Firebase user.
    SELECT ai.[ProviderAuthIdentityId],
           ai.[ProviderId],
           ai.[FirebaseUserId],
           ai.[Email],
           ai.[IsEmailVerified],
           ai.[DisplayName],
           ai.[SignUpStatus],
           CAST(CASE WHEN p.[ProviderId] IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS [HasProfile],
           p.[OnboardingStatus],
           p.[MobileVerifiedAtUtc],
           p.[IsActive]
    FROM [Provider].[ProviderAuthIdentities] AS ai
    LEFT JOIN [Provider].[Providers] AS p
        ON p.[ProviderId] = ai.[ProviderId]
    WHERE ai.[FirebaseUserId] = @FirebaseUserId;
END;
GO
PRINT 'Created/updated [Provider].[GetProviderByFirebaseUid].';
GO


-- 3.2d SetProviderActiveStatus ------------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[SetProviderActiveStatus]
    @ProviderId UNIQUEIDENTIFIER,
    @IsActive BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    -- Lock the provider row so a concurrent SetProviderActiveStatus / booking
    -- create on the same provider serialises behind us.
    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[Providers] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51100, 'Provider profile was not found.', 1;
    END

    -- When DEACTIVATING, check whether any future confirmed bookings exist
    -- across ALL of this provider's services. A confirmed booking is "in the
    -- future" when its date is strictly after today, OR it's today but hasn't
    -- ended yet. UPDLOCK + HOLDLOCK serialises us against concurrent
    -- Booking.CreateBooking so no booking can sneak in between the check and
    -- the flip.
    IF @IsActive = 0
    BEGIN
        DECLARE @Today DATE = CAST(SYSUTCDATETIME() AS DATE);
        DECLARE @NowTime TIME(0) = CAST(SYSUTCDATETIME() AS TIME(0));

        DECLARE @Conflicts TABLE (
            BookingId UNIQUEIDENTIFIER NOT NULL,
            ServiceId UNIQUEIDENTIFIER NOT NULL,
            ServiceCategory NVARCHAR(64) NOT NULL,
            SubCategory NVARCHAR(64) NOT NULL,
            PetParentId UNIQUEIDENTIFIER NULL,
            Source NVARCHAR(16) NOT NULL,
            CustomerName NVARCHAR(200) NULL,
            BookingDate DATE NOT NULL,
            StartTime TIME(0) NOT NULL,
            EndTime TIME(0) NOT NULL
        );

        INSERT INTO @Conflicts (BookingId, ServiceId, ServiceCategory, SubCategory,
                                PetParentId, Source, CustomerName, BookingDate, StartTime, EndTime)
        SELECT b.[BookingId], b.[ServiceId], b.[ServiceCategory], b.[SubCategory],
               b.[PetParentId], b.[Source], b.[CustomerName],
               b.[BookingDate], b.[StartTime], b.[EndTime]
        FROM [Booking].[Bookings] AS b WITH (UPDLOCK, HOLDLOCK)
        WHERE b.[ProviderId] = @ProviderId
          AND b.[Status] = N'Confirmed'
          AND (
              b.[BookingDate] > @Today
              OR (b.[BookingDate] = @Today AND b.[EndTime] > @NowTime)
          );

        IF EXISTS (SELECT 1 FROM @Conflicts)
        BEGIN
            -- Conflict-shape result set: 10 columns (was 8 before custom-job
            -- support landed). The Application reader detects this shape vs
            -- the 3-column success shape and emits the BookingsExist variant.
            -- No write happened — rollback to release the UPDLOCK + HOLDLOCK.
            SELECT BookingId, ServiceId, ServiceCategory, SubCategory,
                   PetParentId, Source, CustomerName,
                   BookingDate, StartTime, EndTime
            FROM @Conflicts
            ORDER BY BookingDate ASC, StartTime ASC;

            ROLLBACK TRANSACTION;
            RETURN;
        END
    END

    UPDATE [Provider].[Providers]
    SET [IsActive] = @IsActive,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [ProviderId] = @ProviderId;

    -- Success-shape result set: 3 columns.
    SELECT @ProviderId AS [ProviderId],
           @IsActive AS [IsActive],
           SYSUTCDATETIME() AS [UpdatedAtUtc];

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[SetProviderActiveStatus].';
GO


-- 3.3 CreateMobileVerificationOtp ---------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[CreateMobileVerificationOtp]
    @ProviderId UNIQUEIDENTIFIER,
    @OtpCode NVARCHAR(10)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ProviderMobileOtpId UNIQUEIDENTIFIER = NEWID();
    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @MobileCountryCode NVARCHAR(8);
    DECLARE @MobileNumber NVARCHAR(32);

    SELECT @MobileCountryCode = [MobileCountryCode],
           @MobileNumber = [MobileNumber]
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;

    IF @MobileNumber IS NULL
    BEGIN
        THROW 51002, 'Provider profile was not found.', 1;
    END

    INSERT INTO [Provider].[ProviderMobileOtps]
    ([ProviderMobileOtpId], [ProviderId], [MobileCountryCode], [MobileNumber],
     [OtpCodeHash], [OtpCodeLastTwo], [DateSentUtc], [ExpiresAtUtc],
     [CreatedAtUtc], [UpdatedAtUtc])
    VALUES
    (
        @ProviderMobileOtpId,
        @ProviderId,
        @MobileCountryCode,
        @MobileNumber,
        HASHBYTES('SHA2_256', CONVERT(NVARCHAR(36), @ProviderMobileOtpId) + N':' + @OtpCode),
        RIGHT(@OtpCode, 2),
        @Now,
        DATEADD(MINUTE, 10, @Now),
        @Now,
        @Now
    );

    SELECT [ProviderMobileOtpId],
           [ProviderId],
           [MobileCountryCode],
           [MobileNumber],
           [DateSentUtc],
           [ExpiresAtUtc]
    FROM [Provider].[ProviderMobileOtps]
    WHERE [ProviderMobileOtpId] = @ProviderMobileOtpId;
END;
GO
PRINT 'Created/updated [Provider].[CreateMobileVerificationOtp].';
GO


-- 3.4 VerifyMobileVerificationOtp ---------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[VerifyMobileVerificationOtp]
    @ProviderId UNIQUEIDENTIFIER,
    @ProviderMobileOtpId UNIQUEIDENTIFIER,
    @OtpCode NVARCHAR(10)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @OtpCodeHash VARBINARY(32);
    DECLARE @ValidationStatus NVARCHAR(32);
    DECLARE @DateSentUtc DATETIME2(7);
    DECLARE @DateValidatedUtc DATETIME2(7);
    DECLARE @ExpiresAtUtc DATETIME2(7);
    DECLARE @ResponseStatus NVARCHAR(32);
    DECLARE @IsValidated BIT = 0;

    BEGIN TRANSACTION;

    SELECT @OtpCodeHash = [OtpCodeHash],
           @ValidationStatus = [ValidationStatus],
           @DateSentUtc = [DateSentUtc],
           @DateValidatedUtc = [DateValidatedUtc],
           @ExpiresAtUtc = [ExpiresAtUtc]
    FROM [Provider].[ProviderMobileOtps] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderMobileOtpId] = @ProviderMobileOtpId
      AND [ProviderId] = @ProviderId;

    IF @OtpCodeHash IS NULL
    BEGIN
        THROW 51003, 'Provider mobile OTP entry was not found.', 1;
    END

    IF @ValidationStatus = N'Validated'
    BEGIN
        SET @IsValidated = 1;
        SET @ResponseStatus = N'Validated';
    END
    ELSE IF @ValidationStatus = N'Expired' OR @Now >= @ExpiresAtUtc
    BEGIN
        UPDATE [Provider].[ProviderMobileOtps]
        SET [ValidationStatus] = N'Expired',
            [UpdatedAtUtc] = @Now
        WHERE [ProviderMobileOtpId] = @ProviderMobileOtpId;

        SET @ResponseStatus = N'Expired';
    END
    ELSE IF @OtpCodeHash = HASHBYTES('SHA2_256', CONVERT(NVARCHAR(36), @ProviderMobileOtpId) + N':' + @OtpCode)
    BEGIN
        UPDATE [Provider].[ProviderMobileOtps]
        SET [ValidationStatus] = N'Validated',
            [DateValidatedUtc] = @Now,
            [UpdatedAtUtc] = @Now
        WHERE [ProviderMobileOtpId] = @ProviderMobileOtpId;

        UPDATE [Provider].[Providers]
        SET [MobileVerifiedAtUtc] = COALESCE([MobileVerifiedAtUtc], @Now),
            [OnboardingStatus] = N'MobileVerified',
            [UpdatedAtUtc] = @Now
        WHERE [ProviderId] = @ProviderId;

        SET @IsValidated = 1;
        SET @ResponseStatus = N'Validated';
        SET @DateValidatedUtc = @Now;
    END
    ELSE
    BEGIN
        UPDATE [Provider].[ProviderMobileOtps]
        SET [FailedAttemptCount] = [FailedAttemptCount] + 1,
            [UpdatedAtUtc] = @Now
        WHERE [ProviderMobileOtpId] = @ProviderMobileOtpId;

        SET @ResponseStatus = N'Invalid';
    END

    SELECT [ProviderMobileOtpId],
           [ProviderId],
           @IsValidated AS [IsValidated],
           @ResponseStatus AS [ValidationStatus],
           [DateSentUtc],
           [DateValidatedUtc],
           [ExpiresAtUtc]
    FROM [Provider].[ProviderMobileOtps]
    WHERE [ProviderMobileOtpId] = @ProviderMobileOtpId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[VerifyMobileVerificationOtp].';
GO


-- 3.5 SaveProviderServiceRegistration -----------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[SaveProviderServiceRegistration]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceCategory NVARCHAR(64),
    @SubCategory NVARCHAR(64),
    @Latitude DECIMAL(9, 6),
    @Longitude DECIMAL(9, 6)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ExistingId UNIQUEIDENTIFIER;
    DECLARE @ExistingCategory NVARCHAR(64);

    BEGIN TRANSACTION;

    SELECT @ExistingId = [ProviderServiceRegistrationId],
           @ExistingCategory = [ServiceCategory]
    FROM [Provider].[ProviderServiceRegistrations] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId;

    IF @ExistingId IS NULL
    BEGIN
        IF NOT EXISTS (
            SELECT 1
            FROM [Provider].[Providers]
            WHERE [ProviderId] = @ProviderId
        )
        BEGIN
            THROW 51010, 'Provider profile was not found.', 1;
        END

        INSERT INTO [Provider].[ProviderServiceRegistrations]
        ([ProviderId], [ServiceCategory], [SubCategory], [Latitude], [Longitude])
        VALUES
        (@ProviderId, @ServiceCategory, @SubCategory, @Latitude, @Longitude);
    END
    ELSE IF @ExistingCategory <> @ServiceCategory
    BEGIN
        DECLARE @ConflictMessage NVARCHAR(400) =
            N'Provider is already registered under ''' + @ExistingCategory +
            N''' and cannot register under ''' + @ServiceCategory + N'''.';
        THROW 51011, @ConflictMessage, 1;
    END
    ELSE
    BEGIN
        UPDATE [Provider].[ProviderServiceRegistrations]
        SET [SubCategory] = @SubCategory,
            [Latitude] = @Latitude,
            [Longitude] = @Longitude,
            [UpdatedAtUtc] = @Now
        WHERE [ProviderServiceRegistrationId] = @ExistingId;
    END

    SELECT [ProviderServiceRegistrationId],
           [ProviderId],
           [ServiceCategory],
           [SubCategory],
           [Latitude],
           [Longitude],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Provider].[ProviderServiceRegistrations]
    WHERE [ProviderId] = @ProviderId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[SaveProviderServiceRegistration].';
GO


-- 3.6 SaveProviderPayoutMethods ----------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[SaveProviderPayoutMethods]
    @ProviderId UNIQUEIDENTIFIER,
    @AcceptsCash BIT,
    @AcceptsDigital BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[Providers]
        WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51020, 'Provider profile was not found.', 1;
    END

    DELETE FROM [Provider].[ProviderPayoutMethods]
    WHERE [ProviderId] = @ProviderId;

    IF @AcceptsCash = 1
    BEGIN
        INSERT INTO [Provider].[ProviderPayoutMethods] ([ProviderId], [PayoutMethod])
        VALUES (@ProviderId, N'Cash');
    END

    IF @AcceptsDigital = 1
    BEGIN
        INSERT INTO [Provider].[ProviderPayoutMethods] ([ProviderId], [PayoutMethod])
        VALUES (@ProviderId, N'Digital');
    END

    SELECT [PayoutMethod]
    FROM [Provider].[ProviderPayoutMethods]
    WHERE [ProviderId] = @ProviderId
    ORDER BY [PayoutMethod];

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[SaveProviderPayoutMethods].';
GO


-- 3.7 SaveProviderCancellationPolicy -----------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[SaveProviderCancellationPolicy]
    @ProviderId UNIQUEIDENTIFIER,
    @MinimumHoursBeforeCancellation INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[Providers]
        WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51021, 'Provider profile was not found.', 1;
    END

    IF EXISTS (
        SELECT 1
        FROM [Provider].[ProviderCancellationPolicies] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        UPDATE [Provider].[ProviderCancellationPolicies]
        SET [MinimumHoursBeforeCancellation] = @MinimumHoursBeforeCancellation,
            [UpdatedAtUtc] = @Now
        WHERE [ProviderId] = @ProviderId;
    END
    ELSE
    BEGIN
        INSERT INTO [Provider].[ProviderCancellationPolicies]
        ([ProviderId], [MinimumHoursBeforeCancellation])
        VALUES (@ProviderId, @MinimumHoursBeforeCancellation);
    END

    SELECT [ProviderId],
           [MinimumHoursBeforeCancellation],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Provider].[ProviderCancellationPolicies]
    WHERE [ProviderId] = @ProviderId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[SaveProviderCancellationPolicy].';
GO


-- 3.8 GetProviderPolicy ------------------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[GetProviderPolicy]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [PayoutMethod]
    FROM [Provider].[ProviderPayoutMethods]
    WHERE [ProviderId] = @ProviderId
    ORDER BY [PayoutMethod];

    SELECT [ProviderId],
           [MinimumHoursBeforeCancellation],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Provider].[ProviderCancellationPolicies]
    WHERE [ProviderId] = @ProviderId;
END;
GO
PRINT 'Created/updated [Provider].[GetProviderPolicy].';
GO


-- 3.9 GetProviderOnboardingStatus --------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[GetProviderOnboardingStatus]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        p.[ProviderId],
        p.[MobileVerifiedAtUtc],
        i.[IsEmailVerified]
    FROM [Provider].[Providers] p
    INNER JOIN [Provider].[ProviderAuthIdentities] i
        ON i.[ProviderAuthIdentityId] = p.[ProviderAuthIdentityId]
    WHERE p.[ProviderId] = @ProviderId;

    SELECT [ServiceCategory], [SubCategory]
    FROM [Provider].[ProviderServiceRegistrations]
    WHERE [ProviderId] = @ProviderId
    ORDER BY [ServiceCategory];

    SELECT [PayoutMethod]
    FROM [Provider].[ProviderPayoutMethods]
    WHERE [ProviderId] = @ProviderId
    ORDER BY [PayoutMethod];

    SELECT [MinimumHoursBeforeCancellation]
    FROM [Provider].[ProviderCancellationPolicies]
    WHERE [ProviderId] = @ProviderId;
END;
GO
PRINT 'Created/updated [Provider].[GetProviderOnboardingStatus].';
GO


-- 3.13 SaveProviderWeeklyAvailability ----------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[SaveProviderWeeklyAvailability]
    @ProviderId UNIQUEIDENTIFIER,
    @AvailabilityJson NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[Providers]
        WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51050, 'Provider profile was not found.', 1;
    END

    DELETE FROM [Provider].[ProviderWeeklyAvailability]
    WHERE [ProviderId] = @ProviderId;

    INSERT INTO [Provider].[ProviderWeeklyAvailability]
    (
        [ProviderId], [DayOfWeek], [IsOpen],
        [StartTime], [EndTime], [BreakStartTime], [BreakEndTime]
    )
    SELECT @ProviderId,
           CAST(JSON_VALUE([value], '$.dayOfWeek') AS TINYINT),
           CAST(JSON_VALUE([value], '$.isOpen') AS BIT),
           CAST(JSON_VALUE([value], '$.startTime') AS TIME(0)),
           CAST(JSON_VALUE([value], '$.endTime') AS TIME(0)),
           CAST(JSON_VALUE([value], '$.breakStartTime') AS TIME(0)),
           CAST(JSON_VALUE([value], '$.breakEndTime') AS TIME(0))
    FROM OPENJSON(@AvailabilityJson);

    SELECT [ProviderId], [DayOfWeek], [IsOpen],
           [StartTime], [EndTime], [BreakStartTime], [BreakEndTime],
           [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Provider].[ProviderWeeklyAvailability]
    WHERE [ProviderId] = @ProviderId
    ORDER BY [DayOfWeek];

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[SaveProviderWeeklyAvailability].';
GO


-- 3.14 GetProviderWeeklyAvailability -----------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[GetProviderWeeklyAvailability]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ProviderId], [DayOfWeek], [IsOpen],
           [StartTime], [EndTime], [BreakStartTime], [BreakEndTime],
           [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Provider].[ProviderWeeklyAvailability]
    WHERE [ProviderId] = @ProviderId
    ORDER BY [DayOfWeek];
END;
GO
PRINT 'Created/updated [Provider].[GetProviderWeeklyAvailability].';
GO


-- 3.15 Booking.CreateBooking -------------------------------------------------
CREATE OR ALTER PROCEDURE [Booking].[CreateBooking]
    @ProviderId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @ServiceId UNIQUEIDENTIFIER,
    @ServiceCategory NVARCHAR(64),
    @SubCategory NVARCHAR(64),
    @ServiceItemCode NVARCHAR(64) = NULL,
    @BookingDate DATE,
    @StartTime TIME(0),
    @EndTime TIME(0),
    @Capacity INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @ProviderIsActive BIT;
    SELECT @ProviderIsActive = [IsActive]
    FROM [Provider].[Providers] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId;

    IF @ProviderIsActive IS NULL
        THROW 51061, 'Provider was not found.', 1;

    -- Master Active/Inactive switch — when the provider has flipped themselves
    -- inactive, NO new bookings are accepted on ANY of their services. The
    -- UPDLOCK + HOLDLOCK above serialises us against a concurrent
    -- SetProviderActiveStatus call, so the check is race-safe.
    IF @ProviderIsActive = 0
        THROW 51067, 'Provider is currently inactive and is not accepting new bookings.', 1;

    IF NOT EXISTS (SELECT 1 FROM [Customer].[PetParents] WHERE [PetParentId] = @PetParentId)
        THROW 51060, 'Pet parent was not found.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[ProviderServices] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [ProviderId] = @ProviderId
          AND [IsActive] = 1
    )
        THROW 51066, 'Service is not valid or active for this provider.', 1;

    DECLARE @Concurrent INT;
    SELECT @Concurrent = COUNT(*)
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ServiceId] = @ServiceId
      AND [BookingDate] = @BookingDate
      AND [Status] = N'Confirmed'
      AND [StartTime] < @EndTime
      AND [EndTime] > @StartTime;

    IF @Concurrent >= @Capacity
        THROW 51062, 'No remaining capacity for this slot.', 1;

    DECLARE @InsertedBookingId TABLE ([BookingId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[Bookings]
    ([ProviderId], [PetParentId], [ServiceId], [ServiceCategory], [SubCategory],
     [ServiceItemCode], [BookingDate], [StartTime], [EndTime])
    OUTPUT inserted.[BookingId] INTO @InsertedBookingId
    VALUES (@ProviderId, @PetParentId, @ServiceId, @ServiceCategory, @SubCategory,
            @ServiceItemCode, @BookingDate, @StartTime, @EndTime);

    DECLARE @BookingId UNIQUEIDENTIFIER = (SELECT TOP (1) [BookingId] FROM @InsertedBookingId);

    SELECT [BookingId], [ProviderId], [PetParentId], [ServiceId], [ServiceCategory], [SubCategory],
           [BookingDate], [StartTime], [EndTime], [Status],
           [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc], [ServiceItemCode],
           [Source], [CustomerName], [CustomerMobileCountryCode], [CustomerMobile],
           [AnimalType], [PetName], [ServiceLocation], [CustomerLocation],
           [PricePerHour], [JobNotes]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Booking].[CreateBooking].';
GO


-- 3.16 Booking.GetBooking ----------------------------------------------------
CREATE OR ALTER PROCEDURE [Booking].[GetBooking]
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingId], [ProviderId], [PetParentId], [ServiceId], [ServiceCategory], [SubCategory],
           [BookingDate], [StartTime], [EndTime], [Status],
           [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc], [ServiceItemCode],
           [Source], [CustomerName], [CustomerMobileCountryCode], [CustomerMobile],
           [AnimalType], [PetName], [ServiceLocation], [CustomerLocation],
           [PricePerHour], [JobNotes]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;
END;
GO
PRINT 'Created/updated [Booking].[GetBooking].';
GO


-- 3.17 Booking.CancelBooking -------------------------------------------------
CREATE OR ALTER PROCEDURE [Booking].[CancelBooking]
    @BookingId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(32);
    DECLARE @CurrentParent UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status],
           @CurrentParent = [PetParentId]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
        THROW 51063, 'Booking was not found.', 1;

    IF @CurrentParent <> @PetParentId
        THROW 51064, 'Only the original booker can cancel this booking.', 1;

    IF @CurrentStatus = N'Cancelled'
        THROW 51065, 'Booking is already cancelled.', 1;

    UPDATE [Booking].[Bookings]
    SET [Status] = N'Cancelled',
        [CancelledAtUtc] = @Now,
        [UpdatedAtUtc] = @Now
    WHERE [BookingId] = @BookingId;

    SELECT [BookingId], [ProviderId], [PetParentId], [ServiceId], [ServiceCategory], [SubCategory],
           [BookingDate], [StartTime], [EndTime], [Status],
           [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc], [ServiceItemCode],
           [Source], [CustomerName], [CustomerMobileCountryCode], [CustomerMobile],
           [AnimalType], [PetName], [ServiceLocation], [CustomerLocation],
           [PricePerHour], [JobNotes]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Booking].[CancelBooking].';
GO


-- 3.18 Booking.ListBookingsByProvider ----------------------------------------
CREATE OR ALTER PROCEDURE [Booking].[ListBookingsByProvider]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceId UNIQUEIDENTIFIER = NULL,
    @BookingDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingId], [ProviderId], [PetParentId], [ServiceId], [ServiceCategory], [SubCategory],
           [BookingDate], [StartTime], [EndTime], [Status],
           [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc], [ServiceItemCode],
           [Source], [CustomerName], [CustomerMobileCountryCode], [CustomerMobile],
           [AnimalType], [PetName], [ServiceLocation], [CustomerLocation],
           [PricePerHour], [JobNotes]
    FROM [Booking].[Bookings]
    WHERE [ProviderId] = @ProviderId
      AND (@ServiceId IS NULL OR [ServiceId] = @ServiceId)
      AND (@BookingDate IS NULL OR [BookingDate] = @BookingDate)
    ORDER BY [BookingDate] DESC, [StartTime] DESC;
END;
GO
PRINT 'Created/updated [Booking].[ListBookingsByProvider].';
GO


-- 3.19 Booking.ListBookingsByPetParent ---------------------------------------
CREATE OR ALTER PROCEDURE [Booking].[ListBookingsByPetParent]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingId], [ProviderId], [PetParentId], [ServiceId], [ServiceCategory], [SubCategory],
           [BookingDate], [StartTime], [EndTime], [Status],
           [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc], [ServiceItemCode],
           [Source], [CustomerName], [CustomerMobileCountryCode], [CustomerMobile],
           [AnimalType], [PetName], [ServiceLocation], [CustomerLocation],
           [PricePerHour], [JobNotes]
    FROM [Booking].[Bookings]
    WHERE [PetParentId] = @PetParentId
    ORDER BY [BookingDate] DESC, [StartTime] DESC;
END;
GO
PRINT 'Created/updated [Booking].[ListBookingsByPetParent].';
GO


-- 3.20 Booking.GetBookingsForDate --------------------------------------------
CREATE OR ALTER PROCEDURE [Booking].[GetBookingsForDate]
    @ServiceId UNIQUEIDENTIFIER,
    @BookingDate DATE
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [StartTime], [EndTime]
    FROM [Booking].[Bookings]
    WHERE [ServiceId] = @ServiceId
      AND [BookingDate] = @BookingDate
      AND [Status] = N'Confirmed'
    ORDER BY [StartTime];
END;
GO
PRINT 'Created/updated [Booking].[GetBookingsForDate].';
GO


-- 3.20b Booking.CreateCustomBooking ------------------------------------------
-- Provider-initiated private/custom booking for an unregistered walk-in. Same
-- race-safe per-service capacity check as [Booking].[CreateBooking]; differs
-- only in identifying the customer via free-text fields instead of PetParentId.
CREATE OR ALTER PROCEDURE [Booking].[CreateCustomBooking]
    @ProviderId                UNIQUEIDENTIFIER,
    @ServiceId                 UNIQUEIDENTIFIER,
    @ServiceCategory           NVARCHAR(64),
    @SubCategory               NVARCHAR(64),
    @CustomerName              NVARCHAR(200),
    @CustomerMobileCountryCode NVARCHAR(8),
    @CustomerMobile            NVARCHAR(32),
    @AnimalType                NVARCHAR(32),
    @PetName                   NVARCHAR(100),
    @BookingDate               DATE,
    @StartTime                 TIME(0),
    @EndTime                   TIME(0),
    @ServiceLocation           NVARCHAR(32),
    @CustomerLocation          NVARCHAR(500) = NULL,
    @PricePerHour              DECIMAL(10, 2),
    @JobNotes                  NVARCHAR(2000) = NULL,
    @Capacity                  INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @ProviderIsActive BIT;
    SELECT @ProviderIsActive = [IsActive]
    FROM [Provider].[Providers] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId;

    IF @ProviderIsActive IS NULL
        THROW 51061, 'Provider was not found.', 1;

    IF @ProviderIsActive = 0
        THROW 51067, 'Provider is currently inactive and is not accepting new bookings.', 1;

    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[ProviderServices] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [ProviderId] = @ProviderId
          AND [IsActive] = 1
    )
        THROW 51066, 'Service is not valid or active for this provider.', 1;

    -- Custom and App bookings share one capacity bucket per ServiceId.
    DECLARE @Concurrent INT;
    SELECT @Concurrent = COUNT(*)
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ServiceId] = @ServiceId
      AND [BookingDate] = @BookingDate
      AND [Status] = N'Confirmed'
      AND [StartTime] < @EndTime
      AND [EndTime] > @StartTime;

    IF @Concurrent >= @Capacity
        THROW 51062, 'No remaining capacity for this slot.', 1;

    DECLARE @InsertedBookingId TABLE ([BookingId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[Bookings]
    ([ProviderId], [PetParentId], [ServiceId], [ServiceCategory], [SubCategory],
     [ServiceItemCode], [BookingDate], [StartTime], [EndTime],
     [Source], [CustomerName], [CustomerMobileCountryCode], [CustomerMobile],
     [AnimalType], [PetName], [ServiceLocation], [CustomerLocation],
     [PricePerHour], [JobNotes])
    OUTPUT inserted.[BookingId] INTO @InsertedBookingId
    VALUES
    (@ProviderId, NULL, @ServiceId, @ServiceCategory, @SubCategory,
     NULL, @BookingDate, @StartTime, @EndTime,
     N'Custom', @CustomerName, @CustomerMobileCountryCode, @CustomerMobile,
     @AnimalType, @PetName, @ServiceLocation, @CustomerLocation,
     @PricePerHour, @JobNotes);

    DECLARE @BookingId UNIQUEIDENTIFIER = (SELECT TOP (1) [BookingId] FROM @InsertedBookingId);

    SELECT [BookingId], [ProviderId], [PetParentId], [ServiceId], [ServiceCategory], [SubCategory],
           [BookingDate], [StartTime], [EndTime], [Status],
           [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc], [ServiceItemCode],
           [Source], [CustomerName], [CustomerMobileCountryCode], [CustomerMobile],
           [AnimalType], [PetName], [ServiceLocation], [CustomerLocation],
           [PricePerHour], [JobNotes]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Booking].[CreateCustomBooking].';
GO


-- 3.10 Event.CreateEvent -----------------------------------------------------
CREATE OR ALTER PROCEDURE [Event].[CreateEvent]
    @ProviderId UNIQUEIDENTIFIER,
    @EventCategory NVARCHAR(64),
    @IsChildFriendly BIT,
    @Title NVARCHAR(200),
    @Description NVARCHAR(MAX),
    @BannerImageUrl NVARCHAR(1000) = NULL,
    @EventType NVARCHAR(32),
    @StartDate DATE,
    @EndDate DATE,
    @StartTime TIME(0),
    @EndTime TIME(0),
    @AmenitiesJson NVARCHAR(MAX) = N'[]'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[Providers]
        WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51030, 'Provider profile was not found.', 1;
    END

    DECLARE @InsertedEventId TABLE (EventId UNIQUEIDENTIFIER);

    INSERT INTO [Event].[Events]
    (
        [ProviderId], [EventCategory], [IsChildFriendly], [Title], [Description],
        [BannerImageUrl], [EventType], [StartDate], [EndDate], [StartTime], [EndTime]
    )
    OUTPUT inserted.[EventId] INTO @InsertedEventId
    VALUES
    (
        @ProviderId, @EventCategory, @IsChildFriendly, @Title, @Description,
        @BannerImageUrl, @EventType, @StartDate, @EndDate, @StartTime, @EndTime
    );

    DECLARE @EventId UNIQUEIDENTIFIER = (SELECT TOP (1) [EventId] FROM @InsertedEventId);

    INSERT INTO [Event].[EventAmenities] ([EventId], [Amenity])
    SELECT DISTINCT @EventId, [value]
    FROM OPENJSON(@AmenitiesJson)
    WHERE [value] IS NOT NULL AND LEN(LTRIM(RTRIM([value]))) > 0;

    SELECT [EventId], [ProviderId], [EventCategory], [IsChildFriendly], [Title],
           [Description], [BannerImageUrl], [EventType], [StartDate], [EndDate],
           [StartTime], [EndTime], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Event].[Events]
    WHERE [EventId] = @EventId;

    SELECT [Amenity]
    FROM [Event].[EventAmenities]
    WHERE [EventId] = @EventId
    ORDER BY [Amenity];

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Event].[CreateEvent].';
GO


-- 3.11 Event.GetEvent --------------------------------------------------------
CREATE OR ALTER PROCEDURE [Event].[GetEvent]
    @EventId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [EventId], [ProviderId], [EventCategory], [IsChildFriendly], [Title],
           [Description], [BannerImageUrl], [EventType], [StartDate], [EndDate],
           [StartTime], [EndTime], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Event].[Events]
    WHERE [EventId] = @EventId;

    SELECT [Amenity]
    FROM [Event].[EventAmenities]
    WHERE [EventId] = @EventId
    ORDER BY [Amenity];
END;
GO
PRINT 'Created/updated [Event].[GetEvent].';
GO


-- 3.12 Event.ListEventsByProvider --------------------------------------------
CREATE OR ALTER PROCEDURE [Event].[ListEventsByProvider]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [EventId], [ProviderId], [EventCategory], [IsChildFriendly], [Title],
           [Description], [BannerImageUrl], [EventType], [StartDate], [EndDate],
           [StartTime], [EndTime], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Event].[Events]
    WHERE [ProviderId] = @ProviderId
    ORDER BY [StartDate] DESC, [StartTime] DESC;

    SELECT a.[EventId], a.[Amenity]
    FROM [Event].[EventAmenities] a
    INNER JOIN [Event].[Events] e ON e.[EventId] = a.[EventId]
    WHERE e.[ProviderId] = @ProviderId
    ORDER BY a.[EventId], a.[Amenity];
END;
GO
PRINT 'Created/updated [Event].[ListEventsByProvider].';
GO


-- 3.12b Event.ListEvents (catalogue-wide list with optional filters) ---------
CREATE OR ALTER PROCEDURE [Event].[ListEvents]
    @EventCategory   NVARCHAR(64)  = NULL,
    @EventType       NVARCHAR(32)  = NULL,
    @StartDate       DATE          = NULL,
    @EndDate         DATE          = NULL,
    @IsChildFriendly BIT           = NULL,
    -- JSON array of amenity codes (e.g. N'["Restrooms","FreeParking"]').
    -- When supplied, only events that carry EVERY listed amenity are returned.
    @AmenitiesJson   NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Amenities TABLE ([Amenity] NVARCHAR(64) NOT NULL PRIMARY KEY);

    IF (@AmenitiesJson IS NOT NULL AND LTRIM(RTRIM(@AmenitiesJson)) <> N'')
    BEGIN
        INSERT INTO @Amenities ([Amenity])
        SELECT DISTINCT [value]
        FROM OPENJSON(@AmenitiesJson)
        WHERE [value] IS NOT NULL AND LTRIM(RTRIM([value])) <> N'';
    END

    DECLARE @AmenityCount INT = (SELECT COUNT(*) FROM @Amenities);

    ;WITH FilteredEvents AS
    (
        SELECT e.[EventId]
        FROM [Event].[Events] e
        WHERE (@EventCategory   IS NULL OR e.[EventCategory]   = @EventCategory)
          AND (@EventType       IS NULL OR e.[EventType]       = @EventType)
          AND (@IsChildFriendly IS NULL OR e.[IsChildFriendly] = @IsChildFriendly)
          AND (@StartDate IS NULL OR e.[EndDate]   >= @StartDate)
          AND (@EndDate   IS NULL OR e.[StartDate] <= @EndDate)
          AND (
                @AmenityCount = 0
                OR @AmenityCount = (
                    SELECT COUNT(DISTINCT a.[Amenity])
                    FROM [Event].[EventAmenities] a
                    INNER JOIN @Amenities f ON f.[Amenity] = a.[Amenity]
                    WHERE a.[EventId] = e.[EventId])
              )
    )
    SELECT e.[EventId], e.[ProviderId], e.[EventCategory], e.[IsChildFriendly],
           e.[Title], e.[Description], e.[BannerImageUrl], e.[EventType],
           e.[StartDate], e.[EndDate], e.[StartTime], e.[EndTime],
           e.[CreatedAtUtc], e.[UpdatedAtUtc]
    FROM [Event].[Events] e
    INNER JOIN FilteredEvents f ON f.[EventId] = e.[EventId]
    ORDER BY e.[StartDate] DESC, e.[StartTime] DESC, e.[EventId] ASC;

    SELECT a.[EventId], a.[Amenity]
    FROM [Event].[EventAmenities] a
    INNER JOIN [Event].[Events] e ON e.[EventId] = a.[EventId]
    WHERE (@EventCategory   IS NULL OR e.[EventCategory]   = @EventCategory)
      AND (@EventType       IS NULL OR e.[EventType]       = @EventType)
      AND (@IsChildFriendly IS NULL OR e.[IsChildFriendly] = @IsChildFriendly)
      AND (@StartDate IS NULL OR e.[EndDate]   >= @StartDate)
      AND (@EndDate   IS NULL OR e.[StartDate] <= @EndDate)
      AND (
            @AmenityCount = 0
            OR @AmenityCount = (
                SELECT COUNT(DISTINCT a2.[Amenity])
                FROM [Event].[EventAmenities] a2
                INNER JOIN @Amenities ff ON ff.[Amenity] = a2.[Amenity]
                WHERE a2.[EventId] = e.[EventId])
          )
    ORDER BY a.[EventId], a.[Amenity];
END;
GO
PRINT 'Created/updated [Event].[ListEvents].';
GO


-- 3.21 Provider.CreateClosures (batch insert, one row per service id) --------
-- Replaces the singular [Provider].[CreateClosure] sproc. The old name is
-- dropped below to keep the schema tidy.
IF OBJECT_ID(N'[Provider].[CreateClosure]', N'P') IS NOT NULL
BEGIN
    DROP PROCEDURE [Provider].[CreateClosure];
    PRINT 'Dropped legacy [Provider].[CreateClosure].';
END
GO

CREATE OR ALTER PROCEDURE [Provider].[CreateClosures]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceIds [Provider].[ServiceIdList] READONLY,
    @StartDate DATE,
    @EndDate DATE,
    @StartTime TIME(0) = NULL,
    @EndTime TIME(0) = NULL,
    @Reason NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM @ServiceIds)
        THROW 51075, 'At least one service id is required.', 1;

    IF NOT EXISTS (SELECT 1 FROM [Provider].[Providers] WHERE [ProviderId] = @ProviderId)
        THROW 51070, 'Provider profile was not found.', 1;

    IF EXISTS (
        SELECT 1
        FROM @ServiceIds AS s
        LEFT JOIN [Provider].[ProviderServices] AS ps WITH (UPDLOCK, HOLDLOCK)
            ON s.[ServiceId] = ps.[ServiceId]
        WHERE ps.[ServiceId] IS NULL
           OR ps.[ProviderId] <> @ProviderId
           OR ps.[IsActive] = 0
    )
        THROW 51072, 'One or more service ids are not valid or active for this provider.', 1;

    DECLARE @Conflicts TABLE (
        ServiceId UNIQUEIDENTIFIER NOT NULL,
        BookingId UNIQUEIDENTIFIER NOT NULL,
        PetParentId UNIQUEIDENTIFIER NULL,
        Source NVARCHAR(16) NOT NULL,
        CustomerName NVARCHAR(200) NULL,
        BookingDate DATE NOT NULL,
        StartTime TIME(0) NOT NULL,
        EndTime TIME(0) NOT NULL
    );

    INSERT INTO @Conflicts (ServiceId, BookingId, PetParentId, Source, CustomerName, BookingDate, StartTime, EndTime)
    SELECT b.[ServiceId], b.[BookingId], b.[PetParentId], b.[Source], b.[CustomerName],
           b.[BookingDate], b.[StartTime], b.[EndTime]
    FROM [Booking].[Bookings] AS b WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN @ServiceIds AS s ON s.[ServiceId] = b.[ServiceId]
    WHERE b.[Status] = N'Confirmed'
      AND b.[BookingDate] BETWEEN @StartDate AND @EndDate
      AND (
          @StartTime IS NULL
          OR (b.[StartTime] < @EndTime AND b.[EndTime] > @StartTime)
      );

    IF EXISTS (SELECT 1 FROM @Conflicts)
    BEGIN
        SELECT ServiceId, BookingId, PetParentId, Source, CustomerName,
               BookingDate, StartTime, EndTime
        FROM @Conflicts
        ORDER BY ServiceId, BookingDate, StartTime;
        ROLLBACK TRANSACTION;
        RETURN;
    END

    DECLARE @Inserted TABLE (
        ClosureId UNIQUEIDENTIFIER,
        ProviderId UNIQUEIDENTIFIER,
        ServiceId UNIQUEIDENTIFIER,
        StartDate DATE,
        EndDate DATE,
        StartTime TIME(0),
        EndTime TIME(0),
        Reason NVARCHAR(500),
        CreatedAtUtc DATETIME2(7)
    );

    INSERT INTO [Provider].[ProviderClosures]
        ([ProviderId], [ServiceId], [StartDate], [EndDate], [StartTime], [EndTime], [Reason])
    OUTPUT inserted.[ClosureId], inserted.[ProviderId], inserted.[ServiceId],
           inserted.[StartDate], inserted.[EndDate], inserted.[StartTime], inserted.[EndTime],
           inserted.[Reason], inserted.[CreatedAtUtc]
    INTO @Inserted
    SELECT @ProviderId, s.[ServiceId], @StartDate, @EndDate, @StartTime, @EndTime, @Reason
    FROM @ServiceIds AS s;

    SELECT ClosureId, ProviderId, ServiceId, StartDate, EndDate, StartTime, EndTime, Reason, CreatedAtUtc
    FROM @Inserted
    ORDER BY ServiceId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[CreateClosures].';
GO


-- 3.22 Provider.ListClosures -------------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[ListClosures]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceId UNIQUEIDENTIFIER = NULL,
    @From DATE = NULL,
    @To DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ClosureId], [ProviderId], [ServiceId], [StartDate], [EndDate],
           [StartTime], [EndTime], [Reason], [CreatedAtUtc]
    FROM [Provider].[ProviderClosures]
    WHERE [ProviderId] = @ProviderId
      AND (@ServiceId IS NULL OR [ServiceId] = @ServiceId)
      AND (@To   IS NULL OR [StartDate] <= @To)
      AND (@From IS NULL OR [EndDate]   >= @From)
    ORDER BY [StartDate], [StartTime];
END;
GO
PRINT 'Created/updated [Provider].[ListClosures].';
GO


-- 3.23 Provider.DeleteClosure ------------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[DeleteClosure]
    @ProviderId UNIQUEIDENTIFIER,
    @ClosureId  UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM [Provider].[ProviderClosures]
    WHERE [ClosureId] = @ClosureId
      AND [ProviderId] = @ProviderId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51071, 'Provider closure was not found for this provider.', 1;
    END
END;
GO
PRINT 'Created/updated [Provider].[DeleteClosure].';
GO


-- 3.24 Provider.GetActiveClosuresForDate -------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[GetActiveClosuresForDate]
    @ServiceId UNIQUEIDENTIFIER,
    @Date DATE
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ClosureId], [StartDate], [EndDate], [StartTime], [EndTime], [Reason]
    FROM [Provider].[ProviderClosures]
    WHERE [ServiceId] = @ServiceId
      AND @Date BETWEEN [StartDate] AND [EndDate]
    ORDER BY [StartTime];
END;
GO
PRINT 'Created/updated [Provider].[GetActiveClosuresForDate].';
GO


-- 3.25 Provider.UpsertProviderService ----------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[UpsertProviderService]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceCategory NVARCHAR(64),
    @SubCategory NVARCHAR(64),
    @ServiceType NVARCHAR(64),
    @ServiceId UNIQUEIDENTIFIER = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1 FROM [Provider].[Providers] WHERE [ProviderId] = @ProviderId
    )
        THROW 51080, 'Provider profile was not found.', 1;

    SELECT @ServiceId = [ServiceId]
    FROM [Provider].[ProviderServices] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId AND [ServiceType] = @ServiceType;

    IF @ServiceId IS NULL
    BEGIN
        SET @ServiceId = NEWID();
        INSERT INTO [Provider].[ProviderServices]
            ([ServiceId], [ProviderId], [ServiceCategory], [SubCategory], [ServiceType], [IsActive])
        VALUES
            (@ServiceId, @ProviderId, @ServiceCategory, @SubCategory, @ServiceType, 1);
    END
    ELSE
    BEGIN
        UPDATE [Provider].[ProviderServices]
        SET [ServiceCategory] = @ServiceCategory,
            [SubCategory]     = @SubCategory,
            [IsActive]        = 1,
            [UpdatedAtUtc]    = @Now
        WHERE [ServiceId] = @ServiceId;
    END

    SELECT [ServiceId], [ProviderId], [ServiceCategory], [SubCategory],
           [ServiceType], [IsActive], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Provider].[ProviderServices]
    WHERE [ServiceId] = @ServiceId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[UpsertProviderService].';
GO


-- 3.26 Provider.DeactivateProviderService ------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[DeactivateProviderService]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceType NVARCHAR(64)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE [Provider].[ProviderServices]
    SET [IsActive] = 0,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [ProviderId] = @ProviderId
      AND [ServiceType] = @ServiceType
      AND [IsActive] = 1;
END;
GO
PRINT 'Created/updated [Provider].[DeactivateProviderService].';
GO


-- 3.27 Provider.ListProviderServices -----------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[ListProviderServices]
    @ProviderId UNIQUEIDENTIFIER,
    @IncludeInactive BIT = 0
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ServiceId], [ProviderId], [ServiceCategory], [SubCategory],
           [ServiceType], [IsActive], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Provider].[ProviderServices]
    WHERE [ProviderId] = @ProviderId
      AND (@IncludeInactive = 1 OR [IsActive] = 1)
    ORDER BY [ServiceType];
END;
GO
PRINT 'Created/updated [Provider].[ListProviderServices].';
GO


-- 3.28 Provider.GetProviderService -------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[GetProviderService]
    @ServiceId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ServiceId], [ProviderId], [ServiceCategory], [SubCategory],
           [ServiceType], [IsActive], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Provider].[ProviderServices]
    WHERE [ServiceId] = @ServiceId;
END;
GO
PRINT 'Created/updated [Provider].[GetProviderService].';
GO


-- 3.29 Event.CreateEventBooking ------------------------------------------------
CREATE OR ALTER PROCEDURE [Event].[CreateEventBooking]
    @EventId UNIQUEIDENTIFIER,
    @BookerName NVARCHAR(200),
    @BookerEmail NVARCHAR(320),
    @BookerMobile NVARCHAR(32) = NULL,
    @PaymentMethod NVARCHAR(32),
    @MaximumCapacity INT,
    @TotalAmount DECIMAL(18, 2),
    @AttendeeNames [Event].[EventBookingAttendeeNames] READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @TicketCount INT = (SELECT COUNT(*) FROM @AttendeeNames);

    IF @TicketCount < 1
        THROW 51094, 'At least one attendee name is required.', 1;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1 FROM [Event].[Events] WHERE [EventId] = @EventId
    )
        THROW 51090, 'Event was not found.', 1;

    DECLARE @ReservedTickets INT;
    SELECT @ReservedTickets = ISNULL(SUM([TicketCount]), 0)
    FROM [Event].[EventBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [EventId] = @EventId
      AND [Status] = N'Confirmed';

    IF @ReservedTickets + @TicketCount > @MaximumCapacity
        THROW 51091, 'Event is sold out or does not have enough remaining capacity.', 1;

    DECLARE @InsertedBookingId TABLE ([BookingId] UNIQUEIDENTIFIER);

    INSERT INTO [Event].[EventBookings]
        ([EventId], [BookerName], [BookerEmail], [BookerMobile],
         [TicketCount], [PaymentMethod], [TotalAmount])
    OUTPUT inserted.[BookingId] INTO @InsertedBookingId
    VALUES
        (@EventId, @BookerName, @BookerEmail, @BookerMobile,
         @TicketCount, @PaymentMethod, @TotalAmount);

    DECLARE @BookingId UNIQUEIDENTIFIER = (SELECT TOP (1) [BookingId] FROM @InsertedBookingId);

    INSERT INTO [Event].[EventBookingTickets]
        ([BookingId], [EventId], [TicketNumber], [AttendeeName])
    SELECT @BookingId, @EventId, a.[TicketNumber], a.[AttendeeName]
    FROM @AttendeeNames AS a;

    SELECT [BookingId], [EventId], [BookerName], [BookerEmail], [BookerMobile],
           [TicketCount], [PaymentMethod], [PaymentStatus], [PaymentReference],
           [TotalAmount], [Status], [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc]
    FROM [Event].[EventBookings]
    WHERE [BookingId] = @BookingId;

    SELECT [TicketId], [BookingId], [EventId], [TicketNumber], [AttendeeName], [CreatedAtUtc]
    FROM [Event].[EventBookingTickets]
    WHERE [BookingId] = @BookingId
    ORDER BY [TicketNumber];

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Event].[CreateEventBooking].';
GO


-- 3.30 Event.GetEventBooking ----------------------------------------------------
CREATE OR ALTER PROCEDURE [Event].[GetEventBooking]
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingId], [EventId], [BookerName], [BookerEmail], [BookerMobile],
           [TicketCount], [PaymentMethod], [PaymentStatus], [PaymentReference],
           [TotalAmount], [Status], [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc]
    FROM [Event].[EventBookings]
    WHERE [BookingId] = @BookingId;

    SELECT [TicketId], [BookingId], [EventId], [TicketNumber], [AttendeeName], [CreatedAtUtc]
    FROM [Event].[EventBookingTickets]
    WHERE [BookingId] = @BookingId
    ORDER BY [TicketNumber];
END;
GO
PRINT 'Created/updated [Event].[GetEventBooking].';
GO


-- 3.31 Event.ConfirmEventBookingPayment ---------------------------------------
CREATE OR ALTER PROCEDURE [Event].[ConfirmEventBookingPayment]
    @BookingId UNIQUEIDENTIFIER,
    @PaymentStatus NVARCHAR(32),
    @PaymentReference NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @PaymentStatus NOT IN (N'Paid', N'Failed')
        THROW 51094, 'PaymentStatus must be Paid or Failed.', 1;

    BEGIN TRANSACTION;

    DECLARE @CurrentStatus NVARCHAR(32);
    DECLARE @CurrentReference NVARCHAR(200);
    SELECT @CurrentStatus = [PaymentStatus],
           @CurrentReference = [PaymentReference]
    FROM [Event].[EventBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
        THROW 51092, 'Event booking was not found.', 1;

    IF @CurrentStatus = @PaymentStatus
       AND ISNULL(@CurrentReference, N'') = ISNULL(@PaymentReference, N'')
    BEGIN
        SET @PaymentStatus = @CurrentStatus;
    END
    ELSE IF @CurrentStatus IN (N'Paid', N'Failed')
    BEGIN
        THROW 51093, 'Event booking payment has already been confirmed with a different result.', 1;
    END
    ELSE
    BEGIN
        UPDATE [Event].[EventBookings]
        SET [PaymentStatus]    = @PaymentStatus,
            [PaymentReference] = @PaymentReference,
            [UpdatedAtUtc]     = SYSUTCDATETIME()
        WHERE [BookingId] = @BookingId;
    END

    SELECT [BookingId], [EventId], [BookerName], [BookerEmail], [BookerMobile],
           [TicketCount], [PaymentMethod], [PaymentStatus], [PaymentReference],
           [TotalAmount], [Status], [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc]
    FROM [Event].[EventBookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Event].[ConfirmEventBookingPayment].';
GO


-- 3.32 Event.IncrementEventCounter -------------------------------------------
CREATE OR ALTER PROCEDURE [Event].[IncrementEventCounter]
    @EventId UNIQUEIDENTIFIER,
    @CounterType NVARCHAR(16)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @CounterType NOT IN (N'View', N'Share', N'Inquiry')
        THROW 51097, 'CounterType must be View, Share, or Inquiry.', 1;

    DECLARE @RowsAffected INT;

    IF @CounterType = N'View'
    BEGIN
        UPDATE [Event].[Events]
        SET [ViewCount] = [ViewCount] + 1
        WHERE [EventId] = @EventId;
        SET @RowsAffected = @@ROWCOUNT;
    END
    ELSE IF @CounterType = N'Share'
    BEGIN
        UPDATE [Event].[Events]
        SET [ShareCount] = [ShareCount] + 1
        WHERE [EventId] = @EventId;
        SET @RowsAffected = @@ROWCOUNT;
    END
    ELSE
    BEGIN
        UPDATE [Event].[Events]
        SET [InquiryCount] = [InquiryCount] + 1
        WHERE [EventId] = @EventId;
        SET @RowsAffected = @@ROWCOUNT;
    END

    IF @RowsAffected = 0
        THROW 51096, 'Event was not found.', 1;

    SELECT [ViewCount], [ShareCount], [InquiryCount]
    FROM [Event].[Events]
    WHERE [EventId] = @EventId;
END;
GO
PRINT 'Created/updated [Event].[IncrementEventCounter].';
GO


-- 3.33 Event.GetEventMetrics --------------------------------------------------
CREATE OR ALTER PROCEDURE [Event].[GetEventMetrics]
    @ProviderId UNIQUEIDENTIFIER,
    @EventId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ViewCount INT;
    DECLARE @ShareCount INT;
    DECLARE @InquiryCount INT;
    SELECT @ViewCount = [ViewCount],
           @ShareCount = [ShareCount],
           @InquiryCount = [InquiryCount]
    FROM [Event].[Events]
    WHERE [EventId] = @EventId
      AND [ProviderId] = @ProviderId;

    IF @ViewCount IS NULL
        THROW 51095, 'Event was not found.', 1;

    DECLARE @ConfirmedAttendees INT;
    DECLARE @Earnings DECIMAL(18, 2);

    SELECT @ConfirmedAttendees = ISNULL(SUM([TicketCount]), 0),
           @Earnings = ISNULL(SUM([TotalAmount]), 0)
    FROM [Event].[EventBookings]
    WHERE [EventId] = @EventId
      AND [Status] = N'Confirmed'
      AND [PaymentStatus] = N'Paid';

    SELECT @ViewCount       AS [ViewCount],
           @ShareCount      AS [ShareCount],
           @InquiryCount    AS [InquiryCount],
           @ConfirmedAttendees AS [ConfirmedAttendees],
           @Earnings        AS [Earnings];
END;
GO
PRINT 'Created/updated [Event].[GetEventMetrics].';
GO


-- 3.34 Event.ListEventAttendees -----------------------------------------------
CREATE OR ALTER PROCEDURE [Event].[ListEventAttendees]
    @ProviderId UNIQUEIDENTIFIER,
    @EventId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (
        SELECT 1 FROM [Event].[Events]
        WHERE [EventId] = @EventId AND [ProviderId] = @ProviderId
    )
        THROW 51095, 'Event was not found.', 1;

    SELECT t.[TicketId],
           t.[BookingId],
           t.[TicketNumber],
           t.[AttendeeName],
           b.[BookerName],
           b.[BookerEmail],
           b.[BookerMobile],
           b.[PaymentMethod],
           b.[PaymentStatus],
           b.[TotalAmount],
           t.[CreatedAtUtc]
    FROM [Event].[EventBookingTickets] t
    INNER JOIN [Event].[EventBookings] b ON b.[BookingId] = t.[BookingId]
    WHERE t.[EventId] = @EventId
      AND b.[Status] = N'Confirmed'
    ORDER BY b.[CreatedAtUtc] ASC, t.[TicketNumber] ASC;
END;
GO
PRINT 'Created/updated [Event].[ListEventAttendees].';
GO


PRINT '--- Pawfront deployment complete ---';
GO
