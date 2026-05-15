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
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Bookings' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[Bookings]
    (
        [BookingId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_Bookings_BookingId] DEFAULT NEWSEQUENTIALID(),
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [PetParentId] UNIQUEIDENTIFIER NOT NULL,
        [ServiceCategory] NVARCHAR(64) NOT NULL,
        [SubCategory] NVARCHAR(64) NOT NULL,
        [BookingDate] DATE NOT NULL,
        [StartTime] TIME(0) NOT NULL,
        [EndTime] TIME(0) NOT NULL,
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
        CONSTRAINT [CK_Bookings_TimeOrder] CHECK ([StartTime] < [EndTime]),
        CONSTRAINT [CK_Bookings_Status]
            CHECK ([Status] IN (N'Confirmed', N'Cancelled', N'Completed', N'NoShow')),
        CONSTRAINT [CK_Bookings_CancelledRequiresTimestamp] CHECK (
            ([Status] = N'Cancelled' AND [CancelledAtUtc] IS NOT NULL)
            OR ([Status] <> N'Cancelled')
        )
    );
    PRINT 'Created table [Booking].[Bookings].';
END
ELSE
BEGIN
    PRINT 'Table [Booking].[Bookings] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Bookings_Provider_Date_Status'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
    CREATE INDEX [IX_Bookings_Provider_Date_Status]
        ON [Booking].[Bookings] ([ProviderId], [BookingDate], [Status])
        INCLUDE ([StartTime], [EndTime], [BookingId], [PetParentId]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Bookings_PetParent_Status'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
    CREATE INDEX [IX_Bookings_PetParent_Status]
        ON [Booking].[Bookings] ([PetParentId], [Status])
        INCLUDE ([BookingDate], [StartTime], [EndTime], [ProviderId]);
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
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[CompleteProviderProfile].';
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
    @ServiceCategory NVARCHAR(64),
    @SubCategory NVARCHAR(64),
    @BookingDate DATE,
    @StartTime TIME(0),
    @EndTime TIME(0),
    @Capacity INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM [Provider].[Providers] WHERE [ProviderId] = @ProviderId)
        THROW 51061, 'Provider was not found.', 1;

    IF NOT EXISTS (SELECT 1 FROM [Customer].[PetParents] WHERE [PetParentId] = @PetParentId)
        THROW 51060, 'Pet parent was not found.', 1;

    DECLARE @Concurrent INT;
    SELECT @Concurrent = COUNT(*)
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId
      AND [BookingDate] = @BookingDate
      AND [Status] = N'Confirmed'
      AND [StartTime] < @EndTime
      AND [EndTime] > @StartTime;

    IF @Concurrent >= @Capacity
        THROW 51062, 'No remaining capacity for this slot.', 1;

    DECLARE @InsertedBookingId TABLE ([BookingId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[Bookings]
    ([ProviderId], [PetParentId], [ServiceCategory], [SubCategory],
     [BookingDate], [StartTime], [EndTime])
    OUTPUT inserted.[BookingId] INTO @InsertedBookingId
    VALUES (@ProviderId, @PetParentId, @ServiceCategory, @SubCategory,
            @BookingDate, @StartTime, @EndTime);

    DECLARE @BookingId UNIQUEIDENTIFIER = (SELECT TOP (1) [BookingId] FROM @InsertedBookingId);

    SELECT [BookingId], [ProviderId], [PetParentId], [ServiceCategory], [SubCategory],
           [BookingDate], [StartTime], [EndTime], [Status],
           [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc]
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

    SELECT [BookingId], [ProviderId], [PetParentId], [ServiceCategory], [SubCategory],
           [BookingDate], [StartTime], [EndTime], [Status],
           [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc]
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

    SELECT [BookingId], [ProviderId], [PetParentId], [ServiceCategory], [SubCategory],
           [BookingDate], [StartTime], [EndTime], [Status],
           [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Booking].[CancelBooking].';
GO


-- 3.18 Booking.ListBookingsByProvider ----------------------------------------
CREATE OR ALTER PROCEDURE [Booking].[ListBookingsByProvider]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingId], [ProviderId], [PetParentId], [ServiceCategory], [SubCategory],
           [BookingDate], [StartTime], [EndTime], [Status],
           [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc]
    FROM [Booking].[Bookings]
    WHERE [ProviderId] = @ProviderId
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

    SELECT [BookingId], [ProviderId], [PetParentId], [ServiceCategory], [SubCategory],
           [BookingDate], [StartTime], [EndTime], [Status],
           [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc]
    FROM [Booking].[Bookings]
    WHERE [PetParentId] = @PetParentId
    ORDER BY [BookingDate] DESC, [StartTime] DESC;
END;
GO
PRINT 'Created/updated [Booking].[ListBookingsByPetParent].';
GO


-- 3.20 Booking.GetBookingsForDate --------------------------------------------
CREATE OR ALTER PROCEDURE [Booking].[GetBookingsForDate]
    @ProviderId UNIQUEIDENTIFIER,
    @BookingDate DATE
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [StartTime], [EndTime]
    FROM [Booking].[Bookings]
    WHERE [ProviderId] = @ProviderId
      AND [BookingDate] = @BookingDate
      AND [Status] = N'Confirmed'
    ORDER BY [StartTime];
END;
GO
PRINT 'Created/updated [Booking].[GetBookingsForDate].';
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


PRINT '--- Pawfront deployment complete ---';
GO
