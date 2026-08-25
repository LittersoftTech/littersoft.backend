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

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = N'Parent')
BEGIN
    EXEC ('CREATE SCHEMA [Parent]');
    PRINT 'Created schema [Parent].';
END
ELSE
BEGIN
    PRINT 'Schema [Parent] already exists.';
END
GO

-- Migration: relocate legacy [Customer].[PetParents] / [Customer].[Pets] tables
-- (if they exist from a pre-Parent-schema deploy) into the new [Parent] schema.
-- The FK from [Booking].[Bookings].[PetParentId] is stored by object id so it
-- survives ALTER SCHEMA TRANSFER without needing to be re-created.
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Pets' AND [schema_id] = SCHEMA_ID(N'Customer'))
AND NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Pets' AND [schema_id] = SCHEMA_ID(N'Parent'))
BEGIN
    ALTER SCHEMA [Parent] TRANSFER [Customer].[Pets];
    PRINT 'Transferred [Customer].[Pets] to [Parent].[Pets].';
END
GO

IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'PetParents' AND [schema_id] = SCHEMA_ID(N'Customer'))
AND NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'PetParents' AND [schema_id] = SCHEMA_ID(N'Parent'))
BEGIN
    ALTER SCHEMA [Parent] TRANSFER [Customer].[PetParents];
    PRINT 'Transferred [Customer].[PetParents] to [Parent].[PetParents].';
END
GO

-- Drop the legacy [Customer] schema once both tables have moved. Skipped if
-- anything still lives there.
IF EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = N'Customer')
   AND NOT EXISTS (
       SELECT 1 FROM sys.objects WHERE [schema_id] = SCHEMA_ID(N'Customer'))
BEGIN
    EXEC ('DROP SCHEMA [Customer]');
    PRINT 'Dropped empty legacy schema [Customer].';
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

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = N'Notification')
BEGIN
    EXEC ('CREATE SCHEMA [Notification]');
    PRINT 'Created schema [Notification].';
END
ELSE
BEGIN
    PRINT 'Schema [Notification] already exists.';
END
GO

-- Reviews exchanged between the parties to a finished booking. Its own schema
-- because a review is about neither party's profile nor the booking itself, and
-- because the subject set is expected to widen (events were scoped out of the
-- first cut) — [Booking] would have been the wrong home for that.
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = N'Review')
BEGIN
    EXEC ('CREATE SCHEMA [Review]');
    PRINT 'Created schema [Review].';
END
ELSE
BEGIN
    PRINT 'Schema [Review] already exists.';
END
GO

-- Provider <-> pet-parent messaging. Its own schema because a conversation
-- belongs to neither party's profile and to no booking: chat is OPEN, so a thread
-- can exist between two people who have never transacted. [Chat] owns the thread
-- index, per-side read state, live connections and blocks; the message BODIES
-- live in the Cosmos "ChatMessages" container, partitioned by conversation --
-- the same SQL-owns-relationships / Cosmos-owns-volume split as
-- [Event].[Events] + the Events document.
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = N'Chat')
BEGIN
    EXEC ('CREATE SCHEMA [Chat]');
    PRINT 'Created schema [Chat].';
END
ELSE
BEGIN
    PRINT 'Schema [Chat] already exists.';
END
GO

-- Support tickets raised by one party against the other ("Report Incident" on a
-- booking, "Report Chat" on a conversation). Its own schema because a ticket
-- belongs to neither party's profile, to no booking and to no thread -- it is
-- ABOUT one of those, and it outlives all of them. [Support] owns the ticket
-- index, its photos and the status the legal hold reads; the narrative and the
-- clarification thread live in the Cosmos "SupportTickets" container, the same
-- SQL-owns-relationships / Cosmos-owns-volume split as [Chat] uses.
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = N'Support')
BEGIN
    EXEC ('CREATE SCHEMA [Support]');
    PRINT 'Created schema [Support].';
END
ELSE
BEGIN
    PRINT 'Schema [Support] already exists.';
END
GO

-- Blocks placed by one party against the other. Its own schema because a block is
-- no longer a chat remedy: it stops new bookings, hides each party's events from
-- the other and removes the provider from discovery, as well as stopping
-- messages. It lived under [Chat] while messaging was the only thing it governed,
-- and the migration below moves it -- leaving it there would leave the schema
-- name asserting something the table no longer does.
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE [name] = N'Block')
BEGIN
    EXEC ('CREATE SCHEMA [Block]');
    PRINT 'Created schema [Block].';
END
ELSE
BEGIN
    PRINT 'Schema [Block] already exists.';
END
GO

-- Migration: relocate [Block].[BlockedParticipants] into the new [Block] schema.
-- There is no FK to or from this table in either direction, so the transfer moves
-- the rows, the PK, the UNIQUE pair key, all three CHECKs and the reverse index
-- without anything needing to be re-created.
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'BlockedParticipants' AND [schema_id] = SCHEMA_ID(N'Chat'))
AND NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'BlockedParticipants' AND [schema_id] = SCHEMA_ID(N'Block'))
BEGIN
    ALTER SCHEMA [Block] TRANSFER [Block].[BlockedParticipants];
    PRINT 'Transferred [Block].[BlockedParticipants] to [Block].[BlockedParticipants].';
END
GO

-- Migration: [ChatBlockId] -> [BlockId]. The id names a block on a person, not a
-- block on a conversation, and every caller outside chat would have had to read a
-- column called "chat" something. Guarded on the old column still existing, so a
-- re-run is a no-op. sp_rename warns that dependent objects are not updated --
-- harmless here, since every procedure that reads this table is CREATE OR ALTERed
-- further down this same script.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Block].[BlockedParticipants]')
      AND [name] = N'ChatBlockId')
BEGIN
    EXEC sp_rename N'[Block].[BlockedParticipants].[ChatBlockId]', N'BlockId', 'COLUMN';
    PRINT 'Renamed [Block].[BlockedParticipants].[ChatBlockId] to [BlockId].';
END
GO

-- The DEFAULT that fills it carried the old column name too.
IF EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE [name] = N'DF_BlockedParticipants_ChatBlockId'
      AND [parent_object_id] = OBJECT_ID(N'[Block].[BlockedParticipants]'))
BEGIN
    EXEC sp_rename N'[Block].[DF_BlockedParticipants_ChatBlockId]',
                   N'DF_BlockedParticipants_BlockId', 'OBJECT';
    PRINT 'Renamed DF_BlockedParticipants_ChatBlockId to DF_BlockedParticipants_BlockId.';
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
        -- Wide banner shown on the provider's card in parent-facing search
        -- results. Provider-level (category-agnostic) and captured during
        -- registration, so it can be set before any ProviderServices row
        -- exists — distinct from [Provider].[ProviderServiceBanners].
        [BannerImageUrl] NVARCHAR(1000) NULL,
        [OnboardingStatus] NVARCHAR(32) NOT NULL
            CONSTRAINT [DF_Providers_OnboardingStatus] DEFAULT N'MobileVerificationPending',
        -- Master Active/Inactive switch. When 0, no new bookings can be created on
        -- ANY of this provider's services (Booking.CreateBooking enforces). Flipped
        -- via [Provider].[SetProviderActiveStatus]; the deactivation path rejects
        -- the toggle if future confirmed bookings still exist, unless the caller
        -- passes @AcknowledgeExistingBookings = 1 to honour them.
        [IsActive] BIT NOT NULL
            CONSTRAINT [DF_Providers_IsActive] DEFAULT 1,
        -- Account-deleted marker. "Delete account" anonymises this row rather
        -- than removing it ([Provider].[DeleteProvider]) so bookings, events and
        -- the payment ledger keep their meaning. Permanent: blocks reactivation
        -- and profile edits (THROW 51115).
        [IsDeleted] BIT NOT NULL
            CONSTRAINT [DF_Providers_IsDeleted] DEFAULT 0,
        [DeletedAtUtc] DATETIME2(7) NULL,
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

-- A provider's number is (country code + number): +41 791234567 and
-- +49 791234567 are two different real numbers and must both be allowed.
-- Deployments created before the key became composite still carry a
-- [MobileNumber]-only index, which wrongly rejected the second registration
-- with 409 MobileNumberAlreadyExists. The IF NOT EXISTS guard below matches on
-- name only and so can never repair that, hence this explicit rebuild: drop the
-- index when its key columns aren't exactly (MobileCountryCode, MobileNumber),
-- then let the create re-add it. Widening a UNIQUE key can only ever admit more
-- rows, so the recreate cannot fail on existing data.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_Providers_MobileNumber'
      AND [object_id] = OBJECT_ID(N'[Provider].[Providers]'))
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    WHERE i.[name] = N'UX_Providers_MobileNumber'
      AND i.[object_id] = OBJECT_ID(N'[Provider].[Providers]')
      AND (
            SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), c.[name]), N',')
                       WITHIN GROUP (ORDER BY ic.[key_ordinal])
            FROM sys.index_columns ic
            INNER JOIN sys.columns c
                ON c.[object_id] = ic.[object_id]
               AND c.[column_id] = ic.[column_id]
            WHERE ic.[object_id] = i.[object_id]
              AND ic.[index_id] = i.[index_id]
              AND ic.[is_included_column] = 0
          ) = N'MobileCountryCode,MobileNumber')
BEGIN
    DROP INDEX [UX_Providers_MobileNumber] ON [Provider].[Providers];
    PRINT 'Dropped mis-keyed index [UX_Providers_MobileNumber]; recreating on (MobileCountryCode, MobileNumber).';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_Providers_MobileNumber'
      AND [object_id] = OBJECT_ID(N'[Provider].[Providers]'))
    CREATE UNIQUE INDEX [UX_Providers_MobileNumber]
        ON [Provider].[Providers] ([MobileCountryCode], [MobileNumber]);
GO

-- The gender picker gained NonBinary / Other / PreferNotToSay. The CREATE TABLE
-- above is skipped once the table exists, so a database created against the
-- original Male/Female-only CHECK would keep rejecting the new values. Rebuild
-- the constraint whenever its definition is out of date.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Providers_Gender'
      AND [parent_object_id] = OBJECT_ID(N'[Provider].[Providers]')
      AND [definition] NOT LIKE N'%PreferNotToSay%')
BEGIN
    ALTER TABLE [Provider].[Providers] DROP CONSTRAINT [CK_Providers_Gender];
    PRINT 'Dropped outdated constraint [CK_Providers_Gender]; recreating with the full gender set.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Providers_Gender'
      AND [parent_object_id] = OBJECT_ID(N'[Provider].[Providers]'))
BEGIN
    ALTER TABLE [Provider].[Providers]
        ADD CONSTRAINT [CK_Providers_Gender]
            CHECK ([Gender] IN (N'Male', N'Female', N'NonBinary', N'Other', N'PreferNotToSay'));
    PRINT 'Created constraint [CK_Providers_Gender].';
END
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

-- Retrofit (2026-07-25): [IsDeleted] + [DeletedAtUtc] for the account-delete
-- flow, which anonymises the provider row instead of removing it.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'IsDeleted'
      AND [object_id] = OBJECT_ID(N'[Provider].[Providers]'))
BEGIN
    ALTER TABLE [Provider].[Providers]
        ADD [IsDeleted] BIT NOT NULL
            CONSTRAINT [DF_Providers_IsDeleted] DEFAULT 0;
    PRINT 'Added column [Provider].[Providers].[IsDeleted].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'DeletedAtUtc'
      AND [object_id] = OBJECT_ID(N'[Provider].[Providers]'))
BEGIN
    ALTER TABLE [Provider].[Providers] ADD [DeletedAtUtc] DATETIME2(7) NULL;
    PRINT 'Added column [Provider].[Providers].[DeletedAtUtc].';
END
GO

-- Add [BannerImageUrl] column to existing Providers tables (idempotent for upgrades).
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'BannerImageUrl'
      AND [object_id] = OBJECT_ID(N'[Provider].[Providers]'))
BEGIN
    ALTER TABLE [Provider].[Providers]
        ADD [BannerImageUrl] NVARCHAR(1000) NULL;
    PRINT 'Added column [Provider].[Providers].[BannerImageUrl].';
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


-- 2.2a Provider.ProviderPhotos -----------------------------------------------
-- General photo gallery owned directly by a provider (not tied to a service).
-- One row per uploaded photo. ON DELETE CASCADE so removing a provider removes
-- the photo URLs (the blobs themselves are cleaned up best-effort by the
-- delete endpoint / a future sweep job).
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ProviderPhotos' AND [schema_id] = SCHEMA_ID(N'Provider'))
BEGIN
    CREATE TABLE [Provider].[ProviderPhotos]
    (
        [ProviderPhotoId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_ProviderPhotos_ProviderPhotoId] DEFAULT NEWSEQUENTIALID(),
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [PhotoUrl] NVARCHAR(1000) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderPhotos_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ProviderPhotos] PRIMARY KEY CLUSTERED ([ProviderPhotoId] ASC),
        CONSTRAINT [FK_ProviderPhotos_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId])
            ON DELETE CASCADE
    );
    PRINT 'Created table [Provider].[ProviderPhotos].';
END
ELSE
BEGIN
    PRINT 'Table [Provider].[ProviderPhotos] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ProviderPhotos_ProviderId'
      AND [object_id] = OBJECT_ID(N'[Provider].[ProviderPhotos]'))
    CREATE INDEX [IX_ProviderPhotos_ProviderId]
        ON [Provider].[ProviderPhotos] ([ProviderId])
        INCLUDE ([PhotoUrl], [CreatedAtUtc]);
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


-- 2.4b Provider.ProviderServiceBanners ----------------------------------------
-- One banner image per bookable service (ProviderServices row). Distinct from the
-- Cosmos offering image (the discovery/profile photo) — a wide banner shown on the
-- service's own screen. Upserted (one row per ServiceId). FKs ProviderServices, so
-- it is created after that table above.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ProviderServiceBanners' AND [schema_id] = SCHEMA_ID(N'Provider'))
BEGIN
    CREATE TABLE [Provider].[ProviderServiceBanners]
    (
        [ServiceId] UNIQUEIDENTIFIER NOT NULL,
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [BannerImageUrl] NVARCHAR(1000) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderServiceBanners_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ProviderServiceBanners_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ProviderServiceBanners] PRIMARY KEY CLUSTERED ([ServiceId] ASC),
        CONSTRAINT [FK_ProviderServiceBanners_ProviderServices_ServiceId]
            FOREIGN KEY ([ServiceId]) REFERENCES [Provider].[ProviderServices] ([ServiceId])
            ON DELETE CASCADE,
        CONSTRAINT [FK_ProviderServiceBanners_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId])
    );
    PRINT 'Created table [Provider].[ProviderServiceBanners].';
END
ELSE
BEGIN
    PRINT 'Table [Provider].[ProviderServiceBanners] already exists.';
END
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


-- 2.8 Parent.ParentAuthIdentities --------------------------------------------
-- Created first because Parent.PetParents now FKs to it. The reciprocal FK
-- (ParentAuthIdentities.PetParentId -> PetParents) is added as a deferred
-- ALTER below, once PetParents exists. Mirrors the Provider/ProviderAuthIdentities
-- circular-FK setup.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ParentAuthIdentities' AND [schema_id] = SCHEMA_ID(N'Parent'))
BEGIN
    CREATE TABLE [Parent].[ParentAuthIdentities]
    (
        [ParentAuthIdentityId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_ParentAuthIdentities_ParentAuthIdentityId] DEFAULT NEWSEQUENTIALID(),
        [PetParentId] UNIQUEIDENTIFIER NULL,
        [FirebaseUserId] NVARCHAR(128) NOT NULL,
        [FirebaseTenantId] NVARCHAR(128) NULL,
        [AuthProvider] NVARCHAR(32) NOT NULL,
        [FirebaseProviderId] NVARCHAR(64) NULL,
        [Email] NVARCHAR(320) NOT NULL,
        [IsEmailVerified] BIT NOT NULL
            CONSTRAINT [DF_ParentAuthIdentities_IsEmailVerified] DEFAULT 0,
        [DisplayName] NVARCHAR(200) NULL,
        [FirebasePhoneNumber] NVARCHAR(32) NULL,
        [PhotoUrl] NVARCHAR(1000) NULL,
        [SignUpStatus] NVARCHAR(32) NOT NULL
            CONSTRAINT [DF_ParentAuthIdentities_SignUpStatus] DEFAULT N'FirebaseAuthenticated',
        [LastSignedInAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ParentAuthIdentities_LastSignedInAtUtc] DEFAULT SYSUTCDATETIME(),
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ParentAuthIdentities_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ParentAuthIdentities_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ParentAuthIdentities] PRIMARY KEY CLUSTERED ([ParentAuthIdentityId] ASC),
        CONSTRAINT [UQ_ParentAuthIdentities_FirebaseUserId] UNIQUE ([FirebaseUserId]),
        CONSTRAINT [CK_ParentAuthIdentities_AuthProvider] CHECK ([AuthProvider] IN (N'Google', N'Apple', N'EmailPassword')),
        CONSTRAINT [CK_ParentAuthIdentities_SignUpStatus] CHECK ([SignUpStatus] IN (N'FirebaseAuthenticated', N'ParentProfileCompleted'))
    );
    PRINT 'Created table [Parent].[ParentAuthIdentities].';
END
ELSE
BEGIN
    PRINT 'Table [Parent].[ParentAuthIdentities] already exists.';
END
GO

-- Migration: drop the legacy FK_ParentAuthIdentities_PetParents constraint if
-- it was created in an earlier version (before PetParents was repurposed as a
-- profile table). The constraint will be recreated below after PetParents has
-- the right shape.
IF EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE [name] = N'FK_ParentAuthIdentities_PetParents_PetParentId'
      AND [parent_object_id] = OBJECT_ID(N'[Parent].[ParentAuthIdentities]'))
BEGIN
    ALTER TABLE [Parent].[ParentAuthIdentities]
        DROP CONSTRAINT [FK_ParentAuthIdentities_PetParents_PetParentId];
    PRINT 'Dropped legacy FK [FK_ParentAuthIdentities_PetParents_PetParentId] (will be re-added below).';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_ParentAuthIdentities_PetParentId'
      AND [object_id] = OBJECT_ID(N'[Parent].[ParentAuthIdentities]'))
    CREATE UNIQUE INDEX [UX_ParentAuthIdentities_PetParentId]
        ON [Parent].[ParentAuthIdentities] ([PetParentId])
        WHERE [PetParentId] IS NOT NULL;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ParentAuthIdentities_Email'
      AND [object_id] = OBJECT_ID(N'[Parent].[ParentAuthIdentities]'))
    CREATE INDEX [IX_ParentAuthIdentities_Email]
        ON [Parent].[ParentAuthIdentities] ([Email]);
GO


-- 2.9 Parent.PetParents ------------------------------------------------------
-- Profile row created by [Parent].[CompletePetParentProfile] after Firebase
-- login. FKs back to [Parent].[ParentAuthIdentities]; UNIQUE on
-- (MobileCountryCode, MobileNumber) so the same number can't register twice.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'PetParents' AND [schema_id] = SCHEMA_ID(N'Parent'))
BEGIN
    CREATE TABLE [Parent].[PetParents]
    (
        [PetParentId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_PetParents_PetParentId] DEFAULT NEWSEQUENTIALID(),
        [ParentAuthIdentityId] UNIQUEIDENTIFIER NOT NULL,
        [FirstName] NVARCHAR(100) NOT NULL,
        [LastName] NVARCHAR(100) NOT NULL,
        [Gender] NVARCHAR(32) NOT NULL,
        [MobileCountryCode] NVARCHAR(8) NOT NULL,
        [MobileNumber] NVARCHAR(32) NOT NULL,
        [DateOfBirth] DATE NOT NULL,
        [AddressLine] NVARCHAR(500) NOT NULL,
        [Latitude] DECIMAL(9, 6) NOT NULL,
        [Longitude] DECIMAL(9, 6) NOT NULL,
        [ZipCode] NVARCHAR(16) NOT NULL,
        [City] NVARCHAR(100) NOT NULL,
        [Description] NVARCHAR(2000) NOT NULL,
        [ProfilePhotoUrl] NVARCHAR(1000) NULL,
        [MobileVerifiedAtUtc] DATETIME2(7) NULL,
        -- Account delete = anonymise + permanently disable, never a row delete
        -- (see [Parent].[DeletePetParent]).
        [IsDeleted] BIT NOT NULL
            CONSTRAINT [DF_PetParents_IsDeleted] DEFAULT 0,
        [DeletedAtUtc] DATETIME2(7) NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_PetParents_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_PetParents_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_PetParents] PRIMARY KEY CLUSTERED ([PetParentId] ASC),
        CONSTRAINT [UQ_PetParents_ParentAuthIdentityId] UNIQUE ([ParentAuthIdentityId]),
        CONSTRAINT [FK_PetParents_ParentAuthIdentities_ParentAuthIdentityId]
            FOREIGN KEY ([ParentAuthIdentityId]) REFERENCES [Parent].[ParentAuthIdentities] ([ParentAuthIdentityId]),
        CONSTRAINT [CK_PetParents_Gender]
            CHECK ([Gender] IN (N'Male', N'Female', N'NonBinary', N'Other', N'PreferNotToSay')),
        CONSTRAINT [CK_PetParents_Latitude]
            CHECK ([Latitude] BETWEEN -90 AND 90),
        CONSTRAINT [CK_PetParents_Longitude]
            CHECK ([Longitude] BETWEEN -180 AND 180)
    );
    PRINT 'Created table [Parent].[PetParents].';
END
ELSE
BEGIN
    PRINT 'Table [Parent].[PetParents] already exists.';
END
GO

-- Migration: extend a legacy [Parent].[PetParents] (previously just PetParentId
-- + timestamps) with all the profile columns. Existing rows survive — added
-- columns are nullable. The CompletePetParentProfile sproc enforces non-null
-- for new rows, and the application layer normalises required inputs.
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'PetParents' AND [schema_id] = SCHEMA_ID(N'Parent'))
AND NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'FirstName'
      AND [object_id] = OBJECT_ID(N'[Parent].[PetParents]'))
BEGIN
    PRINT 'Extending [Parent].[PetParents] with profile columns (nullable for legacy rows).';

    ALTER TABLE [Parent].[PetParents]
        ADD [ParentAuthIdentityId] UNIQUEIDENTIFIER NULL,
            [FirstName] NVARCHAR(100) NULL,
            [LastName] NVARCHAR(100) NULL,
            [Gender] NVARCHAR(32) NULL,
            [MobileCountryCode] NVARCHAR(8) NULL,
            [MobileNumber] NVARCHAR(32) NULL,
            [DateOfBirth] DATE NULL,
            [AddressLine] NVARCHAR(500) NULL,
            [Latitude] DECIMAL(9, 6) NULL,
            [Longitude] DECIMAL(9, 6) NULL,
            [ZipCode] NVARCHAR(16) NULL,
            [City] NVARCHAR(100) NULL,
            [Description] NVARCHAR(2000) NULL,
            [MobileVerifiedAtUtc] DATETIME2(7) NULL;
END
GO

-- Add constraints defensively (covers both upgrade and fresh-deploy paths).
IF NOT EXISTS (
    SELECT 1 FROM sys.key_constraints
    WHERE [name] = N'UQ_PetParents_ParentAuthIdentityId'
      AND [parent_object_id] = OBJECT_ID(N'[Parent].[PetParents]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'ParentAuthIdentityId'
      AND [object_id] = OBJECT_ID(N'[Parent].[PetParents]'))
BEGIN
    -- Filtered unique index: enforces one profile per auth identity but lets
    -- legacy rows (where the column is NULL) coexist.
    IF NOT EXISTS (
        SELECT 1 FROM sys.indexes
        WHERE [name] = N'UX_PetParents_ParentAuthIdentityId'
          AND [object_id] = OBJECT_ID(N'[Parent].[PetParents]'))
        CREATE UNIQUE INDEX [UX_PetParents_ParentAuthIdentityId]
            ON [Parent].[PetParents] ([ParentAuthIdentityId])
            WHERE [ParentAuthIdentityId] IS NOT NULL;
END
GO

-- Retrofit (2026-07-27): [IsDeleted] + [DeletedAtUtc] for the account-delete
-- flow, which anonymises the pet-parent row instead of removing it.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'IsDeleted'
      AND [object_id] = OBJECT_ID(N'[Parent].[PetParents]'))
BEGIN
    ALTER TABLE [Parent].[PetParents]
        ADD [IsDeleted] BIT NOT NULL
            CONSTRAINT [DF_PetParents_IsDeleted] DEFAULT 0;
    PRINT 'Added column [Parent].[PetParents].[IsDeleted].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'DeletedAtUtc'
      AND [object_id] = OBJECT_ID(N'[Parent].[PetParents]'))
BEGIN
    ALTER TABLE [Parent].[PetParents] ADD [DeletedAtUtc] DATETIME2(7) NULL;
    PRINT 'Added column [Parent].[PetParents].[DeletedAtUtc].';
END
GO

-- Idempotent: add [ProfilePhotoUrl] to legacy PetParents rows that pre-date
-- the profile-photo upload endpoint. Nullable — populated only after the
-- parent uploads a photo.
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'PetParents' AND [schema_id] = SCHEMA_ID(N'Parent'))
AND NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'ProfilePhotoUrl'
      AND [object_id] = OBJECT_ID(N'[Parent].[PetParents]'))
BEGIN
    ALTER TABLE [Parent].[PetParents]
        ADD [ProfilePhotoUrl] NVARCHAR(1000) NULL;
    PRINT 'Added column [Parent].[PetParents].[ProfilePhotoUrl].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE [name] = N'FK_PetParents_ParentAuthIdentities_ParentAuthIdentityId'
      AND [parent_object_id] = OBJECT_ID(N'[Parent].[PetParents]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'ParentAuthIdentityId'
      AND [object_id] = OBJECT_ID(N'[Parent].[PetParents]'))
BEGIN
    ALTER TABLE [Parent].[PetParents]
        ADD CONSTRAINT [FK_PetParents_ParentAuthIdentities_ParentAuthIdentityId]
            FOREIGN KEY ([ParentAuthIdentityId])
            REFERENCES [Parent].[ParentAuthIdentities] ([ParentAuthIdentityId]);
    PRINT 'Added FK [FK_PetParents_ParentAuthIdentities_ParentAuthIdentityId].';
END
GO

-- One account per mobile number. [Parent].[CompletePetParentProfile] pre-checks
-- explicitly (THROW 51222), but this index is what makes the rule race-safe, so
-- it must actually be present and keyed on the composite (a number is the country
-- code AND the digits: +41 791234567 and +49 791234567 are two real numbers).
-- The IF NOT EXISTS guard below matches on name only and so can never repair an
-- index built on the wrong columns — hence this explicit rebuild. Widening a
-- UNIQUE key only ever admits more rows, so the recreate cannot fail on existing
-- data; a NARROWER mis-keyed index would already have rejected such rows.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_PetParents_MobileNumber'
      AND [object_id] = OBJECT_ID(N'[Parent].[PetParents]'))
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes i
    WHERE i.[name] = N'UX_PetParents_MobileNumber'
      AND i.[object_id] = OBJECT_ID(N'[Parent].[PetParents]')
      AND (
            SELECT STRING_AGG(CONVERT(NVARCHAR(MAX), c.[name]), N',')
                       WITHIN GROUP (ORDER BY ic.[key_ordinal])
            FROM sys.index_columns ic
            INNER JOIN sys.columns c
                ON c.[object_id] = ic.[object_id]
               AND c.[column_id] = ic.[column_id]
            WHERE ic.[object_id] = i.[object_id]
              AND ic.[index_id] = i.[index_id]
              AND ic.[is_included_column] = 0
          ) = N'MobileCountryCode,MobileNumber')
BEGIN
    DROP INDEX [UX_PetParents_MobileNumber] ON [Parent].[PetParents];
    PRINT 'Dropped mis-keyed index [UX_PetParents_MobileNumber]; recreating on (MobileCountryCode, MobileNumber).';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_PetParents_MobileNumber'
      AND [object_id] = OBJECT_ID(N'[Parent].[PetParents]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'MobileNumber'
      AND [object_id] = OBJECT_ID(N'[Parent].[PetParents]'))
BEGIN
    -- A database that ran without the index could already hold duplicates, and
    -- CREATE UNIQUE INDEX would then fail and abort the whole deployment. Report
    -- them instead and leave the index off: new duplicates are still refused by
    -- [Parent].[CompletePetParentProfile]'s own check, and the index goes on
    -- automatically once the listed rows have been reconciled.
    IF EXISTS (
        SELECT 1
        FROM [Parent].[PetParents]
        WHERE [MobileNumber] IS NOT NULL
        GROUP BY [MobileCountryCode], [MobileNumber]
        HAVING COUNT(*) > 1)
    BEGIN
        PRINT 'WARNING: [Parent].[PetParents] holds duplicate (MobileCountryCode, MobileNumber) rows; '
            + 'skipping [UX_PetParents_MobileNumber]. Resolve the duplicates listed below and re-run.';
        SELECT [MobileCountryCode], [MobileNumber], COUNT(*) AS [AccountCount]
        FROM [Parent].[PetParents]
        WHERE [MobileNumber] IS NOT NULL
        GROUP BY [MobileCountryCode], [MobileNumber]
        HAVING COUNT(*) > 1;
    END
    ELSE
    BEGIN
        -- Filtered so legacy rows migrated without a number don't collide; inert
        -- on fresh installs, where the column is NOT NULL.
        CREATE UNIQUE INDEX [UX_PetParents_MobileNumber]
            ON [Parent].[PetParents] ([MobileCountryCode], [MobileNumber])
            WHERE [MobileNumber] IS NOT NULL;
        PRINT 'Created unique index [UX_PetParents_MobileNumber].';
    END
END
GO

-- Deferred FK from ParentAuthIdentities back to PetParents (created now that
-- PetParents exists). Mirrors Provider's FK_ProviderAuthIdentities_Providers_ProviderId.
IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE [name] = N'FK_ParentAuthIdentities_PetParents_PetParentId'
      AND [parent_object_id] = OBJECT_ID(N'[Parent].[ParentAuthIdentities]'))
BEGIN
    ALTER TABLE [Parent].[ParentAuthIdentities]
        ADD CONSTRAINT [FK_ParentAuthIdentities_PetParents_PetParentId]
            FOREIGN KEY ([PetParentId])
            REFERENCES [Parent].[PetParents] ([PetParentId]);
    PRINT 'Added FK [FK_ParentAuthIdentities_PetParents_PetParentId].';
END
GO


-- 2.9.1 Parent.Pets ----------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Pets' AND [schema_id] = SCHEMA_ID(N'Parent'))
BEGIN
    CREATE TABLE [Parent].[Pets]
    (
        [PetId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_Pets_PetId] DEFAULT NEWSEQUENTIALID(),
        [PetParentId] UNIQUEIDENTIFIER NOT NULL,
        [PetType] NVARCHAR(32) NOT NULL,
        [PetName] NVARCHAR(100) NOT NULL,
        [Breed] NVARCHAR(100) NOT NULL,
        [Gender] NVARCHAR(16) NOT NULL,
        [DateOfBirth] DATE NOT NULL,
        [Weight] DECIMAL(5, 2) NOT NULL,
        [MicrochipId] NVARCHAR(32) NULL,
        [Description] NVARCHAR(2000) NULL,
        [VaccinationStatus] NVARCHAR(32) NULL,
        [SterilizationStatus] NVARCHAR(32) NULL,
        [MedicalHistory] NVARCHAR(MAX) NULL,
        [Temperament] NVARCHAR(32) NULL,
        -- Additional medical-info fields — free text, captured via
        -- PATCH /pets/{petId}/medical-info.
        [VaccinationType] NVARCHAR(100) NULL,
        [VaccinationDose] NVARCHAR(64) NULL,
        [Prescription] NVARCHAR(MAX) NULL,
        -- Single primary/profile photo (distinct from the gallery in PetPhotos).
        [ProfilePhotoUrl] NVARCHAR(1000) NULL,
        -- DELETE /pets/{petId} is a soft delete: the row survives because
        -- [Booking].[Bookings].[PetId] references it and the booking-detail read
        -- joins the pet through it, so removing the row would blank the pet out
        -- of the PROVIDER's history of a job they actually did.
        [IsDeleted] BIT NOT NULL
            CONSTRAINT [DF_Pets_IsDeleted] DEFAULT 0,
        [DeletedAtUtc] DATETIME2(7) NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Pets_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Pets_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_Pets] PRIMARY KEY CLUSTERED ([PetId] ASC),
        CONSTRAINT [FK_Pets_PetParents_PetParentId]
            FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId]),
        CONSTRAINT [CK_Pets_PetType]
            CHECK ([PetType] IN (N'Dog', N'Cat', N'Hamster', N'GuineaPig')),
        CONSTRAINT [CK_Pets_Gender]
            CHECK ([Gender] IN (N'Male', N'Female')),
        CONSTRAINT [CK_Pets_Weight_Positive]
            CHECK ([Weight] > 0),
        CONSTRAINT [CK_Pets_VaccinationStatus]
            CHECK ([VaccinationStatus] IS NULL OR [VaccinationStatus] IN (N'Vaccinated', N'NotVaccinated')),
        CONSTRAINT [CK_Pets_SterilizationStatus]
            CHECK ([SterilizationStatus] IS NULL OR [SterilizationStatus] IN (N'Sterilized', N'Intact')),
        CONSTRAINT [CK_Pets_Temperament]
            CHECK ([Temperament] IS NULL OR [Temperament] IN (
                N'Anxious', N'Friendly', N'Aggressive', N'HyperActive', N'Shy',
                N'Calm', N'Playful', N'Independent', N'Protective'))
    );
    PRINT 'Created table [Parent].[Pets].';
END
ELSE
BEGIN
    PRINT 'Table [Parent].[Pets] already exists.';
END
GO

-- Idempotent: extend a legacy [Parent].[Pets] (previously just PetId +
-- PetParentId + timestamps) with the full pet-profile columns. Added
-- nullable to preserve existing seed rows; the sproc + application enforce
-- non-null on new inserts. CHECK constraints and the microchip UNIQUE
-- filtered index are added separately further down.
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Pets' AND [schema_id] = SCHEMA_ID(N'Parent'))
AND NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'PetType'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
BEGIN
    PRINT 'Extending [Parent].[Pets] with pet-profile columns (nullable for legacy rows).';

    ALTER TABLE [Parent].[Pets]
        ADD [PetType] NVARCHAR(32) NULL,
            [PetName] NVARCHAR(100) NULL,
            [Breed] NVARCHAR(100) NULL,
            [Gender] NVARCHAR(16) NULL,
            [DateOfBirth] DATE NULL,
            [Weight] DECIMAL(5, 2) NULL,
            [MicrochipId] NVARCHAR(32) NULL,
            [Description] NVARCHAR(2000) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Pets_PetType'
      AND [parent_object_id] = OBJECT_ID(N'[Parent].[Pets]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'PetType'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
BEGIN
    -- WITH NOCHECK skips the validation against existing rows (the legacy
    -- ones have NULL, which the CHECK already permits implicitly), but the
    -- constraint is enforced for every future insert/update.
    ALTER TABLE [Parent].[Pets] WITH NOCHECK
        ADD CONSTRAINT [CK_Pets_PetType]
            CHECK ([PetType] IN (N'Dog', N'Cat', N'Hamster', N'GuineaPig'));
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Pets_Gender'
      AND [parent_object_id] = OBJECT_ID(N'[Parent].[Pets]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'Gender'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
BEGIN
    ALTER TABLE [Parent].[Pets] WITH NOCHECK
        ADD CONSTRAINT [CK_Pets_Gender]
            CHECK ([Gender] IN (N'Male', N'Female'));
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Pets_Weight_Positive'
      AND [parent_object_id] = OBJECT_ID(N'[Parent].[Pets]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'Weight'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
BEGIN
    ALTER TABLE [Parent].[Pets] WITH NOCHECK
        ADD CONSTRAINT [CK_Pets_Weight_Positive]
            CHECK ([Weight] > 0);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Pets_PetParentId'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
    CREATE INDEX [IX_Pets_PetParentId]
        ON [Parent].[Pets] ([PetParentId]);
GO

-- Microchip IDs are globally unique (ISO 11784/11785). Filtered unique so
-- pets without a chip don't collide on NULL.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_Pets_MicrochipId'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'MicrochipId'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
    CREATE UNIQUE INDEX [UX_Pets_MicrochipId]
        ON [Parent].[Pets] ([MicrochipId])
        WHERE [MicrochipId] IS NOT NULL;
GO

-- Retrofit (2026-08-05): [IsDeleted] + [DeletedAtUtc] on [Parent].[Pets].
-- DELETE /pets/{petId} used to remove the row and NULL out
-- [Booking].[Bookings].[PetId] on every booking that referenced it, which
-- silently erased the pet from the PROVIDER's record of a job they performed.
-- It is now an anonymise + hide (see [Parent].[DeletePetParentPet]), so the row
-- has to survive and carry a flag instead.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'IsDeleted'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
BEGIN
    ALTER TABLE [Parent].[Pets]
        ADD [IsDeleted] BIT NOT NULL
            CONSTRAINT [DF_Pets_IsDeleted] DEFAULT 0;
    PRINT 'Added column [Parent].[Pets].[IsDeleted].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'DeletedAtUtc'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
BEGIN
    ALTER TABLE [Parent].[Pets] ADD [DeletedAtUtc] DATETIME2(7) NULL;
    PRINT 'Added column [Parent].[Pets].[DeletedAtUtc].';
END
GO


-- Idempotent: extend [Parent].[Pets] with the medical-info columns. All four
-- are nullable in the schema because pets are inserted via AddPetParentPet
-- (which doesn't touch them) and then patched via UpdatePetMedicalInfo.
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Pets' AND [schema_id] = SCHEMA_ID(N'Parent'))
AND NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'VaccinationStatus'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
BEGIN
    PRINT 'Adding medical-info columns to [Parent].[Pets].';

    ALTER TABLE [Parent].[Pets]
        ADD [VaccinationStatus] NVARCHAR(32) NULL,
            [SterilizationStatus] NVARCHAR(32) NULL,
            [MedicalHistory] NVARCHAR(MAX) NULL,
            [Temperament] NVARCHAR(32) NULL;
END
GO

-- Idempotent: add the pet profile-photo column to an existing [Parent].[Pets].
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Pets' AND [schema_id] = SCHEMA_ID(N'Parent'))
AND NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'ProfilePhotoUrl'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
BEGIN
    PRINT 'Adding [ProfilePhotoUrl] to [Parent].[Pets].';
    ALTER TABLE [Parent].[Pets] ADD [ProfilePhotoUrl] NVARCHAR(1000) NULL;
END
GO

-- Idempotent: add the extra medical-info columns (vaccination type/dose +
-- prescription) to an existing [Parent].[Pets].
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Pets' AND [schema_id] = SCHEMA_ID(N'Parent'))
AND NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'VaccinationType'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
BEGIN
    PRINT 'Adding [VaccinationType]/[VaccinationDose]/[Prescription] to [Parent].[Pets].';
    ALTER TABLE [Parent].[Pets]
        ADD [VaccinationType] NVARCHAR(100) NULL,
            [VaccinationDose] NVARCHAR(64) NULL,
            [Prescription] NVARCHAR(MAX) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Pets_VaccinationStatus'
      AND [parent_object_id] = OBJECT_ID(N'[Parent].[Pets]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'VaccinationStatus'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
BEGIN
    ALTER TABLE [Parent].[Pets] WITH NOCHECK
        ADD CONSTRAINT [CK_Pets_VaccinationStatus]
            CHECK ([VaccinationStatus] IS NULL OR [VaccinationStatus] IN (N'Vaccinated', N'NotVaccinated'));
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Pets_SterilizationStatus'
      AND [parent_object_id] = OBJECT_ID(N'[Parent].[Pets]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'SterilizationStatus'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
BEGIN
    ALTER TABLE [Parent].[Pets] WITH NOCHECK
        ADD CONSTRAINT [CK_Pets_SterilizationStatus]
            CHECK ([SterilizationStatus] IS NULL OR [SterilizationStatus] IN (N'Sterilized', N'Intact'));
END
GO

-- The allowed temperaments are the [Pawfront.Domain.Vocabularies.Behaviour] enum,
-- which has grown past the original Anxious/Friendly/Aggressive trio. The CREATE
-- TABLE above is skipped once the table exists, and the add-if-missing block
-- below can only ever CREATE the constraint — never widen one that is already
-- there — so a database carrying an older definition kept rejecting values the
-- API happily validates (e.g. HyperActive → Msg 547 on
-- [Parent].[UpdatePetMedicalInfo], surfacing as a 500). Rebuild it whenever the
-- definition is out of date. Widening a CHECK cannot fail on existing rows.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Pets_Temperament'
      AND [parent_object_id] = OBJECT_ID(N'[Parent].[Pets]')
      AND [definition] NOT LIKE N'%Protective%')
BEGIN
    ALTER TABLE [Parent].[Pets] DROP CONSTRAINT [CK_Pets_Temperament];
    PRINT 'Dropped outdated constraint [CK_Pets_Temperament]; recreating with the full Behaviour set.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Pets_Temperament'
      AND [parent_object_id] = OBJECT_ID(N'[Parent].[Pets]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'Temperament'
      AND [object_id] = OBJECT_ID(N'[Parent].[Pets]'))
BEGIN
    ALTER TABLE [Parent].[Pets] WITH NOCHECK
        ADD CONSTRAINT [CK_Pets_Temperament]
            CHECK ([Temperament] IS NULL OR [Temperament] IN (
                N'Anxious', N'Friendly', N'Aggressive', N'HyperActive', N'Shy',
                N'Calm', N'Playful', N'Independent', N'Protective'));
    PRINT 'Created constraint [CK_Pets_Temperament].';
END
GO


-- 2.9.1a Parent.PetPhotos ----------------------------------------------------
-- One row per uploaded pet photo. ON DELETE CASCADE so removing a pet
-- automatically removes its photo URLs from this table (the blobs
-- themselves are not cleaned up — that's a future job).
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'PetPhotos' AND [schema_id] = SCHEMA_ID(N'Parent'))
BEGIN
    CREATE TABLE [Parent].[PetPhotos]
    (
        [PetPhotoId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_PetPhotos_PetPhotoId] DEFAULT NEWSEQUENTIALID(),
        [PetId] UNIQUEIDENTIFIER NOT NULL,
        [PhotoUrl] NVARCHAR(1000) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_PetPhotos_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_PetPhotos_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_PetPhotos] PRIMARY KEY CLUSTERED ([PetPhotoId] ASC),
        CONSTRAINT [FK_PetPhotos_Pets_PetId]
            FOREIGN KEY ([PetId]) REFERENCES [Parent].[Pets] ([PetId])
            ON DELETE CASCADE
    );
    PRINT 'Created table [Parent].[PetPhotos].';
END
ELSE
BEGIN
    PRINT 'Table [Parent].[PetPhotos] already exists.';
END
GO


-- 2.9.1b Parent.PetNextConsultations -------------------------------------------
-- A pet's next-consultation dates, one row per provider type (Groomer | Vet |
-- Trainer) — a newer date from the same type replaces the old one (upsert).
-- Written by the provider's booking-complete flow; ON DELETE CASCADE with the pet.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'PetNextConsultations' AND [schema_id] = SCHEMA_ID(N'Parent'))
BEGIN
    CREATE TABLE [Parent].[PetNextConsultations]
    (
        [PetNextConsultationId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_PetNextConsultations_PetNextConsultationId] DEFAULT NEWSEQUENTIALID(),
        [PetId] UNIQUEIDENTIFIER NOT NULL,
        [ConsultationType] NVARCHAR(16) NOT NULL,
        [NextConsultationDate] DATE NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_PetNextConsultations_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_PetNextConsultations_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_PetNextConsultations] PRIMARY KEY CLUSTERED ([PetNextConsultationId] ASC),
        CONSTRAINT [FK_PetNextConsultations_Pets_PetId]
            FOREIGN KEY ([PetId]) REFERENCES [Parent].[Pets] ([PetId]) ON DELETE CASCADE,
        CONSTRAINT [CK_PetNextConsultations_ConsultationType]
            CHECK ([ConsultationType] IN (N'Groomer', N'Vet', N'Trainer')),
        CONSTRAINT [UQ_PetNextConsultations_PetId_ConsultationType]
            UNIQUE ([PetId], [ConsultationType])
    );
    PRINT 'Created table [Parent].[PetNextConsultations].';
END
ELSE
BEGIN
    PRINT 'Table [Parent].[PetNextConsultations] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_PetPhotos_PetId'
      AND [object_id] = OBJECT_ID(N'[Parent].[PetPhotos]'))
    CREATE INDEX [IX_PetPhotos_PetId]
        ON [Parent].[PetPhotos] ([PetId])
        INCLUDE ([PhotoUrl], [CreatedAtUtc]);
GO


-- 2.9.1a2 Parent.PetParentPhotos ---------------------------------------------
-- General photo gallery owned directly by a pet parent (not tied to a pet).
-- One row per uploaded photo. ON DELETE CASCADE so removing a parent removes
-- the photo URLs (the blobs themselves are cleaned up best-effort by the
-- delete endpoint / a future sweep job).
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'PetParentPhotos' AND [schema_id] = SCHEMA_ID(N'Parent'))
BEGIN
    CREATE TABLE [Parent].[PetParentPhotos]
    (
        [PetParentPhotoId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_PetParentPhotos_PetParentPhotoId] DEFAULT NEWSEQUENTIALID(),
        [PetParentId] UNIQUEIDENTIFIER NOT NULL,
        [PhotoUrl] NVARCHAR(1000) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_PetParentPhotos_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_PetParentPhotos] PRIMARY KEY CLUSTERED ([PetParentPhotoId] ASC),
        CONSTRAINT [FK_PetParentPhotos_PetParents_PetParentId]
            FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId])
            ON DELETE CASCADE
    );
    PRINT 'Created table [Parent].[PetParentPhotos].';
END
ELSE
BEGIN
    PRINT 'Table [Parent].[PetParentPhotos] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_PetParentPhotos_PetParentId'
      AND [object_id] = OBJECT_ID(N'[Parent].[PetParentPhotos]'))
    CREATE INDEX [IX_PetParentPhotos_PetParentId]
        ON [Parent].[PetParentPhotos] ([PetParentId])
        INCLUDE ([PhotoUrl], [CreatedAtUtc]);
GO


-- 2.9.1b Parent.ParentMobileOtps ---------------------------------------------
-- Mirrors Provider.ProviderMobileOtps. SHA-256 hash of the code (salted with
-- the OTP id) is stored — the raw code is never persisted. 10-minute expiry,
-- failed-attempt counter, terminal Validated/Expired states.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ParentMobileOtps' AND [schema_id] = SCHEMA_ID(N'Parent'))
BEGIN
    CREATE TABLE [Parent].[ParentMobileOtps]
    (
        [ParentMobileOtpId] UNIQUEIDENTIFIER NOT NULL,
        [PetParentId] UNIQUEIDENTIFIER NOT NULL,
        [MobileCountryCode] NVARCHAR(8) NOT NULL,
        [MobileNumber] NVARCHAR(32) NOT NULL,
        [OtpCodeHash] VARBINARY(32) NOT NULL,
        [OtpCodeLastTwo] NVARCHAR(2) NOT NULL,
        [ValidationStatus] NVARCHAR(32) NOT NULL
            CONSTRAINT [DF_ParentMobileOtps_ValidationStatus] DEFAULT N'Pending',
        [FailedAttemptCount] INT NOT NULL
            CONSTRAINT [DF_ParentMobileOtps_FailedAttemptCount] DEFAULT 0,
        [DateSentUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ParentMobileOtps_DateSentUtc] DEFAULT SYSUTCDATETIME(),
        [DateValidatedUtc] DATETIME2(7) NULL,
        [ExpiresAtUtc] DATETIME2(7) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ParentMobileOtps_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ParentMobileOtps_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ParentMobileOtps] PRIMARY KEY CLUSTERED ([ParentMobileOtpId] ASC),
        CONSTRAINT [FK_ParentMobileOtps_PetParents_PetParentId]
            FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId]),
        CONSTRAINT [CK_ParentMobileOtps_ValidationStatus] CHECK ([ValidationStatus] IN (N'Pending', N'Validated', N'Expired'))
    );
    PRINT 'Created table [Parent].[ParentMobileOtps].';
END
ELSE
BEGIN
    PRINT 'Table [Parent].[ParentMobileOtps] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ParentMobileOtps_PetParentId_DateSentUtc'
      AND [object_id] = OBJECT_ID(N'[Parent].[ParentMobileOtps]'))
    CREATE INDEX [IX_ParentMobileOtps_PetParentId_DateSentUtc]
        ON [Parent].[ParentMobileOtps] ([PetParentId], [DateSentUtc] DESC);
GO


-- 2.9.1c Parent.ParentIdentities ---------------------------------------------
-- One identity per parent (UNIQUE on PetParentId). Re-uploading replaces.
-- IdentityType drives a CHECK so callers can't store arbitrary strings.
-- The photo blob URL lives here; the blob itself sits in the shared
-- container under the [PetParentIdentities] folder.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ParentIdentities' AND [schema_id] = SCHEMA_ID(N'Parent'))
BEGIN
    CREATE TABLE [Parent].[ParentIdentities]
    (
        [ParentIdentityId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_ParentIdentities_ParentIdentityId] DEFAULT NEWSEQUENTIALID(),
        [PetParentId] UNIQUEIDENTIFIER NOT NULL,
        [IdentityType] NVARCHAR(32) NOT NULL,
        [IdentityPhotoUrl] NVARCHAR(1000) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ParentIdentities_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ParentIdentities_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ParentIdentities] PRIMARY KEY CLUSTERED ([ParentIdentityId] ASC),
        CONSTRAINT [UQ_ParentIdentities_PetParentId] UNIQUE ([PetParentId]),
        CONSTRAINT [FK_ParentIdentities_PetParents_PetParentId]
            FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId])
            ON DELETE CASCADE,
        CONSTRAINT [CK_ParentIdentities_IdentityType]
            CHECK ([IdentityType] IN (N'Passport', N'DriverLicense', N'NationalId', N'ResidencePermit'))
    );
    PRINT 'Created table [Parent].[ParentIdentities].';
END
ELSE
BEGIN
    PRINT 'Table [Parent].[ParentIdentities] already exists.';
END
GO


-- 2.9.2 Parent.ParentDeviceTokens --------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ParentDeviceTokens' AND [schema_id] = SCHEMA_ID(N'Parent'))
BEGIN
    CREATE TABLE [Parent].[ParentDeviceTokens]
    (
        [ParentDeviceTokenId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_ParentDeviceTokens_ParentDeviceTokenId] DEFAULT NEWSEQUENTIALID(),
        [ParentAuthIdentityId] UNIQUEIDENTIFIER NOT NULL,
        [PetParentId] UNIQUEIDENTIFIER NULL,
        [FcmToken] NVARCHAR(2048) NOT NULL,
        [DeviceId] NVARCHAR(200) NULL,
        [DevicePlatform] NVARCHAR(32) NULL,
        [IsActive] BIT NOT NULL
            CONSTRAINT [DF_ParentDeviceTokens_IsActive] DEFAULT 1,
        [LastSeenAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ParentDeviceTokens_LastSeenAtUtc] DEFAULT SYSUTCDATETIME(),
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ParentDeviceTokens_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ParentDeviceTokens_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_ParentDeviceTokens] PRIMARY KEY CLUSTERED ([ParentDeviceTokenId] ASC),
        CONSTRAINT [UQ_ParentDeviceTokens_FcmToken] UNIQUE ([FcmToken]),
        CONSTRAINT [FK_ParentDeviceTokens_ParentAuthIdentities_ParentAuthIdentityId]
            FOREIGN KEY ([ParentAuthIdentityId]) REFERENCES [Parent].[ParentAuthIdentities] ([ParentAuthIdentityId]),
        CONSTRAINT [FK_ParentDeviceTokens_PetParents_PetParentId]
            FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId]),
        CONSTRAINT [CK_ParentDeviceTokens_DevicePlatform]
            CHECK ([DevicePlatform] IS NULL OR [DevicePlatform] IN (N'Android', N'iOS'))
    );
    PRINT 'Created table [Parent].[ParentDeviceTokens].';
END
ELSE
BEGIN
    PRINT 'Table [Parent].[ParentDeviceTokens] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ParentDeviceTokens_PetParentId_IsActive'
      AND [object_id] = OBJECT_ID(N'[Parent].[ParentDeviceTokens]'))
    CREATE INDEX [IX_ParentDeviceTokens_PetParentId_IsActive]
        ON [Parent].[ParentDeviceTokens] ([PetParentId], [IsActive]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ParentDeviceTokens_ParentAuthIdentityId_IsActive'
      AND [object_id] = OBJECT_ID(N'[Parent].[ParentDeviceTokens]'))
    CREATE INDEX [IX_ParentDeviceTokens_ParentAuthIdentityId_IsActive]
        ON [Parent].[ParentDeviceTokens] ([ParentAuthIdentityId], [IsActive]);
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
        -- Either ProviderId or PetParentId is set; the CHECK below enforces
        -- exactly one. Provider- and parent-organised events live in the
        -- same table so booking, counter, and discovery flows are organiser-
        -- agnostic.
        [ProviderId] UNIQUEIDENTIFIER NULL,
        [PetParentId] UNIQUEIDENTIFIER NULL,
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
        -- Ticketing on the main row (not the Cosmos physical extension) so it's
        -- returned for every event type, including online events.
        [IsPaid] BIT NOT NULL
            CONSTRAINT [DF_Events_IsPaid] DEFAULT 0,
        [Price] DECIMAL(18, 2) NULL,
        -- Optional refund policy (NULL when unset) — doesn't apply to free
        -- events. When set it's one of the CK_Events_CancellationPolicy values.
        [CancellationPolicy] NVARCHAR(32) NULL,
        -- Joining link for ONLINE events; NULL for physical events.
        [EventLink] NVARCHAR(1000) NULL,
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
        CONSTRAINT [FK_Events_PetParents_PetParentId]
            FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId]),
        CONSTRAINT [CK_Events_OrganiserExactlyOne] CHECK (
            ([ProviderId] IS NOT NULL AND [PetParentId] IS NULL)
         OR ([ProviderId] IS NULL AND [PetParentId] IS NOT NULL)
        ),
        CONSTRAINT [CK_Events_EventCategory] CHECK ([EventCategory] IN (
            N'AdoptionAndRescue', N'PetTraining', N'Charity', N'Volunteering',
            N'HealthAndWellness', N'SocialAndCultural', N'OutdoorActivities', N'ParentEducation')),
        CONSTRAINT [CK_Events_EventType] CHECK ([EventType] IN (N'Physical', N'Online')),
        CONSTRAINT [CK_Events_DateRange] CHECK ([StartDate] <= [EndDate]),
        CONSTRAINT [CK_Events_Ticketing] CHECK (
            ([IsPaid] = 0 AND [Price] IS NULL)
         OR ([IsPaid] = 1 AND [Price] IS NOT NULL AND [Price] >= 0)
        ),
        CONSTRAINT [CK_Events_CancellationPolicy] CHECK ([CancellationPolicy] IN (
            N'FullRefundUpTo4Hours', N'FullRefundUpTo2Hours', N'NoRefund'))
    );
    PRINT 'Created table [Event].[Events].';
END
ELSE
BEGIN
    PRINT 'Table [Event].[Events] already exists.';
END
GO

-- 2.10b Retrofit: support parent-organised events alongside provider ones.
-- ProviderId becomes nullable, PetParentId column + FK is added, the
-- exactly-one CHECK is enforced for future inserts. Done as a sequence of
-- idempotent steps so re-runs are safe.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]')
      AND [name] = N'ProviderId'
      AND [is_nullable] = 0)
BEGIN
    ALTER TABLE [Event].[Events]
        ALTER COLUMN [ProviderId] UNIQUEIDENTIFIER NULL;
    PRINT 'Made [Event].[Events].[ProviderId] nullable.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]')
      AND [name] = N'PetParentId')
BEGIN
    ALTER TABLE [Event].[Events]
        ADD [PetParentId] UNIQUEIDENTIFIER NULL;
    PRINT 'Added column [Event].[Events].[PetParentId].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE [name] = N'FK_Events_PetParents_PetParentId'
      AND [parent_object_id] = OBJECT_ID(N'[Event].[Events]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]')
      AND [name] = N'PetParentId')
BEGIN
    ALTER TABLE [Event].[Events]
        ADD CONSTRAINT [FK_Events_PetParents_PetParentId]
            FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId]);
    PRINT 'Added FK [FK_Events_PetParents_PetParentId].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Events_OrganiserExactlyOne'
      AND [parent_object_id] = OBJECT_ID(N'[Event].[Events]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]')
      AND [name] = N'PetParentId')
BEGIN
    -- WITH NOCHECK skips validation of existing rows (all of which have
    -- ProviderId set and PetParentId NULL, so they satisfy the rule
    -- already). Future inserts/updates are checked normally.
    ALTER TABLE [Event].[Events] WITH NOCHECK
        ADD CONSTRAINT [CK_Events_OrganiserExactlyOne] CHECK (
            ([ProviderId] IS NOT NULL AND [PetParentId] IS NULL)
         OR ([ProviderId] IS NULL AND [PetParentId] IS NOT NULL)
        );
    PRINT 'Added CHECK [CK_Events_OrganiserExactlyOne].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Events_PetParentId_StartDate'
      AND [object_id] = OBJECT_ID(N'[Event].[Events]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]')
      AND [name] = N'PetParentId')
    CREATE INDEX [IX_Events_PetParentId_StartDate]
        ON [Event].[Events] ([PetParentId], [StartDate] DESC);
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

-- 2.10c Retrofit: lift ticketing (IsPaid / Price) onto the main event row.
-- Previously isPaid/price lived only in the Cosmos physical extension, so
-- online events (which have no Cosmos doc) never carried them. Now they're
-- SQL columns returned for every event type.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]') AND [name] = N'IsPaid')
BEGIN
    ALTER TABLE [Event].[Events]
        ADD [IsPaid] BIT NOT NULL
            CONSTRAINT [DF_Events_IsPaid] DEFAULT 0;
    PRINT 'Added column [Event].[Events].[IsPaid].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]') AND [name] = N'Price')
BEGIN
    ALTER TABLE [Event].[Events]
        ADD [Price] DECIMAL(18, 2) NULL;
    PRINT 'Added column [Event].[Events].[Price].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Events_Ticketing'
      AND [parent_object_id] = OBJECT_ID(N'[Event].[Events]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]') AND [name] = N'Price')
BEGIN
    -- WITH NOCHECK skips validation of pre-existing rows (which default to
    -- IsPaid = 0 / Price NULL and already satisfy the rule). Future writes
    -- are checked normally.
    ALTER TABLE [Event].[Events] WITH NOCHECK
        ADD CONSTRAINT [CK_Events_Ticketing] CHECK (
            ([IsPaid] = 0 AND [Price] IS NULL)
         OR ([IsPaid] = 1 AND [Price] IS NOT NULL AND [Price] >= 0)
        );
    PRINT 'Added CHECK [CK_Events_Ticketing].';
END
GO

-- 2.10d Retrofit: add the event cancellation/refund policy column (optional —
-- NULL when unset, since it doesn't apply to free events).
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]') AND [name] = N'CancellationPolicy')
BEGIN
    ALTER TABLE [Event].[Events] ADD [CancellationPolicy] NVARCHAR(32) NULL;
    PRINT 'Added column [Event].[Events].[CancellationPolicy].';
END
GO

-- 2.10d.1 Retrofit: relax the cancellation/refund policy to optional on
-- databases where it was previously created NOT NULL with a default of
-- 'NoRefund'. Drop the default constraint, then make the column nullable.
IF EXISTS (
    SELECT 1 FROM sys.default_constraints
    WHERE [name] = N'DF_Events_CancellationPolicy'
      AND [parent_object_id] = OBJECT_ID(N'[Event].[Events]'))
BEGIN
    ALTER TABLE [Event].[Events] DROP CONSTRAINT [DF_Events_CancellationPolicy];
    PRINT 'Dropped default [DF_Events_CancellationPolicy].';
END
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]')
      AND [name] = N'CancellationPolicy'
      AND [is_nullable] = 0)
BEGIN
    ALTER TABLE [Event].[Events] ALTER COLUMN [CancellationPolicy] NVARCHAR(32) NULL;
    PRINT 'Made [Event].[Events].[CancellationPolicy] nullable.';
END
GO

-- 2.10e Retrofit: add the online-event joining link column.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]') AND [name] = N'EventLink')
BEGIN
    ALTER TABLE [Event].[Events] ADD [EventLink] NVARCHAR(1000) NULL;
    PRINT 'Added column [Event].[Events].[EventLink].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Events_CancellationPolicy'
      AND [parent_object_id] = OBJECT_ID(N'[Event].[Events]'))
AND EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Event].[Events]') AND [name] = N'CancellationPolicy')
BEGIN
    -- WITH NOCHECK skips validation of pre-existing rows (which default to
    -- NoRefund and already satisfy the rule). Future writes are checked.
    ALTER TABLE [Event].[Events] WITH NOCHECK
        ADD CONSTRAINT [CK_Events_CancellationPolicy] CHECK ([CancellationPolicy] IN (
            N'FullRefundUpTo4Hours', N'FullRefundUpTo2Hours', N'NoRefund'));
    PRINT 'Added CHECK [CK_Events_CancellationPolicy].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Events_ProviderId_StartDate'
      AND [object_id] = OBJECT_ID(N'[Event].[Events]'))
    CREATE INDEX [IX_Events_ProviderId_StartDate]
        ON [Event].[Events] ([ProviderId], [StartDate] DESC);
GO

-- Drop the pre-parent-events variant of this index (its INCLUDE list lacks
-- [PetParentId]) so the CREATE below rebuilds it with the current definition.
IF EXISTS (
    SELECT 1 FROM sys.indexes i
    WHERE i.[name] = N'IX_Events_Category_StartDate'
      AND i.[object_id] = OBJECT_ID(N'[Event].[Events]')
      AND NOT EXISTS (
          SELECT 1
          FROM sys.index_columns ic
          JOIN sys.columns c
            ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
          WHERE ic.[object_id] = i.[object_id]
            AND ic.[index_id] = i.[index_id]
            AND ic.[is_included_column] = 1
            AND c.[name] = N'PetParentId'))
BEGIN
    PRINT 'Rebuilding [IX_Events_Category_StartDate] with [PetParentId] in the INCLUDE list.';
    DROP INDEX [IX_Events_Category_StartDate] ON [Event].[Events];
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Events_Category_StartDate'
      AND [object_id] = OBJECT_ID(N'[Event].[Events]'))
    CREATE INDEX [IX_Events_Category_StartDate]
        ON [Event].[Events] ([EventCategory], [StartDate] DESC)
        INCLUDE ([ProviderId], [PetParentId], [Title], [EventType]);
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
        -- Which of the parent's pets the booking is for. Populated for
        -- parent-app bookings; NULL for legacy rows and Custom walk-ins.
        [PetId] UNIQUEIDENTIFIER NULL,
        [BookingDate] DATE NOT NULL,
        [StartTime] TIME(0) NOT NULL,
        [EndTime] TIME(0) NOT NULL,
        -- When the job actually finished, if it finished EARLY; NULL whenever
        -- the booked window ran its course. Exists ONLY to release the unused
        -- remainder back to capacity, so occupancy is
        -- [StartTime, COALESCE([ActualEndTime], [EndTime])). Deliberately NOT
        -- part of what the booking cost: pricing and every wire contract keep
        -- reading [EndTime].
        [ActualEndTime] TIME(0) NULL,
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
        -- Where the service is delivered, as chosen by the parent at booking
        -- time: 'ParentLocation' or 'ProviderLocation'. NULL for Custom
        -- walk-ins and legacy rows.
        [LocationType] NVARCHAR(32) NULL,
        -- Snapshots captured at booking creation (price-lock siblings): the
        -- provider's advertised cancellation policy (24/48/72/96 hours, NULL = no
        -- restriction) and the SELECTED service-location address (parent's for
        -- ParentLocation, provider's business address for ProviderLocation). Frozen
        -- so later provider edits never re-rule / re-address an existing booking.
        [CancellationPolicyHours] INT NULL,
        [SnapshotAddressLine] NVARCHAR(500) NULL,
        [SnapshotCity] NVARCHAR(200) NULL,
        [SnapshotZipCode] NVARCHAR(32) NULL,
        [SnapshotLatitude] DECIMAL(9, 6) NULL,
        [SnapshotLongitude] DECIMAL(9, 6) NULL,
        -- Lifecycle: CREATED -> CONFIRMED -> COMPLETED, with APPROVAL_NEEDED for
        -- schedule changes and PROVIDER_CANCELLED / PARENT_CANCELLED as the two
        -- terminal cancellation states. Every status except the cancelled two
        -- still holds the booking's capacity slot.
        [Status] NVARCHAR(48) NOT NULL
            CONSTRAINT [DF_Bookings_Status] DEFAULT N'CREATED',
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Bookings_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Bookings_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [CancelledAtUtc] DATETIME2(7) NULL,

        CONSTRAINT [PK_Bookings] PRIMARY KEY CLUSTERED ([BookingId] ASC),
        CONSTRAINT [FK_Bookings_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
        CONSTRAINT [FK_Bookings_PetParents_PetParentId]
            FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId]),
        CONSTRAINT [FK_Bookings_ProviderServices_ServiceId]
            FOREIGN KEY ([ServiceId]) REFERENCES [Provider].[ProviderServices] ([ServiceId]),
        CONSTRAINT [FK_Bookings_Pets_PetId]
            FOREIGN KEY ([PetId]) REFERENCES [Parent].[Pets] ([PetId]),
        CONSTRAINT [CK_Bookings_TimeOrder] CHECK ([StartTime] < [EndTime]),
        -- An early finish can only SHRINK the occupied window, never extend or
        -- invert it. Equality with [StartTime] releases the whole slot (the job
        -- ended before its booked start, which is possible because starting is
        -- gated on the service DATE, not the time of day).
        CONSTRAINT [CK_Bookings_ActualEndTime] CHECK (
            [ActualEndTime] IS NULL
            OR ([ActualEndTime] >= [StartTime] AND [ActualEndTime] <= [EndTime])
        ),
        CONSTRAINT [CK_Bookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED', N'JOB_STARTED',
                                N'COMPLETED', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')),
        CONSTRAINT [CK_Bookings_CancelledRequiresTimestamp] CHECK (
            ([Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED') AND [CancelledAtUtc] IS NOT NULL)
            OR ([Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED'))
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
        CONSTRAINT [CK_Bookings_CancellationPolicyHours]
            CHECK ([CancellationPolicyHours] IS NULL
                   OR [CancellationPolicyHours] IN (24, 48, 72, 96)),
        CONSTRAINT [CK_Bookings_SourceShape] CHECK
        (
            -- [PricePerHour] is now snapshotted on App rows too (price-lock), so it
            -- is NOT asserted NULL here; only the Custom-identity columns discriminate.
            ([Source] = N'App'
                AND [PetParentId] IS NOT NULL
                AND [CustomerName] IS NULL
                AND [CustomerMobileCountryCode] IS NULL
                AND [CustomerMobile] IS NULL
                AND [AnimalType] IS NULL
                AND [PetName] IS NULL
                AND [ServiceLocation] IS NULL
                AND [CustomerLocation] IS NULL)
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

-- 2.9e Retrofit: [ActualEndTime] — the real finish time of a job that ended
-- EARLY, so the unused remainder of its booked window is released back to the
-- provider's capacity instead of staying blocked. Every capacity / slot /
-- agenda query reads COALESCE([ActualEndTime], [EndTime]); NULL (the value on
-- every existing row) means "occupied the whole window", so this retrofit is
-- behaviour-neutral until a job is completed early. Fires once.
IF COL_LENGTH(N'[Booking].[Bookings]', N'ActualEndTime') IS NULL
BEGIN
    PRINT 'Adding [ActualEndTime] to [Booking].[Bookings].';
    ALTER TABLE [Booking].[Bookings] ADD [ActualEndTime] TIME(0) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Bookings_ActualEndTime')
BEGIN
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_ActualEndTime] CHECK (
            [ActualEndTime] IS NULL
            OR ([ActualEndTime] >= [StartTime] AND [ActualEndTime] <= [EndTime])
        );
END
GO

-- The capacity count in [Booking].[CreateBooking] — the hottest query on this
-- table, and one that runs under UPDLOCK + HOLDLOCK — now reads [ActualEndTime],
-- so it must be covered or every candidate row costs a clustered-index lookup
-- while those locks are held. An index created before the column existed is
-- dropped here and recreated below.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Bookings_Service_Date_Status'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes AS i
    INNER JOIN sys.index_columns AS ic
        ON i.[object_id] = ic.[object_id] AND i.[index_id] = ic.[index_id]
    INNER JOIN sys.columns AS c
        ON ic.[object_id] = c.[object_id] AND ic.[column_id] = c.[column_id]
    WHERE i.[object_id] = OBJECT_ID(N'[Booking].[Bookings]')
      AND i.[name] = N'IX_Bookings_Service_Date_Status'
      AND c.[name] = N'ActualEndTime')
BEGIN
    PRINT 'Rebuilding [IX_Bookings_Service_Date_Status] to cover [ActualEndTime].';
    DROP INDEX [IX_Bookings_Service_Date_Status] ON [Booking].[Bookings];
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Bookings_Service_Date_Status'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
    CREATE INDEX [IX_Bookings_Service_Date_Status]
        ON [Booking].[Bookings] ([ServiceId], [BookingDate], [Status])
        INCLUDE ([StartTime], [EndTime], [ActualEndTime], [BookingId], [PetParentId], [ProviderId]);
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

-- Migration: add the [PetId] column (which pet the booking is for) to an
-- existing [Booking].[Bookings] table. Idempotent; legacy rows stay NULL.
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Bookings' AND [schema_id] = SCHEMA_ID(N'Booking'))
AND NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'PetId'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
BEGIN
    PRINT 'Adding [PetId] to [Booking].[Bookings].';
    ALTER TABLE [Booking].[Bookings]
        ADD [PetId] UNIQUEIDENTIFIER NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE [name] = N'FK_Bookings_Pets_PetId')
AND EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Bookings' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    ALTER TABLE [Booking].[Bookings] WITH NOCHECK
        ADD CONSTRAINT [FK_Bookings_Pets_PetId]
            FOREIGN KEY ([PetId]) REFERENCES [Parent].[Pets] ([PetId]);
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

-- CK_Bookings_SourceShape: relaxed to allow App [PricePerHour] (the offering's unit
-- rate is now snapshotted on App rows too — price-lock). For an existing DB that
-- created the constraint with the old "App ... PricePerHour IS NULL" shape, drop it
-- first; then (re)add the relaxed shape. Idempotent: the drop only fires while the
-- old text is still present.
IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE [name] = N'CK_Bookings_SourceShape'
             AND OBJECT_DEFINITION([object_id]) LIKE '%PricePerHour] IS NULL%')
BEGIN
    PRINT 'Relaxing [CK_Bookings_SourceShape] to allow App [PricePerHour].';
    ALTER TABLE [Booking].[Bookings] DROP CONSTRAINT [CK_Bookings_SourceShape];
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
                AND [CustomerLocation] IS NULL)
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

-- Migration: add the sequential [JobNumber] IDENTITY column to an existing
-- [Booking].[Bookings] table (idempotent). Adding an IDENTITY column backfills
-- existing rows with sequential values automatically. Surfaced as the "PF-000123"
-- friendly Job ID on the booking-detail read.
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Bookings' AND [schema_id] = SCHEMA_ID(N'Booking'))
AND NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'JobNumber'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
BEGIN
    PRINT 'Adding [JobNumber] IDENTITY to [Booking].[Bookings].';
    ALTER TABLE [Booking].[Bookings]
        ADD [JobNumber] INT NOT NULL IDENTITY(1, 1);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_Bookings_JobNumber'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
    CREATE UNIQUE INDEX [UX_Bookings_JobNumber]
        ON [Booking].[Bookings] ([JobNumber]);
GO

-- Migration: add the capture-only payout columns to an existing table
-- (idempotent). [PayoutStatus] defaults to 'Pending'; [PayoutId] stays NULL
-- until an external payout is issued (execution leg not built yet).
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Bookings' AND [schema_id] = SCHEMA_ID(N'Booking'))
AND NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'PayoutStatus'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
BEGIN
    PRINT 'Adding [PayoutStatus] / [PayoutId] to [Booking].[Bookings].';
    ALTER TABLE [Booking].[Bookings]
        ADD [PayoutStatus] NVARCHAR(32) NOT NULL
                CONSTRAINT [DF_Bookings_PayoutStatus] DEFAULT N'Pending',
            [PayoutId] NVARCHAR(64) NULL;
END
GO

-- 'NO_PAYOUT' joined the vocabulary (terminal: the job ended as a no-show or
-- expired, so no money can ever move on it). A database created against the
-- original four-value CHECK would keep rejecting it, and the IF-NOT-EXISTS
-- guard below is by NAME so it could never repair one. Drop an out-of-date
-- definition first — widening a CHECK cannot fail on existing rows.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Bookings_PayoutStatus'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[Bookings]')
      AND [definition] NOT LIKE N'%NO[_]PAYOUT%')
BEGIN
    ALTER TABLE [Booking].[Bookings] DROP CONSTRAINT [CK_Bookings_PayoutStatus];
    PRINT 'Dropped outdated constraint [CK_Bookings_PayoutStatus]; recreating with NO_PAYOUT.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Bookings_PayoutStatus')
BEGIN
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_PayoutStatus]
            CHECK ([PayoutStatus] IN (N'Pending', N'Processing', N'Paid', N'Failed', N'NO_PAYOUT'));
    PRINT 'Created constraint [CK_Bookings_PayoutStatus].';
END
GO

-- Migration: add the [LocationType] column (where the service is delivered —
-- 'ParentLocation' or 'ProviderLocation', chosen by the parent at booking time)
-- to an existing [Booking].[Bookings] table. Idempotent; legacy rows stay NULL.
IF EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Bookings' AND [schema_id] = SCHEMA_ID(N'Booking'))
AND NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [name] = N'LocationType'
      AND [object_id] = OBJECT_ID(N'[Booking].[Bookings]'))
BEGIN
    PRINT 'Adding [LocationType] to [Booking].[Bookings].';
    ALTER TABLE [Booking].[Bookings]
        ADD [LocationType] NVARCHAR(32) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Bookings_LocationType')
BEGIN
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_LocationType]
            CHECK ([LocationType] IS NULL
                OR [LocationType] IN (N'ParentLocation', N'ProviderLocation'));
END
GO

-- Migration: move an existing [Booking].[Bookings] table from the legacy status
-- set (Confirmed/Cancelled/Completed/NoShow) to the 6-status lifecycle. Detected
-- by the old CHECK constraint still mentioning 'NoShow' (which exists only in the
-- legacy set), so this fires once and never on fresh/already-migrated installs.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Bookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[Bookings]')
      AND [definition] LIKE N'%NoShow%')
BEGIN
    PRINT 'Migrating [Booking].[Bookings] to the 6-status lifecycle.';

    ALTER TABLE [Booking].[Bookings] DROP CONSTRAINT [CK_Bookings_Status];
    ALTER TABLE [Booking].[Bookings] DROP CONSTRAINT [CK_Bookings_CancelledRequiresTimestamp];

    UPDATE [Booking].[Bookings] SET [Status] = N'CONFIRMED' WHERE [Status] = N'Confirmed';
    UPDATE [Booking].[Bookings] SET [Status] = N'COMPLETED' WHERE [Status] = N'Completed';
    -- Legacy Cancelled rows came from the parent cancel flow; map both Cancelled
    -- and NoShow onto PARENT_CANCELLED (closest available terminal state).
    UPDATE [Booking].[Bookings] SET [Status] = N'PARENT_CANCELLED' WHERE [Status] IN (N'Cancelled', N'NoShow');

    -- The new CancelledRequiresTimestamp constraint demands a timestamp for both
    -- cancelled statuses; legacy NoShow rows were never required to have one.
    UPDATE [Booking].[Bookings]
    SET [CancelledAtUtc] = SYSUTCDATETIME()
    WHERE [Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED') AND [CancelledAtUtc] IS NULL;

    ALTER TABLE [Booking].[Bookings] DROP CONSTRAINT [DF_Bookings_Status];
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [DF_Bookings_Status] DEFAULT N'CREATED' FOR [Status];

    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'COMPLETED', N'APPROVAL_NEEDED',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED'));
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_CancelledRequiresTimestamp] CHECK (
            ([Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED') AND [CancelledAtUtc] IS NOT NULL)
            OR ([Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED'))
        );
END
GO


-- 2.10a Booking.BookingStatusHistory -----------------------------------------
-- Append-only audit trail of every booking status change (one row per
-- transition, plus a seeded creation row with FromStatus = NULL).
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'BookingStatusHistory' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[BookingStatusHistory]
    (
        [BookingStatusHistoryId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_BookingStatusHistory_Id] DEFAULT NEWSEQUENTIALID(),
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [FromStatus] NVARCHAR(48) NULL,
        [ToStatus] NVARCHAR(48) NOT NULL,
        [ChangedByActor] NVARCHAR(16) NOT NULL,
        [ChangedByActorId] UNIQUEIDENTIFIER NULL,
        [Note] NVARCHAR(500) NULL,
        [ChangedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_BookingStatusHistory_ChangedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_BookingStatusHistory] PRIMARY KEY CLUSTERED ([BookingStatusHistoryId] ASC),
        CONSTRAINT [FK_BookingStatusHistory_Bookings_BookingId]
            FOREIGN KEY ([BookingId]) REFERENCES [Booking].[Bookings] ([BookingId])
            ON DELETE CASCADE,
        CONSTRAINT [CK_BookingStatusHistory_Actor]
            CHECK ([ChangedByActor] IN (N'Provider', N'Parent', N'System'))
    );
    PRINT 'Created table [Booking].[BookingStatusHistory].';
END
ELSE
BEGIN
    PRINT 'Table [Booking].[BookingStatusHistory] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_BookingStatusHistory_Booking_ChangedAt'
      AND [object_id] = OBJECT_ID(N'[Booking].[BookingStatusHistory]'))
    CREATE INDEX [IX_BookingStatusHistory_Booking_ChangedAt]
        ON [Booking].[BookingStatusHistory] ([BookingId], [ChangedAtUtc] ASC)
        INCLUDE ([FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note]);
GO

-- Backfill a creation audit entry for any pre-existing booking that has none, so
-- the status-history endpoint returns at least the current status for legacy
-- rows. Idempotent — only inserts where no history exists yet.
INSERT INTO [Booking].[BookingStatusHistory]
    ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
SELECT b.[BookingId], NULL, b.[Status], N'System', NULL, N'Backfilled at migration'
FROM [Booking].[Bookings] AS b
WHERE NOT EXISTS (
    SELECT 1 FROM [Booking].[BookingStatusHistory] AS h WHERE h.[BookingId] = b.[BookingId]);
GO


-- 2.10b Booking.NightStayBookings ---------------------------------------------
-- Multi-night boarding bookings (PetSitter NightStay service only). Distinct
-- from [Booking].[Bookings], which is single-day (one BookingDate + time
-- window). A night stay spans [CheckInDate, CheckOutDate): the checkout day is
-- NOT a stayed night, so capacity must be free on every night in that range.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'NightStayBookings' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[NightStayBookings]
    (
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_NightStayBookings_Id] DEFAULT NEWSEQUENTIALID(),
        -- Short, human-friendly sequential job number (separate sequence from the
        -- single-day [Booking].[Bookings].[JobNumber]). Surfaced as "PF-000123".
        [JobNumber] INT NOT NULL IDENTITY(1, 1),
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [PetParentId] UNIQUEIDENTIFIER NOT NULL,
        [ServiceId] UNIQUEIDENTIFIER NOT NULL,
        [ServiceCategory] NVARCHAR(64) NOT NULL,
        [SubCategory] NVARCHAR(64) NOT NULL,
        [PetId] UNIQUEIDENTIFIER NULL,
        [CheckInDate] DATE NOT NULL,
        -- Checkout day; NOT a stayed night. Stayed nights = [CheckInDate, CheckOutDate).
        [CheckOutDate] DATE NOT NULL,
        -- The day the pet ACTUALLY went home, if the stay ended early; NULL when
        -- it ran to its booked checkout. Same [CheckInDate, X) meaning as
        -- [CheckOutDate] (a pickup day, not a stayed night). Exists ONLY to
        -- release the unused nights back to per-night capacity, so occupancy is
        -- [CheckInDate, COALESCE([ActualCheckOutDate], [CheckOutDate])).
        -- Deliberately NOT a re-statement of what the stay cost.
        [ActualCheckOutDate] DATE NULL,
        -- Snapshot of the offering's drop-off / pick-up times at booking time.
        [DropOffTime] TIME(0) NOT NULL,
        [PickUpTime] TIME(0) NOT NULL,
        -- Snapshot of the offering's per-night rate at booking time (price-lock).
        [PricePerNight] DECIMAL(10, 2) NULL,
        -- Optional free-text notes the parent attaches to the stay at booking
        -- time. Surfaced on the night-stay booking-detail read.
        [JobNotes] NVARCHAR(2000) NULL,
        -- Where the service is delivered: 'ParentLocation' or 'ProviderLocation'.
        [LocationType] NVARCHAR(32) NULL,
        -- Snapshots captured at booking creation — mirror of [Booking].[Bookings]:
        -- the provider's advertised cancellation policy + the SELECTED
        -- service-location address, frozen so later edits never move an existing stay.
        [CancellationPolicyHours] INT NULL,
        [SnapshotAddressLine] NVARCHAR(500) NULL,
        [SnapshotCity] NVARCHAR(200) NULL,
        [SnapshotZipCode] NVARCHAR(32) NULL,
        [SnapshotLatitude] DECIMAL(9, 6) NULL,
        [SnapshotLongitude] DECIMAL(9, 6) NULL,
        -- Payout (capture-only for now — mirrors [Booking].[Bookings]).
        -- 'NO_PAYOUT' is TERMINAL: the job ended as a no-show or expired, so no
        -- money can ever move on it.
        [PayoutStatus] NVARCHAR(32) NOT NULL
            CONSTRAINT [DF_NightStayBookings_PayoutStatus] DEFAULT N'Pending',
        [PayoutId] NVARCHAR(64) NULL,
        [Status] NVARCHAR(48) NOT NULL
            CONSTRAINT [DF_NightStayBookings_Status] DEFAULT N'CREATED',
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_NightStayBookings_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_NightStayBookings_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [CancelledAtUtc] DATETIME2(7) NULL,

        CONSTRAINT [PK_NightStayBookings] PRIMARY KEY CLUSTERED ([NightStayBookingId] ASC),
        CONSTRAINT [FK_NightStayBookings_Providers_ProviderId]
            FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
        CONSTRAINT [FK_NightStayBookings_PetParents_PetParentId]
            FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId]),
        CONSTRAINT [FK_NightStayBookings_ProviderServices_ServiceId]
            FOREIGN KEY ([ServiceId]) REFERENCES [Provider].[ProviderServices] ([ServiceId]),
        CONSTRAINT [FK_NightStayBookings_Pets_PetId]
            FOREIGN KEY ([PetId]) REFERENCES [Parent].[Pets] ([PetId]),
        CONSTRAINT [CK_NightStayBookings_DateOrder] CHECK ([CheckOutDate] > [CheckInDate]),
        -- An early pickup can only SHRINK the stay. The floor is one night: the
        -- pet was physically there on the check-in day, so that night is never
        -- given back even if the stay ended the same day.
        CONSTRAINT [CK_NightStayBookings_ActualCheckOutDate] CHECK (
            [ActualCheckOutDate] IS NULL
            OR ([ActualCheckOutDate] > [CheckInDate] AND [ActualCheckOutDate] <= [CheckOutDate])
        ),
        CONSTRAINT [CK_NightStayBookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED', N'JOB_STARTED',
                                N'COMPLETED', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')),
        CONSTRAINT [CK_NightStayBookings_CancelledRequiresTimestamp] CHECK (
            ([Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED') AND [CancelledAtUtc] IS NOT NULL)
            OR ([Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED'))
        ),
        CONSTRAINT [CK_NightStayBookings_PayoutStatus]
            CHECK ([PayoutStatus] IN (N'Pending', N'Processing', N'Paid', N'Failed', N'NO_PAYOUT')),
        CONSTRAINT [CK_NightStayBookings_CancellationPolicyHours]
            CHECK ([CancellationPolicyHours] IS NULL
                   OR [CancellationPolicyHours] IN (24, 48, 72, 96)),
        CONSTRAINT [CK_NightStayBookings_LocationType]
            CHECK ([LocationType] IS NULL
                OR [LocationType] IN (N'ParentLocation', N'ProviderLocation'))
    );
    PRINT 'Created table [Booking].[NightStayBookings].';
END
ELSE
BEGIN
    PRINT 'Table [Booking].[NightStayBookings] already exists.';
END
GO

-- 2.10a0 Retrofit: [ActualCheckOutDate] — the day the pet actually went home
-- when a stay ended EARLY, so the remaining nights are released back to
-- per-night capacity instead of staying blocked. Every per-night capacity /
-- availability query reads COALESCE([ActualCheckOutDate], [CheckOutDate]); NULL
-- (the value on every existing row) means "stayed every booked night", so this
-- retrofit is behaviour-neutral until a stay is completed early. Fires once.
IF COL_LENGTH(N'[Booking].[NightStayBookings]', N'ActualCheckOutDate') IS NULL
BEGIN
    PRINT 'Adding [ActualCheckOutDate] to [Booking].[NightStayBookings].';
    ALTER TABLE [Booking].[NightStayBookings] ADD [ActualCheckOutDate] DATE NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_NightStayBookings_ActualCheckOutDate')
BEGIN
    ALTER TABLE [Booking].[NightStayBookings]
        ADD CONSTRAINT [CK_NightStayBookings_ActualCheckOutDate] CHECK (
            [ActualCheckOutDate] IS NULL
            OR ([ActualCheckOutDate] > [CheckInDate] AND [ActualCheckOutDate] <= [CheckOutDate])
        );
END
GO

-- The per-night capacity walk in [Booking].[CreateNightStayBooking] runs under
-- UPDLOCK + HOLDLOCK and now reads [ActualCheckOutDate] on its join, so the
-- index must cover it. One created before the column existed is dropped here
-- and recreated below.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_NightStayBookings_Service_Dates_Status'
      AND [object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]'))
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes AS i
    INNER JOIN sys.index_columns AS ic
        ON i.[object_id] = ic.[object_id] AND i.[index_id] = ic.[index_id]
    INNER JOIN sys.columns AS c
        ON ic.[object_id] = c.[object_id] AND ic.[column_id] = c.[column_id]
    WHERE i.[object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]')
      AND i.[name] = N'IX_NightStayBookings_Service_Dates_Status'
      AND c.[name] = N'ActualCheckOutDate')
BEGIN
    PRINT 'Rebuilding [IX_NightStayBookings_Service_Dates_Status] to cover [ActualCheckOutDate].';
    DROP INDEX [IX_NightStayBookings_Service_Dates_Status] ON [Booking].[NightStayBookings];
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_NightStayBookings_Service_Dates_Status'
      AND [object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]'))
    CREATE INDEX [IX_NightStayBookings_Service_Dates_Status]
        ON [Booking].[NightStayBookings] ([ServiceId], [CheckInDate], [CheckOutDate], [Status])
        INCLUDE ([ActualCheckOutDate], [NightStayBookingId], [PetParentId], [ProviderId]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_NightStayBookings_Provider_Status'
      AND [object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]'))
    CREATE INDEX [IX_NightStayBookings_Provider_Status]
        ON [Booking].[NightStayBookings] ([ProviderId], [Status])
        INCLUDE ([ServiceId], [CheckInDate], [CheckOutDate], [NightStayBookingId], [PetParentId]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_NightStayBookings_PetParent_Status'
      AND [object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]'))
    CREATE INDEX [IX_NightStayBookings_PetParent_Status]
        ON [Booking].[NightStayBookings] ([PetParentId], [Status])
        INCLUDE ([ServiceId], [CheckInDate], [CheckOutDate], [NightStayBookingId], [ProviderId]);
GO

-- 2.10b1 Retrofit: add JobNumber + payout columns to NightStayBookings so the
-- night-stay detail read matches the single-day one (Job ID + payout block).
-- Fires once on DBs created before these columns existed (COL_LENGTH IS NULL).
IF COL_LENGTH(N'[Booking].[NightStayBookings]', N'JobNumber') IS NULL
BEGIN
    PRINT 'Adding [JobNumber] to [Booking].[NightStayBookings].';
    ALTER TABLE [Booking].[NightStayBookings] ADD [JobNumber] INT NOT NULL IDENTITY(1, 1);
END
GO

IF COL_LENGTH(N'[Booking].[NightStayBookings]', N'PayoutStatus') IS NULL
BEGIN
    PRINT 'Adding payout columns to [Booking].[NightStayBookings].';
    ALTER TABLE [Booking].[NightStayBookings] ADD [PayoutStatus] NVARCHAR(32) NOT NULL
        CONSTRAINT [DF_NightStayBookings_PayoutStatus] DEFAULT N'Pending';
    ALTER TABLE [Booking].[NightStayBookings] ADD [PayoutId] NVARCHAR(64) NULL;
    ALTER TABLE [Booking].[NightStayBookings]
        ADD CONSTRAINT [CK_NightStayBookings_PayoutStatus]
            CHECK ([PayoutStatus] IN (N'Pending', N'Processing', N'Paid', N'Failed', N'NO_PAYOUT'));
END
GO

-- Same NO_PAYOUT repair as [Booking].[Bookings] above: neither the CREATE TABLE
-- nor the by-name migration guard can widen a CHECK that already exists.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_NightStayBookings_PayoutStatus'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]')
      AND [definition] NOT LIKE N'%NO[_]PAYOUT%')
BEGIN
    ALTER TABLE [Booking].[NightStayBookings] DROP CONSTRAINT [CK_NightStayBookings_PayoutStatus];
    ALTER TABLE [Booking].[NightStayBookings]
        ADD CONSTRAINT [CK_NightStayBookings_PayoutStatus]
            CHECK ([PayoutStatus] IN (N'Pending', N'Processing', N'Paid', N'Failed', N'NO_PAYOUT'));
    PRINT 'Rebuilt constraint [CK_NightStayBookings_PayoutStatus] with NO_PAYOUT.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_NightStayBookings_JobNumber'
      AND [object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]'))
    CREATE UNIQUE INDEX [UX_NightStayBookings_JobNumber]
        ON [Booking].[NightStayBookings] ([JobNumber]);
GO

-- 2.10b2 Retrofit: add [JobNotes] (optional stay notes captured at booking time)
-- and [LocationType] ('ParentLocation' / 'ProviderLocation' choice) to an
-- existing NightStayBookings table. Fires once (COL_LENGTH IS NULL).
IF COL_LENGTH(N'[Booking].[NightStayBookings]', N'JobNotes') IS NULL
BEGIN
    PRINT 'Adding [JobNotes] to [Booking].[NightStayBookings].';
    ALTER TABLE [Booking].[NightStayBookings] ADD [JobNotes] NVARCHAR(2000) NULL;
END
GO

IF COL_LENGTH(N'[Booking].[NightStayBookings]', N'LocationType') IS NULL
BEGIN
    PRINT 'Adding [LocationType] to [Booking].[NightStayBookings].';
    ALTER TABLE [Booking].[NightStayBookings] ADD [LocationType] NVARCHAR(32) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_NightStayBookings_LocationType')
BEGIN
    ALTER TABLE [Booking].[NightStayBookings]
        ADD CONSTRAINT [CK_NightStayBookings_LocationType]
            CHECK ([LocationType] IS NULL
                OR [LocationType] IN (N'ParentLocation', N'ProviderLocation'));
END
GO


-- 2.10c Booking.NightStayBookingStatusHistory ---------------------------------
-- Append-only audit trail of every night-stay-booking status change. Parallel
-- to [Booking].[BookingStatusHistory], which FKs to the single-day table.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'NightStayBookingStatusHistory' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[NightStayBookingStatusHistory]
    (
        [NightStayBookingStatusHistoryId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_NightStayBookingStatusHistory_Id] DEFAULT NEWSEQUENTIALID(),
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
        [FromStatus] NVARCHAR(48) NULL,
        [ToStatus] NVARCHAR(48) NOT NULL,
        [ChangedByActor] NVARCHAR(16) NOT NULL,
        [ChangedByActorId] UNIQUEIDENTIFIER NULL,
        [Note] NVARCHAR(500) NULL,
        [ChangedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_NightStayBookingStatusHistory_ChangedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_NightStayBookingStatusHistory] PRIMARY KEY CLUSTERED ([NightStayBookingStatusHistoryId] ASC),
        CONSTRAINT [FK_NightStayBookingStatusHistory_NightStayBookings]
            FOREIGN KEY ([NightStayBookingId]) REFERENCES [Booking].[NightStayBookings] ([NightStayBookingId])
            ON DELETE CASCADE,
        CONSTRAINT [CK_NightStayBookingStatusHistory_Actor]
            CHECK ([ChangedByActor] IN (N'Provider', N'Parent', N'System'))
    );
    PRINT 'Created table [Booking].[NightStayBookingStatusHistory].';
END
ELSE
BEGIN
    PRINT 'Table [Booking].[NightStayBookingStatusHistory] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_NightStayBookingStatusHistory_Booking_ChangedAt'
      AND [object_id] = OBJECT_ID(N'[Booking].[NightStayBookingStatusHistory]'))
    CREATE INDEX [IX_NightStayBookingStatusHistory_Booking_ChangedAt]
        ON [Booking].[NightStayBookingStatusHistory] ([NightStayBookingId], [ChangedAtUtc] ASC)
        INCLUDE ([FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note]);
GO


-- 2.10d Retrofit: widen status columns to NVARCHAR(48) + expand the status CHECK
-- lists to the expanded "job" lifecycle (decline, JOB_STARTED, six modification
-- statuses). Detected by the CHECK not yet mentioning 'JOB_STARTED', so it fires
-- once per booking table and never on fresh/already-migrated installs.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Bookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[Bookings]')
      AND [definition] NOT LIKE N'%JOB_STARTED%')
BEGIN
    PRINT 'Expanding [Booking].[Bookings] status set to the job lifecycle.';
    ALTER TABLE [Booking].[Bookings] DROP CONSTRAINT [CK_Bookings_Status];
    ALTER TABLE [Booking].[Bookings] ALTER COLUMN [Status] NVARCHAR(48) NOT NULL;
    ALTER TABLE [Booking].[BookingStatusHistory] ALTER COLUMN [FromStatus] NVARCHAR(48) NULL;
    ALTER TABLE [Booking].[BookingStatusHistory] ALTER COLUMN [ToStatus] NVARCHAR(48) NOT NULL;
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED', N'JOB_STARTED',
                                N'COMPLETED', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED'));
END
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_NightStayBookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]')
      AND [definition] NOT LIKE N'%JOB_STARTED%')
BEGIN
    PRINT 'Expanding [Booking].[NightStayBookings] status set to the job lifecycle.';
    ALTER TABLE [Booking].[NightStayBookings] DROP CONSTRAINT [CK_NightStayBookings_Status];
    ALTER TABLE [Booking].[NightStayBookings] ALTER COLUMN [Status] NVARCHAR(48) NOT NULL;
    ALTER TABLE [Booking].[NightStayBookingStatusHistory] ALTER COLUMN [FromStatus] NVARCHAR(48) NULL;
    ALTER TABLE [Booking].[NightStayBookingStatusHistory] ALTER COLUMN [ToStatus] NVARCHAR(48) NOT NULL;
    ALTER TABLE [Booking].[NightStayBookings]
        ADD CONSTRAINT [CK_NightStayBookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED', N'JOB_STARTED',
                                N'COMPLETED', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED'));
END
GO


-- 2.10d2 Retrofit: add the two no-show statuses (PARENT_NO_SHOW = the parent
-- failed to appear, reported by the provider; PROVIDER_NO_SHOW = the provider
-- failed to appear, reported by the parent) to both booking status CHECK lists.
-- Detected by the CHECK not yet mentioning 'PARENT_NO_SHOW', so it fires once
-- per booking table and never on fresh/already-migrated installs.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Bookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[Bookings]')
      AND [definition] NOT LIKE N'%PARENT_NO_SHOW%')
BEGIN
    PRINT 'Adding the no-show statuses to [Booking].[Bookings].';
    ALTER TABLE [Booking].[Bookings] DROP CONSTRAINT [CK_Bookings_Status];
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED', N'JOB_STARTED',
                                N'COMPLETED', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW'));
END
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_NightStayBookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]')
      AND [definition] NOT LIKE N'%PARENT_NO_SHOW%')
BEGIN
    PRINT 'Adding the no-show statuses to [Booking].[NightStayBookings].';
    ALTER TABLE [Booking].[NightStayBookings] DROP CONSTRAINT [CK_NightStayBookings_Status];
    ALTER TABLE [Booking].[NightStayBookings]
        ADD CONSTRAINT [CK_NightStayBookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED', N'JOB_STARTED',
                                N'COMPLETED', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW'));
END
GO

-- Retrofit: add the EXPIRED status (a CREATED booking left pending for 24+
-- hours is automatically expired — terminal, frees capacity; the provider can
-- no longer accept it) to both booking status CHECK lists. Detected by the
-- CHECK not yet mentioning 'EXPIRED', so it fires once per booking table and
-- never on fresh/already-migrated installs.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Bookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[Bookings]')
      AND [definition] NOT LIKE N'%EXPIRED%')
BEGIN
    PRINT 'Adding the EXPIRED status to [Booking].[Bookings].';
    ALTER TABLE [Booking].[Bookings] DROP CONSTRAINT [CK_Bookings_Status];
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED', N'JOB_STARTED',
                                N'COMPLETED', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                N'EXPIRED'));
END
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_NightStayBookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]')
      AND [definition] NOT LIKE N'%EXPIRED%')
BEGIN
    PRINT 'Adding the EXPIRED status to [Booking].[NightStayBookings].';
    ALTER TABLE [Booking].[NightStayBookings] DROP CONSTRAINT [CK_NightStayBookings_Status];
    ALTER TABLE [Booking].[NightStayBookings]
        ADD CONSTRAINT [CK_NightStayBookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED', N'JOB_STARTED',
                                N'COMPLETED', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                N'EXPIRED'));
END
GO

-- Retrofit (2026-07-25): rename the OTP-cancellation status
-- OTP_ATTEMPTS_EXCEEDED -> OTP_MAX_ATTEMPTS_EXCEEDED so both apps can label it
-- "OTP Max Attempts Exceeded" instead of showing a generic cancellation. Fires
-- once per booking table when the CHECK doesn't yet mention the new value, and
-- jumps straight to the final status list — so the older JOB_EXPIRED / START_JOB
-- / PAID retrofit blocks below then find their tokens present and no-op.
-- Order inside the block matters: the CHECK must be dropped BEFORE the data
-- UPDATE (the old CHECK forbids the new value, the new CHECK forbids the old
-- one), so drop -> migrate rows -> re-add.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Bookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[Bookings]')
      AND [definition] NOT LIKE N'%OTP_MAX_ATTEMPTS_EXCEEDED%')
BEGIN
    PRINT 'Renaming OTP_ATTEMPTS_EXCEEDED -> OTP_MAX_ATTEMPTS_EXCEEDED on [Booking].[Bookings].';
    ALTER TABLE [Booking].[Bookings] DROP CONSTRAINT [CK_Bookings_Status];

    UPDATE [Booking].[Bookings]
    SET [Status] = N'OTP_MAX_ATTEMPTS_EXCEEDED'
    WHERE [Status] = N'OTP_ATTEMPTS_EXCEEDED';

    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED',
                                N'START_JOB', N'IN_PROGRESS', N'ENDING', N'JOB_STARTED',
                                N'COMPLETED', N'PAID', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED'));
END
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_NightStayBookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]')
      AND [definition] NOT LIKE N'%OTP_MAX_ATTEMPTS_EXCEEDED%')
BEGIN
    PRINT 'Renaming OTP_ATTEMPTS_EXCEEDED -> OTP_MAX_ATTEMPTS_EXCEEDED on [Booking].[NightStayBookings].';
    ALTER TABLE [Booking].[NightStayBookings] DROP CONSTRAINT [CK_NightStayBookings_Status];

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'OTP_MAX_ATTEMPTS_EXCEEDED'
    WHERE [Status] = N'OTP_ATTEMPTS_EXCEEDED';

    ALTER TABLE [Booking].[NightStayBookings]
        ADD CONSTRAINT [CK_NightStayBookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED',
                                N'START_JOB', N'IN_PROGRESS', N'ENDING', N'JOB_STARTED',
                                N'COMPLETED', N'PAID', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED'));
END
GO

-- The audit trails carry the status as free text (no CHECK), so their rename is
-- an unconditional idempotent UPDATE.
UPDATE [Booking].[BookingStatusHistory]
SET [ToStatus] = N'OTP_MAX_ATTEMPTS_EXCEEDED'
WHERE [ToStatus] = N'OTP_ATTEMPTS_EXCEEDED';
GO

UPDATE [Booking].[BookingStatusHistory]
SET [FromStatus] = N'OTP_MAX_ATTEMPTS_EXCEEDED'
WHERE [FromStatus] = N'OTP_ATTEMPTS_EXCEEDED';
GO

UPDATE [Booking].[NightStayBookingStatusHistory]
SET [ToStatus] = N'OTP_MAX_ATTEMPTS_EXCEEDED'
WHERE [ToStatus] = N'OTP_ATTEMPTS_EXCEEDED';
GO

UPDATE [Booking].[NightStayBookingStatusHistory]
SET [FromStatus] = N'OTP_MAX_ATTEMPTS_EXCEEDED'
WHERE [FromStatus] = N'OTP_ATTEMPTS_EXCEEDED';
GO

-- Retrofit: add the JOB_EXPIRED (provider accepted but never started the job
-- and the scheduled window elapsed) and OTP_MAX_ATTEMPTS_EXCEEDED (the 6th wrong
-- start-OTP attempt cancelled the job) statuses to both booking status CHECK
-- lists. Both are terminal and free capacity. Detected by the CHECK not yet
-- mentioning 'JOB_EXPIRED', so it fires once per booking table and never on
-- fresh/already-migrated installs.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Bookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[Bookings]')
      AND [definition] NOT LIKE N'%JOB_EXPIRED%')
BEGIN
    PRINT 'Adding the JOB_EXPIRED + OTP_MAX_ATTEMPTS_EXCEEDED statuses to [Booking].[Bookings].';
    ALTER TABLE [Booking].[Bookings] DROP CONSTRAINT [CK_Bookings_Status];
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED', N'JOB_STARTED',
                                N'COMPLETED', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED'));
END
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_NightStayBookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]')
      AND [definition] NOT LIKE N'%JOB_EXPIRED%')
BEGIN
    PRINT 'Adding the JOB_EXPIRED + OTP_MAX_ATTEMPTS_EXCEEDED statuses to [Booking].[NightStayBookings].';
    ALTER TABLE [Booking].[NightStayBookings] DROP CONSTRAINT [CK_NightStayBookings_Status];
    ALTER TABLE [Booking].[NightStayBookings]
        ADD CONSTRAINT [CK_NightStayBookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED', N'JOB_STARTED',
                                N'COMPLETED', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED'));
END
GO


-- Retrofit: add the dual-OTP job-lifecycle statuses START_JOB (provider tapped
-- "Start Job"; start-OTP issued) / IN_PROGRESS (start-OTP verified) / ENDING
-- (retired "End Job" state, kept for legacy rows) to both booking status CHECK lists.
-- Detected by the CHECK not yet mentioning 'START_JOB', so it fires once per
-- booking table and never on fresh/already-migrated installs.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Bookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[Bookings]')
      AND [definition] NOT LIKE N'%START_JOB%')
BEGIN
    PRINT 'Adding the START_JOB / IN_PROGRESS / ENDING statuses to [Booking].[Bookings].';
    ALTER TABLE [Booking].[Bookings] DROP CONSTRAINT [CK_Bookings_Status];
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED',
                                N'START_JOB', N'IN_PROGRESS', N'ENDING', N'JOB_STARTED',
                                N'COMPLETED', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED'));
END
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_NightStayBookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]')
      AND [definition] NOT LIKE N'%START_JOB%')
BEGIN
    PRINT 'Adding the START_JOB / IN_PROGRESS / ENDING statuses to [Booking].[NightStayBookings].';
    ALTER TABLE [Booking].[NightStayBookings] DROP CONSTRAINT [CK_NightStayBookings_Status];
    ALTER TABLE [Booking].[NightStayBookings]
        ADD CONSTRAINT [CK_NightStayBookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED',
                                N'START_JOB', N'IN_PROGRESS', N'ENDING', N'JOB_STARTED',
                                N'COMPLETED', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED'));
END
GO

-- Retrofit (2026-07-23): add the terminal PAID status (the parent has paid the
-- provider; a row is written to [Booking].[BookingPayments]) to both booking
-- status CHECK lists. Detected by the CHECK not yet mentioning 'PAID', so it
-- fires once per booking table and never on fresh/already-migrated installs.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Bookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[Bookings]')
      AND [definition] NOT LIKE N'%PAID%')
BEGIN
    PRINT 'Adding the PAID status to [Booking].[Bookings].';
    ALTER TABLE [Booking].[Bookings] DROP CONSTRAINT [CK_Bookings_Status];
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED',
                                N'START_JOB', N'IN_PROGRESS', N'ENDING', N'JOB_STARTED',
                                N'COMPLETED', N'PAID', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED'));
END
GO

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_NightStayBookings_Status'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[NightStayBookings]')
      AND [definition] NOT LIKE N'%PAID%')
BEGIN
    PRINT 'Adding the PAID status to [Booking].[NightStayBookings].';
    ALTER TABLE [Booking].[NightStayBookings] DROP CONSTRAINT [CK_NightStayBookings_Status];
    ALTER TABLE [Booking].[NightStayBookings]
        ADD CONSTRAINT [CK_NightStayBookings_Status]
            CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED',
                                N'START_JOB', N'IN_PROGRESS', N'ENDING', N'JOB_STARTED',
                                N'COMPLETED', N'PAID', N'APPROVAL_NEEDED',
                                N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                                N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                                N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED'));
END
GO

-- Backfill (2026-07-29): an accepted job whose scheduled window elapsed without
-- ever getting underway is a NO-SHOW, not a neutral expiry. The old sweep wrote
-- JOB_EXPIRED for that case until the no-show arm superseded it, so relabel the
-- rows it already produced — otherwise the same situation reads two different
-- ways depending on when it happened.
--
-- Who was absent is NOT guessed. It is read back from the audit trail: the
-- JOB_EXPIRED history row records the status the booking held when it was swept,
-- which is exactly the evidence the live arm branches on — START_JOB (the
-- provider was there and the start code was issued, but the parent never handed
-- it back) -> PARENT_NO_SHOW; any confirmed-equivalent state (the provider never
-- so much as tapped Start) -> PROVIDER_NO_SHOW.
--
-- A row with no JOB_EXPIRED audit entry is LEFT ALONE — there is no evidence to
-- attribute it, and JOB_EXPIRED remains a valid terminal status. Capacity is
-- unaffected either way: all three statuses are in the capacity-freeing set.
-- Idempotent — once converted, no JOB_EXPIRED rows remain for a re-run to find.
-- Runs after the status CHECK retrofits above, so the no-show values are already
-- permitted. Single-day only, by decision; night-stay JOB_EXPIRED rows are left
-- as they are.
DECLARE @BackfilledNoShows TABLE (
    [BookingId] UNIQUEIDENTIFIER NOT NULL,
    [ToStatus]  NVARCHAR(48)     NOT NULL);
DECLARE @NoShowBackfillNote NVARCHAR(500) =
    N'Relabelled from JOB_EXPIRED: an accepted job whose scheduled window elapsed unstarted is recorded as a no-show. '
    + N'Attribution taken from the status the booking held when it was swept.';

IF EXISTS (SELECT 1 FROM [Booking].[Bookings] WHERE [Status] = N'JOB_EXPIRED')
BEGIN
    ;WITH [SweptFrom] AS (
        SELECT h.[BookingId],
               h.[FromStatus],
               ROW_NUMBER() OVER (PARTITION BY h.[BookingId] ORDER BY h.[ChangedAtUtc] DESC) AS [Rn]
        FROM [Booking].[BookingStatusHistory] h
        WHERE h.[ToStatus] = N'JOB_EXPIRED'
    )
    UPDATE b
    SET [Status] = CASE WHEN s.[FromStatus] = N'START_JOB' THEN N'PARENT_NO_SHOW'
                        ELSE N'PROVIDER_NO_SHOW' END,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    OUTPUT inserted.[BookingId], inserted.[Status] INTO @BackfilledNoShows
    FROM [Booking].[Bookings] b
    INNER JOIN [SweptFrom] s ON s.[BookingId] = b.[BookingId] AND s.[Rn] = 1
    WHERE b.[Status] = N'JOB_EXPIRED';

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [BookingId], N'JOB_EXPIRED', [ToStatus], N'System', NULL, @NoShowBackfillNote
    FROM @BackfilledNoShows;

    -- PRINT takes a scalar expression only — a subquery inline here is a parse
    -- error (Msg 1046), so the counts are materialised into variables first.
    DECLARE @ProviderNoShowBackfilled INT, @ParentNoShowBackfilled INT;
    SELECT @ProviderNoShowBackfilled = COUNT(*) FROM @BackfilledNoShows WHERE [ToStatus] = N'PROVIDER_NO_SHOW';
    SELECT @ParentNoShowBackfilled = COUNT(*) FROM @BackfilledNoShows WHERE [ToStatus] = N'PARENT_NO_SHOW';

    PRINT 'Backfilled '
        + CAST(@ProviderNoShowBackfilled AS NVARCHAR(16))
        + ' booking(s) to PROVIDER_NO_SHOW and '
        + CAST(@ParentNoShowBackfilled AS NVARCHAR(16))
        + ' to PARENT_NO_SHOW (was JOB_EXPIRED).';

    IF EXISTS (SELECT 1 FROM [Booking].[Bookings] WHERE [Status] = N'JOB_EXPIRED')
        PRINT 'NOTE: some JOB_EXPIRED booking(s) have no JOB_EXPIRED audit row and were left unchanged.';
END
GO

-- Retrofit (2026-07-23): snapshot the per-night rate onto NightStayBookings so a
-- later rate change never re-prices an existing stay (single-day already has
-- [PricePerHour]). Fires once when the column is absent.
IF COL_LENGTH(N'[Booking].[NightStayBookings]', N'PricePerNight') IS NULL
BEGIN
    PRINT 'Adding [PricePerNight] to [Booking].[NightStayBookings].';
    ALTER TABLE [Booking].[NightStayBookings] ADD [PricePerNight] DECIMAL(10, 2) NULL;
END
GO

-- Retrofit (2026-07-24): snapshot the cancellation policy + the SELECTED
-- service-location address onto bookings so later provider edits never re-rule /
-- re-address an existing booking (price-lock siblings). Single-day + night-stay.
IF COL_LENGTH(N'[Booking].[Bookings]', N'CancellationPolicyHours') IS NULL
BEGIN
    PRINT 'Adding cancellation-policy + address snapshot columns to [Booking].[Bookings].';
    ALTER TABLE [Booking].[Bookings] ADD
        [CancellationPolicyHours] INT NULL,
        [SnapshotAddressLine] NVARCHAR(500) NULL,
        [SnapshotCity] NVARCHAR(200) NULL,
        [SnapshotZipCode] NVARCHAR(32) NULL,
        [SnapshotLatitude] DECIMAL(9, 6) NULL,
        [SnapshotLongitude] DECIMAL(9, 6) NULL;
END
GO

IF COL_LENGTH(N'[Booking].[NightStayBookings]', N'CancellationPolicyHours') IS NULL
BEGIN
    PRINT 'Adding cancellation-policy + address snapshot columns to [Booking].[NightStayBookings].';
    ALTER TABLE [Booking].[NightStayBookings] ADD
        [CancellationPolicyHours] INT NULL,
        [SnapshotAddressLine] NVARCHAR(500) NULL,
        [SnapshotCity] NVARCHAR(200) NULL,
        [SnapshotZipCode] NVARCHAR(32) NULL,
        [SnapshotLatitude] DECIMAL(9, 6) NULL,
        [SnapshotLongitude] DECIMAL(9, 6) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Bookings_CancellationPolicyHours')
BEGIN
    ALTER TABLE [Booking].[Bookings]
        ADD CONSTRAINT [CK_Bookings_CancellationPolicyHours]
            CHECK ([CancellationPolicyHours] IS NULL
                   OR [CancellationPolicyHours] IN (24, 48, 72, 96));
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_NightStayBookings_CancellationPolicyHours')
BEGIN
    ALTER TABLE [Booking].[NightStayBookings]
        ADD CONSTRAINT [CK_NightStayBookings_CancellationPolicyHours]
            CHECK ([CancellationPolicyHours] IS NULL
                   OR [CancellationPolicyHours] IN (24, 48, 72, 96));
END
GO

-- One-time backfill (2026-07-24): freeze EXISTING bookings at CURRENT values so even
-- already-created (e.g. CONFIRMED) bookings stop tracking later provider edits.
-- Guarded on IS NULL, so re-runs are no-ops.
--   * Cancellation policy: from the provider's current policy row (INNER JOIN — a
--     provider with no policy row leaves the booking NULL = "no restriction").
--   * Selected-location address: ParentLocation rows snapshot the parent's current
--     profile address (available in SQL). ProviderLocation rows are LEFT untouched
--     here — the street/city/zip live in Cosmos (unreachable from SQL), so they keep
--     live-resolving on read (a future C# pass can freeze them). Likewise legacy App
--     [PricePerHour] keeps live-falling-back (the offering rate is Cosmos-sourced).
UPDATE b
SET b.[CancellationPolicyHours] = p.[MinimumHoursBeforeCancellation]
FROM [Booking].[Bookings] AS b
INNER JOIN [Provider].[ProviderCancellationPolicies] AS p
    ON p.[ProviderId] = b.[ProviderId]
WHERE b.[CancellationPolicyHours] IS NULL;
GO

UPDATE b
SET b.[CancellationPolicyHours] = p.[MinimumHoursBeforeCancellation]
FROM [Booking].[NightStayBookings] AS b
INNER JOIN [Provider].[ProviderCancellationPolicies] AS p
    ON p.[ProviderId] = b.[ProviderId]
WHERE b.[CancellationPolicyHours] IS NULL;
GO

UPDATE b
SET b.[SnapshotAddressLine] = pp.[AddressLine],
    b.[SnapshotCity]        = pp.[City],
    b.[SnapshotZipCode]     = pp.[ZipCode],
    b.[SnapshotLatitude]    = pp.[Latitude],
    b.[SnapshotLongitude]   = pp.[Longitude]
FROM [Booking].[Bookings] AS b
INNER JOIN [Parent].[PetParents] AS pp ON pp.[PetParentId] = b.[PetParentId]
WHERE b.[LocationType] = N'ParentLocation'
  AND b.[SnapshotAddressLine] IS NULL;
GO

UPDATE b
SET b.[SnapshotAddressLine] = pp.[AddressLine],
    b.[SnapshotCity]        = pp.[City],
    b.[SnapshotZipCode]     = pp.[ZipCode],
    b.[SnapshotLatitude]    = pp.[Latitude],
    b.[SnapshotLongitude]   = pp.[Longitude]
FROM [Booking].[NightStayBookings] AS b
INNER JOIN [Parent].[PetParents] AS pp ON pp.[PetParentId] = b.[PetParentId]
WHERE b.[LocationType] = N'ParentLocation'
  AND b.[SnapshotAddressLine] IS NULL;
GO


-- 2.10e Booking verification OTPs, evidence, modifications (single-day + night-stay) --
-- Verification OTPs: a low-secrecy share code the parent reads to the provider —
-- the start code that gates START_JOB → IN_PROGRESS (completion needs no OTP).
-- Evidence: optional completion photos. Modifications: pending/resolved
-- schedule-change proposals.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE [name] = N'BookingStartOtps' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[BookingStartOtps]
    (
        [BookingStartOtpId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_BookingStartOtps_Id] DEFAULT NEWSEQUENTIALID(),
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [OtpCode] NVARCHAR(6) NOT NULL,
        [Status] NVARCHAR(16) NOT NULL
            CONSTRAINT [DF_BookingStartOtps_Status] DEFAULT N'Pending',
        [FailedAttemptCount] INT NOT NULL
            CONSTRAINT [DF_BookingStartOtps_FailedAttemptCount] DEFAULT 0,
        [IssuedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_BookingStartOtps_IssuedAtUtc] DEFAULT SYSUTCDATETIME(),
        [ExpiresAtUtc] DATETIME2(7) NOT NULL,
        [ConsumedAtUtc] DATETIME2(7) NULL,
        [SeenAtUtc] DATETIME2(7) NULL,
        CONSTRAINT [PK_BookingStartOtps] PRIMARY KEY CLUSTERED ([BookingStartOtpId] ASC),
        CONSTRAINT [FK_BookingStartOtps_Bookings_BookingId]
            FOREIGN KEY ([BookingId]) REFERENCES [Booking].[Bookings] ([BookingId]) ON DELETE CASCADE,
        CONSTRAINT [CK_BookingStartOtps_Status] CHECK ([Status] IN (N'Pending', N'Consumed', N'Expired'))
    );
    PRINT 'Created table [Booking].[BookingStartOtps].';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_BookingStartOtps_Booking_Issued' AND [object_id] = OBJECT_ID(N'[Booking].[BookingStartOtps]'))
    CREATE INDEX [IX_BookingStartOtps_Booking_Issued]
        ON [Booking].[BookingStartOtps] ([BookingId], [IssuedAtUtc] DESC)
        INCLUDE ([OtpCode], [Status], [ExpiresAtUtc]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE [name] = N'BookingEvidence' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[BookingEvidence]
    (
        [BookingEvidenceId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_BookingEvidence_Id] DEFAULT NEWSEQUENTIALID(),
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [PhotoUrl] NVARCHAR(1000) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_BookingEvidence_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_BookingEvidence] PRIMARY KEY CLUSTERED ([BookingEvidenceId] ASC),
        CONSTRAINT [FK_BookingEvidence_Bookings_BookingId]
            FOREIGN KEY ([BookingId]) REFERENCES [Booking].[Bookings] ([BookingId]) ON DELETE CASCADE
    );
    PRINT 'Created table [Booking].[BookingEvidence].';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_BookingEvidence_Booking_Created' AND [object_id] = OBJECT_ID(N'[Booking].[BookingEvidence]'))
    CREATE INDEX [IX_BookingEvidence_Booking_Created]
        ON [Booking].[BookingEvidence] ([BookingId], [CreatedAtUtc] ASC) INCLUDE ([PhotoUrl]);
GO

-- Staging area for the open date/time-change proposal (pending-only; the row is
-- DELETED on accept/decline). If a legacy-shape table from an earlier deploy of
-- this same unreleased feature exists (detected by the dropped [Status] column),
-- recreate it — the table holds no durable data.
IF OBJECT_ID(N'[Booking].[BookingModifications]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[Booking].[BookingModifications]', N'Status') IS NOT NULL
BEGIN
    PRINT 'Recreating [Booking].[BookingModifications] as date/time-only staging.';
    DROP TABLE [Booking].[BookingModifications];
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE [name] = N'BookingModifications' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[BookingModifications]
    (
        [BookingModificationId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_BookingModifications_Id] DEFAULT NEWSEQUENTIALID(),
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [RequestedByActor] NVARCHAR(16) NOT NULL,
        [RequestedByActorId] UNIQUEIDENTIFIER NOT NULL,
        [ProposedBookingDate] DATE NOT NULL,
        [ProposedStartTime] TIME(0) NOT NULL,
        [ProposedEndTime] TIME(0) NOT NULL,
        [RequestNote] NVARCHAR(500) NULL,
        [HasAcknowledgedTerms] BIT NOT NULL
            CONSTRAINT [DF_BookingModifications_HasAcknowledgedTerms] DEFAULT 0,
        [AcknowledgedPricePerHour] DECIMAL(10, 2) NULL,
        [AcknowledgedCancellationPolicyHours] INT NULL,
        [AcknowledgedAddressLine] NVARCHAR(500) NULL,
        [AcknowledgedCity] NVARCHAR(200) NULL,
        [AcknowledgedZipCode] NVARCHAR(32) NULL,
        [AcknowledgedLatitude] DECIMAL(9, 6) NULL,
        [AcknowledgedLongitude] DECIMAL(9, 6) NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_BookingModifications_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_BookingModifications] PRIMARY KEY CLUSTERED ([BookingModificationId] ASC),
        CONSTRAINT [UQ_BookingModifications_BookingId] UNIQUE ([BookingId]),
        CONSTRAINT [FK_BookingModifications_Bookings_BookingId]
            FOREIGN KEY ([BookingId]) REFERENCES [Booking].[Bookings] ([BookingId]) ON DELETE CASCADE,
        CONSTRAINT [CK_BookingModifications_RequestedByActor] CHECK ([RequestedByActor] IN (N'Provider', N'Parent')),
        CONSTRAINT [CK_BookingModifications_TimeOrder] CHECK ([ProposedStartTime] < [ProposedEndTime]),
        CONSTRAINT [CK_BookingModifications_AcknowledgedPrice]
            CHECK ([AcknowledgedPricePerHour] IS NULL OR [AcknowledgedPricePerHour] >= 0),
        CONSTRAINT [CK_BookingModifications_AcknowledgedCancellationPolicy]
            CHECK ([AcknowledgedCancellationPolicyHours] IS NULL
                   OR [AcknowledgedCancellationPolicyHours] IN (24, 48, 72, 96))
    );
    PRINT 'Created table [Booking].[BookingModifications].';
END
GO

-- Terms-drift acknowledgement (2026-07-27): the requester's confirmation of the
-- provider's CURRENT terms is staged next to the proposed schedule, so the
-- counterparty's accept applies exactly the values the requester was shown.
-- Added to an already-created staging table.
IF COL_LENGTH(N'[Booking].[BookingModifications]', N'HasAcknowledgedTerms') IS NULL
BEGIN
    ALTER TABLE [Booking].[BookingModifications]
        ADD [HasAcknowledgedTerms] BIT NOT NULL
                CONSTRAINT [DF_BookingModifications_HasAcknowledgedTerms] DEFAULT 0,
            [AcknowledgedPricePerHour] DECIMAL(10, 2) NULL,
            [AcknowledgedCancellationPolicyHours] INT NULL,
            [AcknowledgedAddressLine] NVARCHAR(500) NULL,
            [AcknowledgedCity] NVARCHAR(200) NULL,
            [AcknowledgedZipCode] NVARCHAR(32) NULL,
            [AcknowledgedLatitude] DECIMAL(9, 6) NULL,
            [AcknowledgedLongitude] DECIMAL(9, 6) NULL;
    PRINT 'Added acknowledged-terms columns to [Booking].[BookingModifications].';
END
GO
-- NB: the guard must be schema-qualified. A bare OBJECT_ID(N'[CK_...]', N'C')
-- resolves against the CALLER's default schema (dbo), never [Booking], so it
-- returned NULL even when the constraint existed and the ADD then failed 2714.
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_BookingModifications_AcknowledgedPrice'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[BookingModifications]'))
BEGIN
    ALTER TABLE [Booking].[BookingModifications] WITH CHECK
        ADD CONSTRAINT [CK_BookingModifications_AcknowledgedPrice]
            CHECK ([AcknowledgedPricePerHour] IS NULL OR [AcknowledgedPricePerHour] >= 0);
END
GO
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_BookingModifications_AcknowledgedCancellationPolicy'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[BookingModifications]'))
BEGIN
    ALTER TABLE [Booking].[BookingModifications] WITH CHECK
        ADD CONSTRAINT [CK_BookingModifications_AcknowledgedCancellationPolicy]
            CHECK ([AcknowledgedCancellationPolicyHours] IS NULL
                   OR [AcknowledgedCancellationPolicyHours] IN (24, 48, 72, 96));
END
GO

-- Booking.BookingPrescriptions: one per-visit Vet prescription per booking,
-- upserted. Vaccinations is a JSON array of vaccine names (app-serialized).
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE [name] = N'BookingPrescriptions' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[BookingPrescriptions]
    (
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [PrescriptionText] NVARCHAR(4000) NULL,
        [IsPetVaccinated] BIT NOT NULL,
        [Vaccinations] NVARCHAR(MAX) NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_BookingPrescriptions_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_BookingPrescriptions_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_BookingPrescriptions] PRIMARY KEY CLUSTERED ([BookingId] ASC),
        CONSTRAINT [FK_BookingPrescriptions_Bookings_BookingId]
            FOREIGN KEY ([BookingId]) REFERENCES [Booking].[Bookings] ([BookingId]) ON DELETE CASCADE
    );
    PRINT 'Created table [Booking].[BookingPrescriptions].';
END
GO

-- Booking.BookingPayments: one payment row per paid booking (written when the
-- provider marks a COMPLETED booking PAID). Financial ledger — NO FK to the
-- booking tables (payment history survives booking deletion); keyed by
-- (BookingType, BookingId). Source for the per-provider "total received" report.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE [name] = N'BookingPayments' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[BookingPayments]
    (
        [BookingPaymentId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_BookingPayments_Id] DEFAULT NEWSEQUENTIALID(),
        [BookingType] NVARCHAR(16) NOT NULL,
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [PetParentId] UNIQUEIDENTIFIER NOT NULL,
        [Amount] DECIMAL(10, 2) NOT NULL,
        [PawfrontFee] DECIMAL(10, 2) NOT NULL
            CONSTRAINT [DF_BookingPayments_PawfrontFee] DEFAULT 0,
        [PaymentMethod] NVARCHAR(16) NOT NULL,
        [PaidAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_BookingPayments_PaidAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_BookingPayments] PRIMARY KEY CLUSTERED ([BookingPaymentId] ASC),
        CONSTRAINT [UQ_BookingPayments_Booking] UNIQUE ([BookingType], [BookingId]),
        CONSTRAINT [CK_BookingPayments_BookingType] CHECK ([BookingType] IN (N'SingleDay', N'NightStay')),
        CONSTRAINT [CK_BookingPayments_PaymentMethod] CHECK ([PaymentMethod] IN (N'Cash', N'Digital')),
        CONSTRAINT [CK_BookingPayments_Amount] CHECK ([Amount] >= 0),
        CONSTRAINT [CK_BookingPayments_PawfrontFee] CHECK ([PawfrontFee] >= 0)
    );
    PRINT 'Created table [Booking].[BookingPayments].';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_BookingPayments_Provider' AND [object_id] = OBJECT_ID(N'[Booking].[BookingPayments]'))
    CREATE INDEX [IX_BookingPayments_Provider]
        ON [Booking].[BookingPayments] ([ProviderId])
        INCLUDE ([Amount], [PawfrontFee], [PaidAtUtc]);
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE [name] = N'NightStayBookingStartOtps' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[NightStayBookingStartOtps]
    (
        [NightStayBookingStartOtpId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_NightStayBookingStartOtps_Id] DEFAULT NEWSEQUENTIALID(),
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
        [OtpCode] NVARCHAR(6) NOT NULL,
        [Status] NVARCHAR(16) NOT NULL
            CONSTRAINT [DF_NightStayBookingStartOtps_Status] DEFAULT N'Pending',
        [FailedAttemptCount] INT NOT NULL
            CONSTRAINT [DF_NightStayBookingStartOtps_FailedAttemptCount] DEFAULT 0,
        [IssuedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_NightStayBookingStartOtps_IssuedAtUtc] DEFAULT SYSUTCDATETIME(),
        [ExpiresAtUtc] DATETIME2(7) NOT NULL,
        [ConsumedAtUtc] DATETIME2(7) NULL,
        [SeenAtUtc] DATETIME2(7) NULL,
        CONSTRAINT [PK_NightStayBookingStartOtps] PRIMARY KEY CLUSTERED ([NightStayBookingStartOtpId] ASC),
        CONSTRAINT [FK_NightStayBookingStartOtps_NightStayBookings]
            FOREIGN KEY ([NightStayBookingId]) REFERENCES [Booking].[NightStayBookings] ([NightStayBookingId]) ON DELETE CASCADE,
        CONSTRAINT [CK_NightStayBookingStartOtps_Status] CHECK ([Status] IN (N'Pending', N'Consumed', N'Expired'))
    );
    PRINT 'Created table [Booking].[NightStayBookingStartOtps].';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_NightStayBookingStartOtps_Booking_Issued' AND [object_id] = OBJECT_ID(N'[Booking].[NightStayBookingStartOtps]'))
    CREATE INDEX [IX_NightStayBookingStartOtps_Booking_Issued]
        ON [Booking].[NightStayBookingStartOtps] ([NightStayBookingId], [IssuedAtUtc] DESC)
        INCLUDE ([OtpCode], [Status], [ExpiresAtUtc]);
GO

-- Retrofit: the two-phase (Start/End) OTP model was retired 2026-07-23 — the OTP
-- now gates only START_JOB → IN_PROGRESS, so the [OtpKind] discriminator is
-- dropped wherever an earlier deploy added it. Fires once per table.
IF COL_LENGTH(N'[Booking].[BookingStartOtps]', N'OtpKind') IS NOT NULL
BEGIN
    PRINT 'Dropping retired [OtpKind] from [Booking].[BookingStartOtps].';
    ALTER TABLE [Booking].[BookingStartOtps] DROP CONSTRAINT IF EXISTS [CK_BookingStartOtps_OtpKind];
    ALTER TABLE [Booking].[BookingStartOtps] DROP CONSTRAINT IF EXISTS [DF_BookingStartOtps_OtpKind];
    ALTER TABLE [Booking].[BookingStartOtps] DROP COLUMN [OtpKind];
END
GO
IF COL_LENGTH(N'[Booking].[NightStayBookingStartOtps]', N'OtpKind') IS NOT NULL
BEGIN
    PRINT 'Dropping retired [OtpKind] from [Booking].[NightStayBookingStartOtps].';
    ALTER TABLE [Booking].[NightStayBookingStartOtps] DROP CONSTRAINT IF EXISTS [CK_NightStayBookingStartOtps_OtpKind];
    ALTER TABLE [Booking].[NightStayBookingStartOtps] DROP CONSTRAINT IF EXISTS [DF_NightStayBookingStartOtps_OtpKind];
    ALTER TABLE [Booking].[NightStayBookingStartOtps] DROP COLUMN [OtpKind];
END
GO

-- [SeenAtUtc] (2026-08-04) — when the PARENT first actually saw the start code,
-- stamped by Booking.IssueBookingStartOtp (the sproc that returns it to them).
-- Separates the two start-code nudges: "you haven't opened your code" versus
-- "you have it, the provider still hasn't entered it". NULL on existing rows,
-- which reads correctly as "not seen".
IF COL_LENGTH(N'[Booking].[BookingStartOtps]', N'SeenAtUtc') IS NULL
BEGIN
    ALTER TABLE [Booking].[BookingStartOtps] ADD [SeenAtUtc] DATETIME2(7) NULL;
    PRINT 'Added [SeenAtUtc] to [Booking].[BookingStartOtps].';
END
GO

IF COL_LENGTH(N'[Booking].[NightStayBookingStartOtps]', N'SeenAtUtc') IS NULL
BEGIN
    ALTER TABLE [Booking].[NightStayBookingStartOtps] ADD [SeenAtUtc] DATETIME2(7) NULL;
    PRINT 'Added [SeenAtUtc] to [Booking].[NightStayBookingStartOtps].';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE [name] = N'NightStayBookingEvidence' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[NightStayBookingEvidence]
    (
        [NightStayBookingEvidenceId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_NightStayBookingEvidence_Id] DEFAULT NEWSEQUENTIALID(),
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
        [PhotoUrl] NVARCHAR(1000) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_NightStayBookingEvidence_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_NightStayBookingEvidence] PRIMARY KEY CLUSTERED ([NightStayBookingEvidenceId] ASC),
        CONSTRAINT [FK_NightStayBookingEvidence_NightStayBookings]
            FOREIGN KEY ([NightStayBookingId]) REFERENCES [Booking].[NightStayBookings] ([NightStayBookingId]) ON DELETE CASCADE
    );
    PRINT 'Created table [Booking].[NightStayBookingEvidence].';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_NightStayBookingEvidence_Booking_Created' AND [object_id] = OBJECT_ID(N'[Booking].[NightStayBookingEvidence]'))
    CREATE INDEX [IX_NightStayBookingEvidence_Booking_Created]
        ON [Booking].[NightStayBookingEvidence] ([NightStayBookingId], [CreatedAtUtc] ASC) INCLUDE ([PhotoUrl]);
GO

-- Where each party was at the moments of a booking that matter in a dispute:
-- arrival, job start, no-show, the parent showing their start code, cash changing
-- hands (or not), and evidence capture. Deliberately separate from the status-history
-- audit trail: an audit row has ONE actor while several of these triggers want both
-- parties' positions, and half of them are not status changes at all. Coordinates are
-- NOT NULL — the API refuses the action without a usable fix, so a moment either has
-- its location or never happened.
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE [name] = N'BookingLocationEvents' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[BookingLocationEvents]
    (
        [BookingLocationEventId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_BookingLocationEvents_Id] DEFAULT NEWSEQUENTIALID(),
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [Trigger] NVARCHAR(32) NOT NULL,
        [CapturedByType] NVARCHAR(16) NOT NULL,
        [CapturedById] UNIQUEIDENTIFIER NOT NULL,
        [Latitude] DECIMAL(9, 6) NOT NULL,
        [Longitude] DECIMAL(9, 6) NOT NULL,
        [AccuracyMetres] DECIMAL(9, 2) NULL,
        [DeviceCapturedAtUtc] DATETIME2(7) NULL,
        [RecordedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_BookingLocationEvents_RecordedAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_BookingLocationEvents] PRIMARY KEY CLUSTERED ([BookingLocationEventId] ASC),
        CONSTRAINT [FK_BookingLocationEvents_Bookings_BookingId]
            FOREIGN KEY ([BookingId]) REFERENCES [Booking].[Bookings] ([BookingId]) ON DELETE CASCADE,
        CONSTRAINT [CK_BookingLocationEvents_Trigger]
            CHECK ([Trigger] IN (N'ArrivalConfirmed', N'JobStartProceeded', N'NoShowMarked',
                                N'StartOtpShown', N'CashReceived', N'CashNotReceived',
                                N'EvidenceCaptured')),
        CONSTRAINT [CK_BookingLocationEvents_CapturedByType]
            CHECK ([CapturedByType] IN (N'Provider', N'Parent')),
        CONSTRAINT [CK_BookingLocationEvents_TriggerParty]
            CHECK (NOT ([Trigger] IN (N'ArrivalConfirmed', N'JobStartProceeded') AND [CapturedByType] = N'Parent')
               AND NOT ([Trigger] = N'StartOtpShown' AND [CapturedByType] = N'Provider')),
        CONSTRAINT [CK_BookingLocationEvents_Latitude] CHECK ([Latitude] BETWEEN -90 AND 90),
        CONSTRAINT [CK_BookingLocationEvents_Longitude] CHECK ([Longitude] BETWEEN -180 AND 180),
        CONSTRAINT [CK_BookingLocationEvents_AccuracyMetres] CHECK ([AccuracyMetres] IS NULL OR [AccuracyMetres] >= 0)
    );
    PRINT 'Created table [Booking].[BookingLocationEvents].';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_BookingLocationEvents_Booking_RecordedAt' AND [object_id] = OBJECT_ID(N'[Booking].[BookingLocationEvents]'))
    CREATE INDEX [IX_BookingLocationEvents_Booking_RecordedAt]
        ON [Booking].[BookingLocationEvents] ([BookingId], [RecordedAtUtc] ASC)
        INCLUDE ([Trigger], [CapturedByType], [CapturedById], [Latitude], [Longitude],
                 [AccuracyMetres], [DeviceCapturedAtUtc]);
GO

-- Night-stay twin. Twinned rather than discriminated because that is what every
-- other lifecycle child table here does; the discriminated shape is reserved for
-- the tables that deliberately carry no FK, such as [Booking].[BookingPayments].
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE [name] = N'NightStayBookingLocationEvents' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[NightStayBookingLocationEvents]
    (
        [NightStayBookingLocationEventId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_NightStayBookingLocationEvents_Id] DEFAULT NEWSEQUENTIALID(),
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
        [Trigger] NVARCHAR(32) NOT NULL,
        [CapturedByType] NVARCHAR(16) NOT NULL,
        [CapturedById] UNIQUEIDENTIFIER NOT NULL,
        [Latitude] DECIMAL(9, 6) NOT NULL,
        [Longitude] DECIMAL(9, 6) NOT NULL,
        [AccuracyMetres] DECIMAL(9, 2) NULL,
        [DeviceCapturedAtUtc] DATETIME2(7) NULL,
        [RecordedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_NightStayBookingLocationEvents_RecordedAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_NightStayBookingLocationEvents] PRIMARY KEY CLUSTERED ([NightStayBookingLocationEventId] ASC),
        CONSTRAINT [FK_NightStayBookingLocationEvents_NightStayBookings]
            FOREIGN KEY ([NightStayBookingId]) REFERENCES [Booking].[NightStayBookings] ([NightStayBookingId]) ON DELETE CASCADE,
        CONSTRAINT [CK_NightStayBookingLocationEvents_Trigger]
            CHECK ([Trigger] IN (N'ArrivalConfirmed', N'JobStartProceeded', N'NoShowMarked',
                                N'StartOtpShown', N'CashReceived', N'CashNotReceived',
                                N'EvidenceCaptured')),
        CONSTRAINT [CK_NightStayBookingLocationEvents_CapturedByType]
            CHECK ([CapturedByType] IN (N'Provider', N'Parent')),
        CONSTRAINT [CK_NightStayBookingLocationEvents_TriggerParty]
            CHECK (NOT ([Trigger] IN (N'ArrivalConfirmed', N'JobStartProceeded') AND [CapturedByType] = N'Parent')
               AND NOT ([Trigger] = N'StartOtpShown' AND [CapturedByType] = N'Provider')),
        CONSTRAINT [CK_NightStayBookingLocationEvents_Latitude] CHECK ([Latitude] BETWEEN -90 AND 90),
        CONSTRAINT [CK_NightStayBookingLocationEvents_Longitude] CHECK ([Longitude] BETWEEN -180 AND 180),
        CONSTRAINT [CK_NightStayBookingLocationEvents_AccuracyMetres] CHECK ([AccuracyMetres] IS NULL OR [AccuracyMetres] >= 0)
    );
    PRINT 'Created table [Booking].[NightStayBookingLocationEvents].';
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE [name] = N'IX_NightStayBookingLocationEvents_Booking_RecordedAt' AND [object_id] = OBJECT_ID(N'[Booking].[NightStayBookingLocationEvents]'))
    CREATE INDEX [IX_NightStayBookingLocationEvents_Booking_RecordedAt]
        ON [Booking].[NightStayBookingLocationEvents] ([NightStayBookingId], [RecordedAtUtc] ASC)
        INCLUDE ([Trigger], [CapturedByType], [CapturedById], [Latitude], [Longitude],
                 [AccuracyMetres], [DeviceCapturedAtUtc]);
GO

IF OBJECT_ID(N'[Booking].[NightStayBookingModifications]', N'U') IS NOT NULL
   AND COL_LENGTH(N'[Booking].[NightStayBookingModifications]', N'Status') IS NOT NULL
BEGIN
    PRINT 'Recreating [Booking].[NightStayBookingModifications] as date-only staging.';
    DROP TABLE [Booking].[NightStayBookingModifications];
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE [name] = N'NightStayBookingModifications' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE TABLE [Booking].[NightStayBookingModifications]
    (
        [NightStayBookingModificationId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_NightStayBookingModifications_Id] DEFAULT NEWSEQUENTIALID(),
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
        [RequestedByActor] NVARCHAR(16) NOT NULL,
        [RequestedByActorId] UNIQUEIDENTIFIER NOT NULL,
        [ProposedCheckInDate] DATE NOT NULL,
        [ProposedCheckOutDate] DATE NOT NULL,
        [RequestNote] NVARCHAR(500) NULL,
        [HasAcknowledgedTerms] BIT NOT NULL
            CONSTRAINT [DF_NightStayBookingModifications_HasAcknowledgedTerms] DEFAULT 0,
        [AcknowledgedPricePerNight] DECIMAL(10, 2) NULL,
        [AcknowledgedCancellationPolicyHours] INT NULL,
        [AcknowledgedDropOffTime] TIME(0) NULL,
        [AcknowledgedPickUpTime] TIME(0) NULL,
        [AcknowledgedAddressLine] NVARCHAR(500) NULL,
        [AcknowledgedCity] NVARCHAR(200) NULL,
        [AcknowledgedZipCode] NVARCHAR(32) NULL,
        [AcknowledgedLatitude] DECIMAL(9, 6) NULL,
        [AcknowledgedLongitude] DECIMAL(9, 6) NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_NightStayBookingModifications_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_NightStayBookingModifications] PRIMARY KEY CLUSTERED ([NightStayBookingModificationId] ASC),
        CONSTRAINT [UQ_NightStayBookingModifications_BookingId] UNIQUE ([NightStayBookingId]),
        CONSTRAINT [FK_NightStayBookingModifications_NightStayBookings]
            FOREIGN KEY ([NightStayBookingId]) REFERENCES [Booking].[NightStayBookings] ([NightStayBookingId]) ON DELETE CASCADE,
        CONSTRAINT [CK_NightStayBookingModifications_RequestedByActor] CHECK ([RequestedByActor] IN (N'Provider', N'Parent')),
        CONSTRAINT [CK_NightStayBookingModifications_DateOrder] CHECK ([ProposedCheckOutDate] > [ProposedCheckInDate]),
        CONSTRAINT [CK_NightStayBookingModifications_AcknowledgedPrice]
            CHECK ([AcknowledgedPricePerNight] IS NULL OR [AcknowledgedPricePerNight] >= 0),
        CONSTRAINT [CK_NightStayBookingModifications_AcknowledgedCancellationPolicy]
            CHECK ([AcknowledgedCancellationPolicyHours] IS NULL
                   OR [AcknowledgedCancellationPolicyHours] IN (24, 48, 72, 96))
    );
    PRINT 'Created table [Booking].[NightStayBookingModifications].';
END
GO

-- Terms-drift acknowledgement (2026-07-27) — night-stay mirror. Adds the
-- offering's drop-off / pick-up times to the acknowledged set, since a stay
-- freezes those at creation too.
IF COL_LENGTH(N'[Booking].[NightStayBookingModifications]', N'HasAcknowledgedTerms') IS NULL
BEGIN
    ALTER TABLE [Booking].[NightStayBookingModifications]
        ADD [HasAcknowledgedTerms] BIT NOT NULL
                CONSTRAINT [DF_NightStayBookingModifications_HasAcknowledgedTerms] DEFAULT 0,
            [AcknowledgedPricePerNight] DECIMAL(10, 2) NULL,
            [AcknowledgedCancellationPolicyHours] INT NULL,
            [AcknowledgedDropOffTime] TIME(0) NULL,
            [AcknowledgedPickUpTime] TIME(0) NULL,
            [AcknowledgedAddressLine] NVARCHAR(500) NULL,
            [AcknowledgedCity] NVARCHAR(200) NULL,
            [AcknowledgedZipCode] NVARCHAR(32) NULL,
            [AcknowledgedLatitude] DECIMAL(9, 6) NULL,
            [AcknowledgedLongitude] DECIMAL(9, 6) NULL;
    PRINT 'Added acknowledged-terms columns to [Booking].[NightStayBookingModifications].';
END
GO
-- Schema-qualified for the same reason as the single-day guard above.
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_NightStayBookingModifications_AcknowledgedPrice'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[NightStayBookingModifications]'))
BEGIN
    ALTER TABLE [Booking].[NightStayBookingModifications] WITH CHECK
        ADD CONSTRAINT [CK_NightStayBookingModifications_AcknowledgedPrice]
            CHECK ([AcknowledgedPricePerNight] IS NULL OR [AcknowledgedPricePerNight] >= 0);
END
GO
IF NOT EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_NightStayBookingModifications_AcknowledgedCancellationPolicy'
      AND [parent_object_id] = OBJECT_ID(N'[Booking].[NightStayBookingModifications]'))
BEGIN
    ALTER TABLE [Booking].[NightStayBookingModifications] WITH CHECK
        ADD CONSTRAINT [CK_NightStayBookingModifications_AcknowledgedCancellationPolicy]
            CHECK ([AcknowledgedCancellationPolicyHours] IS NULL
                   OR [AcknowledgedCancellationPolicyHours] IN (24, 48, 72, 96));
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


-- 2.11a Event.EventPayoutMethods ----------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'EventPayoutMethods' AND [schema_id] = SCHEMA_ID(N'Event'))
BEGIN
    CREATE TABLE [Event].[EventPayoutMethods]
    (
        [EventId] UNIQUEIDENTIFIER NOT NULL,
        [PayoutMethod] NVARCHAR(32) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_EventPayoutMethods_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

        CONSTRAINT [PK_EventPayoutMethods] PRIMARY KEY CLUSTERED ([EventId] ASC, [PayoutMethod] ASC),
        CONSTRAINT [FK_EventPayoutMethods_Events_EventId]
            FOREIGN KEY ([EventId]) REFERENCES [Event].[Events] ([EventId]) ON DELETE CASCADE,
        CONSTRAINT [CK_EventPayoutMethods_PayoutMethod]
            CHECK ([PayoutMethod] IN (N'Cash', N'Digital'))
    );
    PRINT 'Created table [Event].[EventPayoutMethods].';
END
ELSE
BEGIN
    PRINT 'Table [Event].[EventPayoutMethods] already exists.';
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
            CHECK ([PaymentMethod] IN (N'CreditCard', N'Twint', N'Cash', N'Free')),
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

-- Widen the payment-method CHECK to also accept Cash + Free (in addition to
-- the original CreditCard + Twint). Drop-and-re-add so re-runs against an
-- existing database that still carries the 2-value constraint are corrected.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_EventBookings_PaymentMethod'
      AND [parent_object_id] = OBJECT_ID(N'[Event].[EventBookings]'))
    ALTER TABLE [Event].[EventBookings] DROP CONSTRAINT [CK_EventBookings_PaymentMethod];
GO
ALTER TABLE [Event].[EventBookings] WITH CHECK ADD CONSTRAINT [CK_EventBookings_PaymentMethod]
    CHECK ([PaymentMethod] IN (N'CreditCard', N'Twint', N'Cash', N'Free'));
GO
PRINT 'Ensured [CK_EventBookings_PaymentMethod] accepts CreditCard/Twint/Cash/Free.';
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


-- 2.20 Notification.NotificationOutbox -----------------------------------------
-- Transactional outbox for push notifications AND the read-model behind the
-- in-app inbox — one row is both the delivery job and the inbox entry. Written
-- in the same transaction as the domain change that caused it, by all three
-- processes that change a booking's status (provider API, parent API, and the
-- Pawfront.Functions sweep). NotificationDispatchFunction claims batches and
-- sends them to FCM.
--
-- [Title]/[Body]/[Route] are NULL at enqueue time and rendered by the dispatcher
-- from [NotificationType] + [DataJson], so all copy lives in one C# catalog.
-- NO FK to Providers/PetParents — anonymised accounts must keep their history,
-- and [RecipientId] is polymorphic (discriminated by [Audience]) anyway.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'NotificationOutbox' AND [schema_id] = SCHEMA_ID(N'Notification'))
BEGIN
    CREATE TABLE [Notification].[NotificationOutbox]
    (
        [NotificationId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_NotificationOutbox_Id] DEFAULT NEWSEQUENTIALID(),
        [Audience] NVARCHAR(16) NOT NULL,
        [RecipientId] UNIQUEIDENTIFIER NOT NULL,
        [NotificationType] NVARCHAR(64) NOT NULL,
        [Title] NVARCHAR(200) NULL,
        [Body] NVARCHAR(1000) NULL,
        [Route] NVARCHAR(200) NULL,
        [EntityType] NVARCHAR(32) NULL,
        [EntityId] UNIQUEIDENTIFIER NULL,
        [DataJson] NVARCHAR(MAX) NULL,
        [ImageUrl] NVARCHAR(1000) NULL,
        [Status] NVARCHAR(16) NOT NULL
            CONSTRAINT [DF_NotificationOutbox_Status] DEFAULT N'Pending',
        [AttemptCount] INT NOT NULL
            CONSTRAINT [DF_NotificationOutbox_AttemptCount] DEFAULT 0,
        [NextAttemptAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_NotificationOutbox_NextAttemptAtUtc] DEFAULT SYSUTCDATETIME(),
        [LastError] NVARCHAR(2000) NULL,
        [DeliveredCount] INT NOT NULL
            CONSTRAINT [DF_NotificationOutbox_DeliveredCount] DEFAULT 0,
        [DedupeKey] NVARCHAR(200) NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_NotificationOutbox_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_NotificationOutbox_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [SentAtUtc] DATETIME2(7) NULL,
        [ReadAtUtc] DATETIME2(7) NULL,
        CONSTRAINT [PK_NotificationOutbox] PRIMARY KEY CLUSTERED ([NotificationId] ASC),
        CONSTRAINT [CK_NotificationOutbox_Audience]
            CHECK ([Audience] IN (N'Provider', N'PetParent')),
        CONSTRAINT [CK_NotificationOutbox_Status]
            CHECK ([Status] IN (N'Pending', N'Sending', N'Sent', N'NoDevice', N'Failed'))
    );
    PRINT 'Created table [Notification].[NotificationOutbox].';
END
ELSE
BEGIN
    PRINT 'Table [Notification].[NotificationOutbox] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_NotificationOutbox_Dispatch'
      AND [object_id] = OBJECT_ID(N'[Notification].[NotificationOutbox]'))
    CREATE INDEX [IX_NotificationOutbox_Dispatch]
        ON [Notification].[NotificationOutbox] ([Status], [NextAttemptAtUtc])
        INCLUDE ([Audience], [RecipientId], [AttemptCount]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_NotificationOutbox_Inbox'
      AND [object_id] = OBJECT_ID(N'[Notification].[NotificationOutbox]'))
    CREATE INDEX [IX_NotificationOutbox_Inbox]
        ON [Notification].[NotificationOutbox] ([Audience], [RecipientId], [CreatedAtUtc] DESC)
        INCLUDE ([ReadAtUtc], [Title]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_NotificationOutbox_DedupeKey'
      AND [object_id] = OBJECT_ID(N'[Notification].[NotificationOutbox]'))
    CREATE UNIQUE INDEX [UX_NotificationOutbox_DedupeKey]
        ON [Notification].[NotificationOutbox] ([DedupeKey])
        WHERE [DedupeKey] IS NOT NULL;
GO


-- 2.21 Notification table types ------------------------------------------------
-- NOTE: a table type cannot be ALTERed. Changing either shape means dropping and
-- recreating it, which first requires dropping every sproc that references it.
IF NOT EXISTS (
    SELECT 1 FROM sys.types AS t
    INNER JOIN sys.schemas AS s ON t.[schema_id] = s.[schema_id]
    WHERE t.[name] = N'NotificationDeliveryResultList' AND s.[name] = N'Notification')
BEGIN
    CREATE TYPE [Notification].[NotificationDeliveryResultList] AS TABLE
    (
        [NotificationId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        [Status] NVARCHAR(16) NOT NULL,
        [Title] NVARCHAR(200) NULL,
        [Body] NVARCHAR(1000) NULL,
        [Route] NVARCHAR(200) NULL,
        [DeliveredCount] INT NOT NULL,
        [LastError] NVARCHAR(2000) NULL
    );
    PRINT 'Created type [Notification].[NotificationDeliveryResultList].';
END
ELSE
BEGIN
    PRINT 'Type [Notification].[NotificationDeliveryResultList] already exists.';
END
GO

-- Deliberately unkeyed: [FcmToken] is NVARCHAR(2048) (4096 bytes), past the
-- 900-byte clustered key limit a PRIMARY KEY would impose. Duplicates are
-- harmless — the deactivation UPDATE is idempotent.
IF NOT EXISTS (
    SELECT 1 FROM sys.types AS t
    INNER JOIN sys.schemas AS s ON t.[schema_id] = s.[schema_id]
    WHERE t.[name] = N'DeviceTokenList' AND s.[name] = N'Notification')
BEGIN
    CREATE TYPE [Notification].[DeviceTokenList] AS TABLE
    (
        [Audience] NVARCHAR(16) NOT NULL,
        [FcmToken] NVARCHAR(2048) NOT NULL
    );
    PRINT 'Created type [Notification].[DeviceTokenList].';
END
ELSE
BEGIN
    PRINT 'Type [Notification].[DeviceTokenList] already exists.';
END
GO


-- 2.22 Review.BookingReviews ---------------------------------------------------
-- Reviews exchanged between the two parties to a finished booking. ONE table holds
-- both directions, discriminated by [ReviewerType]: 'Parent' is the pet parent
-- reviewing the provider (rating + optional comment + optional photos), 'Provider'
-- is the provider rating the pet parent (rating ONLY — the CHECK below refuses a
-- comment on that direction).
--
-- [BookingType] discriminates which booking table [BookingId] points at, and as with
-- [Booking].[BookingPayments] there is deliberately NO FK — one column cannot
-- reference two tables. [Review].[UpsertBookingReview] is what proves the booking
-- exists, that the caller is a party to it, and that it has reached COMPLETED or PAID.
--
-- Names are NOT denormalised here: the list read joins [Parent].[PetParents] live, so
-- an anonymised account reads "Deleted User" instead of keeping the real name frozen.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'BookingReviews' AND [schema_id] = SCHEMA_ID(N'Review'))
BEGIN
    CREATE TABLE [Review].[BookingReviews]
    (
        [BookingReviewId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_BookingReviews_Id] DEFAULT NEWSEQUENTIALID(),
        [BookingType] NVARCHAR(16) NOT NULL,
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [ReviewerType] NVARCHAR(16) NOT NULL,
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [PetParentId] UNIQUEIDENTIFIER NOT NULL,
        [Rating] TINYINT NOT NULL,
        [Comment] NVARCHAR(1000) NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_BookingReviews_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_BookingReviews_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_BookingReviews] PRIMARY KEY CLUSTERED ([BookingReviewId] ASC),
        -- One review per booking per direction. Resubmitting edits the existing row,
        -- so this is what makes a concurrent double-submit safe.
        CONSTRAINT [UQ_BookingReviews_Booking_Reviewer]
            UNIQUE ([BookingType], [BookingId], [ReviewerType]),
        CONSTRAINT [CK_BookingReviews_BookingType]
            CHECK ([BookingType] IN (N'SingleDay', N'NightStay')),
        CONSTRAINT [CK_BookingReviews_ReviewerType]
            CHECK ([ReviewerType] IN (N'Parent', N'Provider')),
        CONSTRAINT [CK_BookingReviews_Rating]
            CHECK ([Rating] >= 1 AND [Rating] <= 5),
        CONSTRAINT [CK_BookingReviews_ProviderRatingHasNoComment]
            CHECK ([ReviewerType] = N'Parent' OR [Comment] IS NULL)
    );
    PRINT 'Created table [Review].[BookingReviews].';
END
ELSE
BEGIN
    PRINT 'Table [Review].[BookingReviews] already exists.';
END
GO

-- Drives the provider-reviews list + its average/histogram summary. Filtered to the
-- parent direction, which is the only one this read ever wants.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_BookingReviews_Provider_Created'
      AND [object_id] = OBJECT_ID(N'[Review].[BookingReviews]'))
    CREATE INDEX [IX_BookingReviews_Provider_Created]
        ON [Review].[BookingReviews] ([ProviderId], [CreatedAtUtc] DESC)
        INCLUDE ([Rating], [BookingType], [BookingId], [PetParentId])
        WHERE [ReviewerType] = N'Parent';
GO

-- The mirror: a pet parent's aggregate rating, shown on the provider-facing
-- customer card.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_BookingReviews_PetParent'
      AND [object_id] = OBJECT_ID(N'[Review].[BookingReviews]'))
    CREATE INDEX [IX_BookingReviews_PetParent]
        ON [Review].[BookingReviews] ([PetParentId])
        INCLUDE ([Rating])
        WHERE [ReviewerType] = N'Provider';
GO

-- The other direction on the same column: the reviews a parent has WRITTEN, which
-- is what the `review` block on their two "my bookings" lists reads. The index
-- above cannot serve it — that one is filtered to the provider direction — and a
-- parent-authored scan would otherwise fall back to the clustered index.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_BookingReviews_PetParent_Authored'
      AND [object_id] = OBJECT_ID(N'[Review].[BookingReviews]'))
    CREATE INDEX [IX_BookingReviews_PetParent_Authored]
        ON [Review].[BookingReviews] ([PetParentId])
        INCLUDE ([BookingType], [BookingId], [Rating])
        WHERE [ReviewerType] = N'Parent';
GO


-- 2.23 Review.BookingReviewPhotos ----------------------------------------------
-- Photos attached to a pet parent's booking review — one row per uploaded photo,
-- same shape as [Booking].[BookingEvidence]. Blobs live under the [ReviewPhotos]
-- folder keyed by the review id, which is why photos are a SECOND call after the
-- review row exists. Only parent-authored reviews can carry them.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'BookingReviewPhotos' AND [schema_id] = SCHEMA_ID(N'Review'))
BEGIN
    CREATE TABLE [Review].[BookingReviewPhotos]
    (
        [BookingReviewPhotoId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_BookingReviewPhotos_Id] DEFAULT NEWSEQUENTIALID(),
        [BookingReviewId] UNIQUEIDENTIFIER NOT NULL,
        [PhotoUrl] NVARCHAR(1000) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_BookingReviewPhotos_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_BookingReviewPhotos] PRIMARY KEY CLUSTERED ([BookingReviewPhotoId] ASC),
        CONSTRAINT [FK_BookingReviewPhotos_BookingReviews_BookingReviewId]
            FOREIGN KEY ([BookingReviewId]) REFERENCES [Review].[BookingReviews] ([BookingReviewId])
            ON DELETE CASCADE
    );
    PRINT 'Created table [Review].[BookingReviewPhotos].';
END
ELSE
BEGIN
    PRINT 'Table [Review].[BookingReviewPhotos] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_BookingReviewPhotos_Review_Created'
      AND [object_id] = OBJECT_ID(N'[Review].[BookingReviewPhotos]'))
    CREATE INDEX [IX_BookingReviewPhotos_Review_Created]
        ON [Review].[BookingReviewPhotos] ([BookingReviewId], [CreatedAtUtc] ASC)
        INCLUDE ([PhotoUrl]);
GO


--------------------------------------------------------------------------------
-- 2.9 Sequences + functions (must precede the procedures that reference them —
--     unlike table references, a function or sequence named by a procedure is
--     resolved when the procedure is created, not deferred to first execution)
--------------------------------------------------------------------------------

-- Mints the payout reference stamped on a booking when its job completes
-- ([PayoutId], 'PO-000123'). A SEQUENCE rather than a per-table IDENTITY because
-- single-day and night-stay bookings live in separate tables but share ONE payout
-- namespace, exactly as they share the [Booking].[BookingPayments] ledger.
IF NOT EXISTS (SELECT 1 FROM sys.sequences
               WHERE [name] = N'PayoutNumberSequence' AND [schema_id] = SCHEMA_ID(N'Booking'))
BEGIN
    CREATE SEQUENCE [Booking].[PayoutNumberSequence]
        AS BIGINT START WITH 1 INCREMENT BY 1 NO CYCLE CACHE 50;
    PRINT 'Created sequence [Booking].[PayoutNumberSequence].';
END
ELSE
BEGIN
    PRINT 'Sequence [Booking].[PayoutNumberSequence] already exists.';
END
GO

-- Backfill 1: bookings already PAID were settled before payout tracking existed,
-- so their [PayoutStatus] still reads 'Pending' — which is simply wrong. Cash-only
-- means recording the payment settles the payout, so PAID implies 'Paid'.
-- Idempotent: re-runs match nothing.
IF EXISTS (SELECT 1 FROM [Booking].[Bookings]
           WHERE [Status] = N'PAID' AND [PayoutStatus] <> N'Paid')
BEGIN
    UPDATE [Booking].[Bookings]
    SET [PayoutStatus] = N'Paid'
    WHERE [Status] = N'PAID' AND [PayoutStatus] <> N'Paid';
    PRINT 'Backfilled [PayoutStatus] = Paid on already-PAID [Booking].[Bookings].';
END
GO

IF EXISTS (SELECT 1 FROM [Booking].[NightStayBookings]
           WHERE [Status] = N'PAID' AND [PayoutStatus] <> N'Paid')
BEGIN
    UPDATE [Booking].[NightStayBookings]
    SET [PayoutStatus] = N'Paid'
    WHERE [Status] = N'PAID' AND [PayoutStatus] <> N'Paid';
    PRINT 'Backfilled [PayoutStatus] = Paid on already-PAID [Booking].[NightStayBookings].';
END
GO

-- Backfill 2: mint payout references for bookings that completed before the
-- stamping shipped, so the earnings breakdown doesn't show a blank reference
-- against historical rows. Custom walk-ins are deliberately skipped — they are
-- off-platform and never enter the payout pipeline.
--
-- sp_sequence_get_range reserves a contiguous block in ONE call, which is the
-- documented way to allocate sequence values in bulk; NEXT VALUE FOR is not
-- usable per-row inside a set-based UPDATE expression here.
-- Idempotent: guarded on [PayoutId] IS NULL.
DECLARE @PayoutBackfillCount INT =
    (SELECT COUNT(*) FROM [Booking].[Bookings]
     WHERE [PayoutId] IS NULL AND [Source] = N'App' AND [Status] IN (N'COMPLETED', N'PAID'));

IF @PayoutBackfillCount > 0
BEGIN
    DECLARE @FirstValue SQL_VARIANT;
    EXEC sys.sp_sequence_get_range
        @sequence_name = N'[Booking].[PayoutNumberSequence]',
        @range_size = @PayoutBackfillCount,
        @range_first_value = @FirstValue OUTPUT;

    DECLARE @First BIGINT = CONVERT(BIGINT, @FirstValue);

    ;WITH [Numbered] AS
    (
        SELECT [PayoutId],
               [Offset] = ROW_NUMBER() OVER (ORDER BY [JobNumber]) - 1
        FROM [Booking].[Bookings]
        WHERE [PayoutId] IS NULL AND [Source] = N'App' AND [Status] IN (N'COMPLETED', N'PAID')
    )
    UPDATE [Numbered]
    SET [PayoutId] = N'PO-' + FORMAT(@First + [Offset], N'D6');

    PRINT 'Backfilled [PayoutId] on ' + CONVERT(NVARCHAR(20), @PayoutBackfillCount)
        + ' completed [Booking].[Bookings].';
END
GO

DECLARE @NightPayoutBackfillCount INT =
    (SELECT COUNT(*) FROM [Booking].[NightStayBookings]
     WHERE [PayoutId] IS NULL AND [Status] IN (N'COMPLETED', N'PAID'));

IF @NightPayoutBackfillCount > 0
BEGIN
    DECLARE @NightFirstValue SQL_VARIANT;
    EXEC sys.sp_sequence_get_range
        @sequence_name = N'[Booking].[PayoutNumberSequence]',
        @range_size = @NightPayoutBackfillCount,
        @range_first_value = @NightFirstValue OUTPUT;

    DECLARE @NightFirst BIGINT = CONVERT(BIGINT, @NightFirstValue);

    ;WITH [Numbered] AS
    (
        SELECT [PayoutId],
               [Offset] = ROW_NUMBER() OVER (ORDER BY [JobNumber]) - 1
        FROM [Booking].[NightStayBookings]
        WHERE [PayoutId] IS NULL AND [Status] IN (N'COMPLETED', N'PAID')
    )
    UPDATE [Numbered]
    SET [PayoutId] = N'PO-' + FORMAT(@NightFirst + [Offset], N'D6');

    PRINT 'Backfilled [PayoutId] on ' + CONVERT(NVARCHAR(20), @NightPayoutBackfillCount)
        + ' completed [Booking].[NightStayBookings].';
END
GO

-- Backfill 3: bookings that ended as a no-show or expired still read
-- [PayoutStatus] = 'Pending', which claims money is on its way for a job nobody
-- performed. Those three statuses are terminal and produce nothing, so settle
-- them as 'NO_PAYOUT' — the value the transition sprocs now write going forward.
--
-- Scoped to rows still reading 'Pending' so a hand-corrected or (impossibly)
-- 'Paid' row is never clobbered. Idempotent: re-runs match nothing. Runs AFTER
-- the CHECK repair above, which is what makes the value storable.
IF EXISTS (SELECT 1 FROM [Booking].[Bookings]
           WHERE [Status] IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED')
             AND [PayoutStatus] = N'Pending')
BEGIN
    UPDATE [Booking].[Bookings]
    SET [PayoutStatus] = N'NO_PAYOUT'
    WHERE [Status] IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED')
      AND [PayoutStatus] = N'Pending';
    PRINT 'Backfilled [PayoutStatus] = NO_PAYOUT on no-show / expired [Booking].[Bookings].';
END
GO

IF EXISTS (SELECT 1 FROM [Booking].[NightStayBookings]
           WHERE [Status] IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED')
             AND [PayoutStatus] = N'Pending')
BEGIN
    UPDATE [Booking].[NightStayBookings]
    SET [PayoutStatus] = N'NO_PAYOUT'
    WHERE [Status] IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED')
      AND [PayoutStatus] = N'Pending';
    PRINT 'Backfilled [PayoutStatus] = NO_PAYOUT on no-show / expired [Booking].[NightStayBookings].';
END
GO

-- The single definition of "what is this booking worth, and has the money moved".
-- Shared by the provider earnings sprocs and the pet-parent spend/history sprocs,
-- so the two sides can never report different figures for the same booking. See
-- database/Pawfront.Database/Functions/BookingAmounts.sql for the full rationale.
CREATE OR ALTER FUNCTION [Booking].[BookingAmounts]
(
    @ProviderId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @FeePercentage DECIMAL(9, 4)
)
RETURNS TABLE
AS
RETURN
(
    SELECT [BookingType]   = N'SingleDay',
           [BookingId]     = b.[BookingId],
           [ProviderId]    = b.[ProviderId],
           [PetParentId]   = b.[PetParentId],
           [PetId]         = b.[PetId],
           [ServiceDate]   = b.[BookingDate],
           [Status]        = b.[Status],
           [IsEarned]      = CAST(CASE WHEN b.[Status] IN (N'COMPLETED', N'PAID') THEN 1 ELSE 0 END AS BIT),
           [IsPaid]        = CAST(CASE WHEN b.[Status] = N'PAID' THEN 1 ELSE 0 END AS BIT),
           [IsPrivate]     = CAST(CASE WHEN b.[Source] = N'Custom' THEN 1 ELSE 0 END AS BIT),
           [Amount]        = amt.[Amount],
           [Fee]           = CAST(COALESCE(
                                 pay.[PawfrontFee],
                                 CASE
                                     WHEN amt.[Amount] IS NULL THEN NULL
                                     WHEN b.[Source] = N'Custom' THEN 0
                                     ELSE ROUND(amt.[Amount] * @FeePercentage / 100.0, 2)
                                 END) AS DECIMAL(12, 2)),
           [PaidAtUtc]     = pay.[PaidAtUtc],
           [PaymentMethod] = pay.[PaymentMethod]
    FROM [Booking].[Bookings] b
    LEFT JOIN [Booking].[BookingPayments] pay
        ON pay.[BookingType] = N'SingleDay'
       AND pay.[BookingId] = b.[BookingId]
    CROSS APPLY
    (
        SELECT [Amount] = CAST(COALESCE(
            pay.[Amount],
            CASE
                WHEN b.[PricePerHour] IS NULL THEN NULL
                -- A Custom walk-in is ALWAYS rate x hours, whatever the category:
                -- [PricePerHour] there is the hourly rate the provider typed for
                -- that one job, not an offering's flat fee. Mirrors
                -- BookingService.ResolveCustomPricing, which does not branch on
                -- category at all.
                WHEN b.[Source] = N'Custom' THEN
                    ROUND(b.[PricePerHour] * (DATEDIFF(MINUTE, b.[StartTime], b.[EndTime]) / 60.0), 2)
                -- App bookings: only PetSitter DayCare bills per hour; every other
                -- single-day service snapshots a flat fee.
                WHEN b.[ServiceCategory] = N'PetSitter'
                    THEN ROUND(b.[PricePerHour] * (DATEDIFF(MINUTE, b.[StartTime], b.[EndTime]) / 60.0), 2)
                ELSE ROUND(b.[PricePerHour], 2)
            END) AS DECIMAL(12, 2))
    ) amt
    WHERE (@ProviderId IS NULL OR b.[ProviderId] = @ProviderId)
      AND (@PetParentId IS NULL OR b.[PetParentId] = @PetParentId)

    UNION ALL

    -- Night-stay bookings are always App bookings, so [IsPrivate] is constant 0.
    SELECT [BookingType]   = N'NightStay',
           [BookingId]     = n.[NightStayBookingId],
           [ProviderId]    = n.[ProviderId],
           [PetParentId]   = n.[PetParentId],
           [PetId]         = n.[PetId],
           [ServiceDate]   = n.[CheckOutDate],
           [Status]        = n.[Status],
           [IsEarned]      = CAST(CASE WHEN n.[Status] IN (N'COMPLETED', N'PAID') THEN 1 ELSE 0 END AS BIT),
           [IsPaid]        = CAST(CASE WHEN n.[Status] = N'PAID' THEN 1 ELSE 0 END AS BIT),
           [IsPrivate]     = CAST(0 AS BIT),
           [Amount]        = amt.[Amount],
           [Fee]           = CAST(COALESCE(
                                 pay.[PawfrontFee],
                                 CASE
                                     WHEN amt.[Amount] IS NULL THEN NULL
                                     ELSE ROUND(amt.[Amount] * @FeePercentage / 100.0, 2)
                                 END) AS DECIMAL(12, 2)),
           [PaidAtUtc]     = pay.[PaidAtUtc],
           [PaymentMethod] = pay.[PaymentMethod]
    FROM [Booking].[NightStayBookings] n
    LEFT JOIN [Booking].[BookingPayments] pay
        ON pay.[BookingType] = N'NightStay'
       AND pay.[BookingId] = n.[NightStayBookingId]
    CROSS APPLY
    (
        SELECT [Amount] = CAST(COALESCE(
            pay.[Amount],
            CASE
                WHEN n.[PricePerNight] IS NULL THEN NULL
                -- Stayed nights = [CheckInDate, CheckOutDate); checkout day isn't billed.
                ELSE ROUND(n.[PricePerNight] * DATEDIFF(DAY, n.[CheckInDate], n.[CheckOutDate]), 2)
            END) AS DECIMAL(12, 2))
    ) amt
    WHERE (@ProviderId IS NULL OR n.[ProviderId] = @ProviderId)
      AND (@PetParentId IS NULL OR n.[PetParentId] = @PetParentId)
);
GO


-- 2.X Support.Tickets ---------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Tickets' AND [schema_id] = SCHEMA_ID(N'Support'))
BEGIN
-- Support tickets. ONE table holds every kind, discriminated by [TicketType]:
--   'BookingIncident' -> "Report Incident" on a booking. [BookingId] +
--                        [BookingType] name the job; [PetId] is denormalised from
--                        it (see below).
--   'ChatIncident'    -> "Report Chat" on a conversation. [ConversationId] names
--                        the thread, which is then under legal hold.
--   'EventIncident'   -> a problem with an event. [EventId] names it.
--   'AppIssue'        -> something wrong with the app itself. No subject at all.
--
-- The first two are raised BY one party AGAINST the other, and store both party
-- ids. The last two are not: they have a reporter and nobody else, so only the
-- reporter's own party column is set and the other is NULL — which is why both
-- are nullable. Every read path stays a plain equality test, since the reporter's
-- column is populated exactly as it always was.
--
-- An event report deliberately records NO counterparty. The organiser is one join
-- away through [EventId], and an event organised by a pet parent and reported by
-- another pet parent cannot be represented by this table's one-Provider /
-- one-PetParent shape at all. Support resolves the organiser when they work the
-- case; the organiser is not a party to the ticket and never sees it in their own
-- "my tickets" list.
--
-- This row is the INDEX. The narrative — the reporter's comment and the
-- clarification thread, which can grow without bound as support asks and the
-- creator answers — lives in the Cosmos "SupportTickets" container, partitioned
-- by /ticketId. Same SQL-owns-relationships / Cosmos-owns-volume split as
-- [Chat].[Conversations] + the ChatMessages container.
--
-- [Status] lives HERE and not in the document, which is the one thing that split
-- forces. Three rules read it and all three are T-SQL predicates that cannot
-- reach Cosmos: the open-ticket uniqueness below, the account/pet delete
-- refusals, and the chat legal hold in [Chat].[DeleteConversationForParticipant].
--
-- A ticket is raised against a SUBJECT — one booking, one conversation, one event
-- — and NOT against a person. That is what the uniqueness indexes below key on: a
-- parent with five bookings from the same provider can report each of them
-- separately, because each is a different incident with a different account of
-- what happened. Reporting somebody does NOT sever the pair: they stay able to
-- message, book and find each other, and nothing is written to
-- [Block].[BlockedParticipants]. Blocking remains the users' own, separate remedy.
--
-- An 'AppIssue' has no subject and therefore no uniqueness rule at all: each bug
-- report is a different bug, and there is nothing to key a "one open" rule on.
--
-- NO FK to [Provider].[Providers] or [Parent].[PetParents], and none to either
-- booking table — the same posture as [Booking].[BookingPayments] and
-- [Review].[BookingReviews]. One column cannot reference two tables, and an
-- anonymised account must keep its tickets: an open ticket is precisely what
-- stops that account being deleted in the first place.
--
-- Party names are NOT denormalised here. The admin panel joins them live, so a
-- deleted account reads "Deleted User" rather than leaving a real name frozen in
-- a support record.
CREATE TABLE [Support].[Tickets]
(
    [TicketId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_Tickets_TicketId] DEFAULT NEWSEQUENTIALID(),

    -- Friendly reference shown to both parties and to support: TK-000123.
    -- An IDENTITY rather than a SEQUENCE (cf. [Booking].[PayoutNumberSequence]),
    -- because unlike payouts there is exactly ONE table minting these.
    [TicketNumber] INT IDENTITY(1, 1) NOT NULL,

    [TicketType] NVARCHAR(24) NOT NULL,

    -- For the two COUNTERPARTY kinds ('BookingIncident' / 'ChatIncident') BOTH
    -- parties are stored, whichever direction the report runs in, so every read
    -- path — the two "my tickets" lists, the delete guards — is a plain equality
    -- test with no CASE on who raised it.
    --
    -- For 'AppIssue' and 'EventIncident' there IS no counterparty: only the
    -- reporter's own column is set, matching [RaisedByType], and the other is
    -- NULL. Nullable purely for those two; CK_Tickets_SubjectMatchesType below is
    -- what keeps each kind honest about which columns it may populate.
    [ProviderId] UNIQUEIDENTIFIER NULL,
    [PetParentId] UNIQUEIDENTIFIER NULL,
    [RaisedByType] NVARCHAR(16) NOT NULL,

    -- Booking incidents only.
    [BookingType] NVARCHAR(16) NULL,
    [BookingId] UNIQUEIDENTIFIER NULL,
    -- Denormalised from the booking at creation, purely so the pet-delete guard
    -- in [Parent].[DeletePetParentPet] is a single indexed read rather than a
    -- UNION across both booking tables. Safe to copy: a booking's pet is fixed at
    -- creation — a modification changes its schedule, never its animal.
    [PetId] UNIQUEIDENTIFIER NULL,

    -- Chat incidents only.
    [ConversationId] UNIQUEIDENTIFIER NULL,

    -- Event incidents only. No FK, same posture as every other subject column
    -- here — and the event outlives the ticket's usefulness either way.
    [EventId] UNIQUEIDENTIFIER NULL,

    -- How the reporter classified the incident, alongside the free-text account
    -- that goes to the Cosmos narrative: [Category] is what kind of problem it is,
    -- [Reason] the one-line summary of this particular one. Both optional, both
    -- plain strings — the vocabulary is the app's picker, deliberately NOT a CHECK
    -- constraint, so adding a category is a mobile release and not a migration.
    --
    -- [Reason] used to travel onto the severance row in [Block].[BlockedParticipants];
    -- reporting no longer writes there, so it lives on the ticket it describes.
    -- Both are returned on every ticket read and are never shown to the reported
    -- party — only to its two parties' own "my tickets" screens and to support.
    [Category] NVARCHAR(100) NULL,
    [Reason] NVARCHAR(500) NULL,

    [Status] NVARCHAR(48) NOT NULL
        CONSTRAINT [DF_Tickets_Status] DEFAULT N'OPENED',

    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Tickets_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Tickets_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [ClosedAtUtc] DATETIME2(7) NULL,

    CONSTRAINT [PK_Tickets] PRIMARY KEY CLUSTERED ([TicketId] ASC),
    CONSTRAINT [UQ_Tickets_TicketNumber] UNIQUE ([TicketNumber]),

    CONSTRAINT [CK_Tickets_TicketType]
        CHECK ([TicketType] IN (
            N'BookingIncident', N'ChatIncident', N'EventIncident', N'AppIssue')),
    CONSTRAINT [CK_Tickets_RaisedByType]
        CHECK ([RaisedByType] IN (N'Provider', N'PetParent')),
    CONSTRAINT [CK_Tickets_BookingType]
        CHECK ([BookingType] IS NULL OR [BookingType] IN (N'SingleDay', N'NightStay')),
    CONSTRAINT [CK_Tickets_Status]
        CHECK ([Status] IN (
            N'OPENED',
            N'IN_REVIEW',
            N'CLARIFICATION_ASKED_TO_CREATOR',
            N'CLARIFICATION_RECEIVED_FROM_CREATOR',
            N'PENDING_WITH_LEGAL_TEAM',
            N'CLOSED')),

    -- Exactly one subject, matching its type — and, for the two kinds that have
    -- no counterparty, exactly one party column, matching [RaisedByType]. Same
    -- shape as [Event].[Events]'s ProviderId / PetParentId pair: the discriminator
    -- and the columns it governs are kept honest by the constraint rather than by
    -- whichever procedure happened to write the row.
    CONSTRAINT [CK_Tickets_SubjectMatchesType]
        CHECK (
            ([TicketType] = N'BookingIncident'
                AND [BookingId] IS NOT NULL
                AND [BookingType] IS NOT NULL
                AND [ConversationId] IS NULL
                AND [EventId] IS NULL
                AND [ProviderId] IS NOT NULL
                AND [PetParentId] IS NOT NULL)
         OR ([TicketType] = N'ChatIncident'
                AND [ConversationId] IS NOT NULL
                AND [BookingId] IS NULL
                AND [BookingType] IS NULL
                AND [PetId] IS NULL
                AND [EventId] IS NULL
                AND [ProviderId] IS NOT NULL
                AND [PetParentId] IS NOT NULL)
         OR ([TicketType] = N'EventIncident'
                AND [EventId] IS NOT NULL
                AND [BookingId] IS NULL
                AND [BookingType] IS NULL
                AND [PetId] IS NULL
                AND [ConversationId] IS NULL
                AND (([RaisedByType] = N'Provider'
                        AND [ProviderId] IS NOT NULL AND [PetParentId] IS NULL)
                  OR ([RaisedByType] = N'PetParent'
                        AND [PetParentId] IS NOT NULL AND [ProviderId] IS NULL)))
         OR ([TicketType] = N'AppIssue'
                AND [EventId] IS NULL
                AND [BookingId] IS NULL
                AND [BookingType] IS NULL
                AND [PetId] IS NULL
                AND [ConversationId] IS NULL
                AND (([RaisedByType] = N'Provider'
                        AND [ProviderId] IS NOT NULL AND [PetParentId] IS NULL)
                  OR ([RaisedByType] = N'PetParent'
                        AND [PetParentId] IS NOT NULL AND [ProviderId] IS NULL)))),

    -- CLOSED is the only status that stamps a closure time, and it always does.
    CONSTRAINT [CK_Tickets_ClosedAtUtc]
        CHECK (([Status] = N'CLOSED' AND [ClosedAtUtc] IS NOT NULL)
            OR ([Status] <> N'CLOSED' AND [ClosedAtUtc] IS NULL))
);

    PRINT 'Created table [Support].[Tickets].';
END
ELSE
BEGIN
    PRINT 'Table [Support].[Tickets] already exists.';
END
GO

-- The reporter's two classifiers. [Reason] moved here when reporting stopped
-- writing a severance row and [Category] joined it; a database created before
-- either has the table without them.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Support].[Tickets]') AND [name] = N'Category')
BEGIN
    ALTER TABLE [Support].[Tickets] ADD [Category] NVARCHAR(100) NULL;
    PRINT 'Added column [Support].[Tickets].[Category].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Support].[Tickets]') AND [name] = N'Reason')
BEGIN
    ALTER TABLE [Support].[Tickets] ADD [Reason] NVARCHAR(500) NULL;
    PRINT 'Added column [Support].[Tickets].[Reason].';
END
GO

-- The event subject. A database created before 'EventIncident' existed has the
-- table without it.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Support].[Tickets]') AND [name] = N'EventId')
BEGIN
    ALTER TABLE [Support].[Tickets] ADD [EventId] UNIQUEIDENTIFIER NULL;
    PRINT 'Added column [Support].[Tickets].[EventId].';
END
GO

-- The two party columns became NULLable when the reporter-only kinds ('AppIssue',
-- 'EventIncident') landed: those have a reporter and no counterparty, so exactly
-- one of the pair is set. Permitted even though both columns are indexed —
-- NOT NULL -> NULL is the one ALTER COLUMN an index does not block.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Support].[Tickets]')
      AND [name] = N'ProviderId' AND [is_nullable] = 0)
BEGIN
    ALTER TABLE [Support].[Tickets] ALTER COLUMN [ProviderId] UNIQUEIDENTIFIER NULL;
    PRINT 'Made [Support].[Tickets].[ProviderId] nullable.';
END
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Support].[Tickets]')
      AND [name] = N'PetParentId' AND [is_nullable] = 0)
BEGIN
    ALTER TABLE [Support].[Tickets] ALTER COLUMN [PetParentId] UNIQUEIDENTIFIER NULL;
    PRINT 'Made [Support].[Tickets].[PetParentId] nullable.';
END
GO

-- 'EventIncident' and 'AppIssue' joined the vocabulary. A database created
-- against the original two-value CHECK would keep rejecting them, and the
-- IF-NOT-EXISTS guard below is by NAME so it could never repair one. Drop an
-- out-of-date definition first — widening a CHECK cannot fail on existing rows.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Tickets_TicketType'
      AND [parent_object_id] = OBJECT_ID(N'[Support].[Tickets]')
      AND [definition] NOT LIKE N'%AppIssue%')
BEGIN
    ALTER TABLE [Support].[Tickets] DROP CONSTRAINT [CK_Tickets_TicketType];
    PRINT 'Dropped outdated constraint [CK_Tickets_TicketType]; recreating with the four kinds.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Tickets_TicketType')
BEGIN
    ALTER TABLE [Support].[Tickets]
        ADD CONSTRAINT [CK_Tickets_TicketType]
            CHECK ([TicketType] IN (
                N'BookingIncident', N'ChatIncident', N'EventIncident', N'AppIssue'));
    PRINT 'Created constraint [CK_Tickets_TicketType].';
END
GO

-- Same repair for the subject rule, which grew from two branches to four and now
-- also asserts which PARTY columns each kind may populate. Existing rows are
-- booking or chat incidents with both party ids set and a NULL [EventId], so they
-- satisfy the first two branches and the recreate cannot fail on data.
IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE [name] = N'CK_Tickets_SubjectMatchesType'
      AND [parent_object_id] = OBJECT_ID(N'[Support].[Tickets]')
      AND [definition] NOT LIKE N'%EventId%')
BEGIN
    ALTER TABLE [Support].[Tickets] DROP CONSTRAINT [CK_Tickets_SubjectMatchesType];
    PRINT 'Dropped outdated constraint [CK_Tickets_SubjectMatchesType]; recreating with all four kinds.';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE [name] = N'CK_Tickets_SubjectMatchesType')
BEGIN
    ALTER TABLE [Support].[Tickets]
        ADD CONSTRAINT [CK_Tickets_SubjectMatchesType]
            CHECK (
                ([TicketType] = N'BookingIncident'
                    AND [BookingId] IS NOT NULL
                    AND [BookingType] IS NOT NULL
                    AND [ConversationId] IS NULL
                    AND [EventId] IS NULL
                    AND [ProviderId] IS NOT NULL
                    AND [PetParentId] IS NOT NULL)
             OR ([TicketType] = N'ChatIncident'
                    AND [ConversationId] IS NOT NULL
                    AND [BookingId] IS NULL
                    AND [BookingType] IS NULL
                    AND [PetId] IS NULL
                    AND [EventId] IS NULL
                    AND [ProviderId] IS NOT NULL
                    AND [PetParentId] IS NOT NULL)
             OR ([TicketType] = N'EventIncident'
                    AND [EventId] IS NOT NULL
                    AND [BookingId] IS NULL
                    AND [BookingType] IS NULL
                    AND [PetId] IS NULL
                    AND [ConversationId] IS NULL
                    AND (([RaisedByType] = N'Provider'
                            AND [ProviderId] IS NOT NULL AND [PetParentId] IS NULL)
                      OR ([RaisedByType] = N'PetParent'
                            AND [PetParentId] IS NOT NULL AND [ProviderId] IS NULL)))
             OR ([TicketType] = N'AppIssue'
                    AND [EventId] IS NULL
                    AND [BookingId] IS NULL
                    AND [BookingType] IS NULL
                    AND [PetId] IS NULL
                    AND [ConversationId] IS NULL
                    AND (([RaisedByType] = N'Provider'
                            AND [ProviderId] IS NOT NULL AND [PetParentId] IS NULL)
                      OR ([RaisedByType] = N'PetParent'
                            AND [PetParentId] IS NOT NULL AND [ProviderId] IS NULL))));
    PRINT 'Created constraint [CK_Tickets_SubjectMatchesType].';
END
GO

-- Uniqueness moved from the PAIR to the SUBJECT: a ticket is about one booking or
-- one conversation, not about a person, so two bookings with the same provider are
-- two reportable incidents. The old pair-scoped index would refuse the second one,
-- and an [IF NOT EXISTS]-by-name guard could never repair that — the same trap
-- [CK_Providers_Gender] and [UX_Providers_MobileNumber] hit — so it is dropped by
-- name here.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_Tickets_OpenPair' AND [object_id] = OBJECT_ID(N'[Support].[Tickets]'))
BEGIN
    DROP INDEX [UX_Tickets_OpenPair] ON [Support].[Tickets];
    PRINT 'Dropped superseded index [UX_Tickets_OpenPair].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_Tickets_OpenBooking' AND [object_id] = OBJECT_ID(N'[Support].[Tickets]'))
BEGIN
    -- ONE open ticket per BOOKING, in either direction: while a report on a job is
    -- open, that job cannot be reported again. Reporting a DIFFERENT booking with the
    -- same provider is unaffected, which is the whole point — a ticket is about an
    -- incident, and two jobs are two incidents.
    --
    -- Still "either direction": both parties reporting the same job is one incident
    -- seen from two sides, and support works it as one case. The second reporter is
    -- handed the open ticket rather than opening a duplicate.
    --
    -- A filtered UNIQUE index rather than a check in the procedure, so the rule is
    -- race-safe for free — two reports landing together cannot both find "no open
    -- ticket" and both insert. [Support].[CreateTicket] still reads first, but only
    -- so it can answer 409 with the id of the ticket already open rather than
    -- surfacing a constraint violation.
    CREATE UNIQUE INDEX [UX_Tickets_OpenBooking]
        ON [Support].[Tickets] ([BookingType], [BookingId])
        WHERE [BookingId] IS NOT NULL AND [Status] <> N'CLOSED';

    PRINT 'Created index [UX_Tickets_OpenBooking].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Tickets_Provider' AND [object_id] = OBJECT_ID(N'[Support].[Tickets]'))
BEGIN
    -- The provider's "my tickets" list: their whole history, newest activity first,
    -- with the columns the list filters and sorts on covered.
    CREATE INDEX [IX_Tickets_Provider]
        ON [Support].[Tickets] ([ProviderId], [UpdatedAtUtc] DESC)
        INCLUDE ([TicketType], [Status], [CreatedAtUtc], [PetParentId], [RaisedByType]);

    PRINT 'Created index [IX_Tickets_Provider].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Tickets_PetParent' AND [object_id] = OBJECT_ID(N'[Support].[Tickets]'))
BEGIN
    -- The parent's mirror of it.
    CREATE INDEX [IX_Tickets_PetParent]
        ON [Support].[Tickets] ([PetParentId], [UpdatedAtUtc] DESC)
        INCLUDE ([TicketType], [Status], [CreatedAtUtc], [ProviderId], [RaisedByType]);

    PRINT 'Created index [IX_Tickets_PetParent].';
END
GO

-- Superseded by the UNIQUE index below, which has the same key, filter and
-- INCLUDE and therefore serves the legal-hold read identically — it just also
-- enforces one open ticket per conversation.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Tickets_OpenConversation' AND [object_id] = OBJECT_ID(N'[Support].[Tickets]'))
BEGIN
    DROP INDEX [IX_Tickets_OpenConversation] ON [Support].[Tickets];
    PRINT 'Dropped superseded index [IX_Tickets_OpenConversation].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_Tickets_OpenConversation' AND [object_id] = OBJECT_ID(N'[Support].[Tickets]'))
BEGIN
    -- Two jobs in one index.
    --
    --   1. ONE open ticket per CONVERSATION — the chat-incident mirror of
    --      [UX_Tickets_OpenBooking] above. A parent may report one thread and still
    --      report a booking with the same provider; what they cannot do is report the
    --      same thread twice while the first report is open.
    --   2. The legal hold. [Chat].[DeleteConversationForParticipant] and the message
    --      delete both ask "is there an open ticket on this conversation" on every
    --      call, so it must be a point read and not a scan — hence the INCLUDE.
    --
    -- Filtered to open tickets because a closed one neither holds a thread nor blocks
    -- a fresh report.
    CREATE UNIQUE INDEX [UX_Tickets_OpenConversation]
        ON [Support].[Tickets] ([ConversationId])
        INCLUDE ([TicketNumber], [Status])
        WHERE [ConversationId] IS NOT NULL AND [Status] <> N'CLOSED';

    PRINT 'Created index [UX_Tickets_OpenConversation].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Tickets_OpenPet' AND [object_id] = OBJECT_ID(N'[Support].[Tickets]'))
BEGIN
    -- The pet-delete guard, same reasoning as the hold above.
    CREATE INDEX [IX_Tickets_OpenPet]
        ON [Support].[Tickets] ([PetId])
        INCLUDE ([TicketNumber], [Status])
        WHERE [PetId] IS NOT NULL AND [Status] <> N'CLOSED';

    PRINT 'Created index [IX_Tickets_OpenPet].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'UX_Tickets_OpenEventReporter' AND [object_id] = OBJECT_ID(N'[Support].[Tickets]'))
BEGIN
    -- ONE open ticket per EVENT per REPORTER — deliberately NOT per event. An
    -- event has many attendees and each of them reporting it is a separate
    -- account of a separate experience, unlike a booking or a thread, which have
    -- exactly two parties and therefore one incident between them.
    --
    -- The key works because NULLs compare EQUAL for uniqueness: a parent-raised
    -- row is (E, NULL, parentX) and another parent's is (E, NULL, parentY) —
    -- distinct — while the same parent twice collides.
    CREATE UNIQUE INDEX [UX_Tickets_OpenEventReporter]
        ON [Support].[Tickets] ([EventId], [ProviderId], [PetParentId])
        WHERE [EventId] IS NOT NULL AND [Status] <> N'CLOSED';
    PRINT 'Created index [UX_Tickets_OpenEventReporter].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Tickets_OpenRaisedByProvider' AND [object_id] = OBJECT_ID(N'[Support].[Tickets]'))
BEGIN
    -- "Which of MY subjects do I already have an open ticket on?" —
    -- [Support].[ListMyOpenTicketSubjects], read once per request by every
    -- surface that offers a Report button. One index per side, since the
    -- reporter's id lives in a different column depending on which app they are.
    CREATE INDEX [IX_Tickets_OpenRaisedByProvider]
        ON [Support].[Tickets] ([ProviderId])
        INCLUDE ([TicketNumber], [TicketType], [BookingType], [BookingId],
                 [ConversationId], [EventId], [Status])
        WHERE [RaisedByType] = N'Provider' AND [Status] <> N'CLOSED';
    PRINT 'Created index [IX_Tickets_OpenRaisedByProvider].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Tickets_OpenRaisedByPetParent' AND [object_id] = OBJECT_ID(N'[Support].[Tickets]'))
BEGIN
    CREATE INDEX [IX_Tickets_OpenRaisedByPetParent]
        ON [Support].[Tickets] ([PetParentId])
        INCLUDE ([TicketNumber], [TicketType], [BookingType], [BookingId],
                 [ConversationId], [EventId], [Status])
        WHERE [RaisedByType] = N'PetParent' AND [Status] <> N'CLOSED';
    PRINT 'Created index [IX_Tickets_OpenRaisedByPetParent].';
END
GO

-- 2.X Support.TicketPhotos ----------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'TicketPhotos' AND [schema_id] = SCHEMA_ID(N'Support'))
BEGIN
-- Photos attached to a booking incident. One row per uploaded photo — the same
-- shape as [Review].[BookingReviewPhotos] and [Booking].[BookingEvidence].
--
-- The blob upload happens in the app layer under the [IncidentPhotos] folder
-- ("incident-photos/<ticketId>/<guid>.<ext>"), which is why photos are a SECOND
-- call after the ticket row exists: the blob owner id is the ticket's own id. The
-- row here is the source of truth.
--
-- These live in SQL rather than in the Cosmos ticket document, even though the
-- rest of the narrative is over there. The reason is the 5-photo cap: enforcing
-- it needs a count taken under a lock ([Support].[AddTicketPhoto] holds
-- UPDLOCK + HOLDLOCK), and two uploads in flight against a Cosmos document would
-- each read "room for one more". The document holds the words; this holds the
-- countable thing.
--
-- Only booking incidents carry photos. A chat incident needs none — the images
-- already in the thread are the evidence, and the whole conversation is under
-- legal hold — so [Support].[AddTicketPhoto] rejects that type.
--
-- Deleting a photo is deliberately NOT supported: evidence a reporter can retract
-- after support has read it would defeat the point of the hold. This is the one
-- gallery in the product with no delete path.
CREATE TABLE [Support].[TicketPhotos]
(
    [TicketPhotoId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_TicketPhotos_Id] DEFAULT NEWSEQUENTIALID(),
    [TicketId] UNIQUEIDENTIFIER NOT NULL,
    [PhotoUrl] NVARCHAR(1000) NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_TicketPhotos_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_TicketPhotos] PRIMARY KEY CLUSTERED ([TicketPhotoId] ASC),
    CONSTRAINT [FK_TicketPhotos_Tickets_TicketId]
        FOREIGN KEY ([TicketId]) REFERENCES [Support].[Tickets] ([TicketId])
        ON DELETE CASCADE
);

    PRINT 'Created table [Support].[TicketPhotos].';
END
ELSE
BEGIN
    PRINT 'Table [Support].[TicketPhotos] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_TicketPhotos_Ticket_Created' AND [object_id] = OBJECT_ID(N'[Support].[TicketPhotos]'))
BEGIN
    -- Photos are always read for a known ticket (or a page of them), oldest-first.
    CREATE INDEX [IX_TicketPhotos_Ticket_Created]
        ON [Support].[TicketPhotos] ([TicketId], [CreatedAtUtc] ASC)
        INCLUDE ([PhotoUrl]);

    PRINT 'Created index [IX_TicketPhotos_Ticket_Created].';
END
GO

-- [Block].[BlockedParticipants] deliberately gains NOTHING from the support module.
-- Raising a ticket does not sever the pair, so there is no [Source] discriminator
-- and no [TicketId] to add: every block row has one origin (a user tapped Block)
-- and one owner (that user). An earlier draft added both here, ahead of the table
-- itself being created further down this script — it would have failed on a fresh
-- database.
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


-- 3.1b SaveParentAuthIdentity -------------------------------------------------
CREATE OR ALTER PROCEDURE [Parent].[SaveParentAuthIdentity]
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

    DECLARE @ParentAuthIdentityId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @ParentAuthIdentityId = [ParentAuthIdentityId]
    FROM [Parent].[ParentAuthIdentities] WITH (UPDLOCK, HOLDLOCK)
    WHERE [FirebaseUserId] = @FirebaseUserId;

    IF @ParentAuthIdentityId IS NULL
    BEGIN
        INSERT INTO [Parent].[ParentAuthIdentities]
        (
            [FirebaseUserId],
            [FirebaseTenantId],
            [AuthProvider],
            [FirebaseProviderId],
            [Email],
            [IsEmailVerified],
            [DisplayName],
            [FirebasePhoneNumber],
            [PhotoUrl]
        )
        VALUES
        (
            @FirebaseUserId,
            @FirebaseTenantId,
            @AuthProvider,
            @FirebaseProviderId,
            @Email,
            @IsEmailVerified,
            @DisplayName,
            @FirebasePhoneNumber,
            @PhotoUrl
        );

        SELECT @ParentAuthIdentityId = [ParentAuthIdentityId],
               @PetParentId = [PetParentId]
        FROM [Parent].[ParentAuthIdentities]
        WHERE [FirebaseUserId] = @FirebaseUserId;
    END
    ELSE
    BEGIN
        UPDATE [Parent].[ParentAuthIdentities]
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
        WHERE [ParentAuthIdentityId] = @ParentAuthIdentityId;

        SELECT @PetParentId = [PetParentId]
        FROM [Parent].[ParentAuthIdentities]
        WHERE [ParentAuthIdentityId] = @ParentAuthIdentityId;
    END

    IF @FcmToken IS NOT NULL AND LEN(LTRIM(RTRIM(@FcmToken))) > 0
    BEGIN
        IF EXISTS (
            SELECT 1
            FROM [Parent].[ParentDeviceTokens] WITH (UPDLOCK, HOLDLOCK)
            WHERE [FcmToken] = @FcmToken
        )
        BEGIN
            UPDATE [Parent].[ParentDeviceTokens]
            SET [ParentAuthIdentityId] = @ParentAuthIdentityId,
                [PetParentId] = @PetParentId,
                [DeviceId] = @DeviceId,
                [DevicePlatform] = @DevicePlatform,
                [IsActive] = 1,
                [LastSeenAtUtc] = SYSUTCDATETIME(),
                [UpdatedAtUtc] = SYSUTCDATETIME()
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
    END

    SELECT [ParentAuthIdentityId],
           [PetParentId],
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
    FROM [Parent].[ParentAuthIdentities]
    WHERE [FirebaseUserId] = @FirebaseUserId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Parent].[SaveParentAuthIdentity].';
GO


-- 3.1c CompletePetParentProfile -----------------------------------------------
-- The auth identity is resolved server-side from the caller's Firebase
-- user id (sub/user_id claim) rather than trusted from the request body.
-- This closes the gap where a malicious caller could complete a different
-- user's profile by guessing the auth identity id.
--
-- A mobile number can back only ONE account: the explicit pre-check below
-- (THROW 51222 -> 409 MobileNumberAlreadyExists) is the deterministic error the
-- caller sees, and UX_PetParents_MobileNumber is the race-safe backstop.
CREATE OR ALTER PROCEDURE [Parent].[CompletePetParentProfile]
    @FirebaseUserId NVARCHAR(128),
    @FirstName NVARCHAR(100),
    @LastName NVARCHAR(100),
    @Gender NVARCHAR(32),
    @MobileCountryCode NVARCHAR(8),
    @MobileNumber NVARCHAR(32),
    @DateOfBirth DATE,
    @AddressLine NVARCHAR(500),
    @Latitude DECIMAL(9, 6),
    @Longitude DECIMAL(9, 6),
    @ZipCode NVARCHAR(16),
    @City NVARCHAR(100),
    @Description NVARCHAR(2000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ParentAuthIdentityId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @ParentAuthIdentityId = [ParentAuthIdentityId],
           @PetParentId = [PetParentId]
    FROM [Parent].[ParentAuthIdentities] WITH (UPDLOCK, HOLDLOCK)
    WHERE [FirebaseUserId] = @FirebaseUserId;

    IF @ParentAuthIdentityId IS NULL
    BEGIN
        THROW 51200, 'Parent auth identity was not found.', 1;
    END

    IF @PetParentId IS NULL
    BEGIN
        -- UPDLOCK + HOLDLOCK range-locks the (MobileCountryCode, MobileNumber)
        -- key so a concurrent completion of the same number waits here rather
        -- than reading "free" at the same instant.
        IF EXISTS (
            SELECT 1
            FROM [Parent].[PetParents] WITH (UPDLOCK, HOLDLOCK)
            WHERE [MobileCountryCode] = @MobileCountryCode
              AND [MobileNumber] = @MobileNumber)
        BEGIN
            THROW 51222, 'Mobile number is already registered to another account.', 1;
        END

        INSERT INTO [Parent].[PetParents]
        (
            [ParentAuthIdentityId],
            [FirstName],
            [LastName],
            [Gender],
            [MobileCountryCode],
            [MobileNumber],
            [DateOfBirth],
            [AddressLine],
            [Latitude],
            [Longitude],
            [ZipCode],
            [City],
            [Description]
        )
        VALUES
        (
            @ParentAuthIdentityId,
            @FirstName,
            @LastName,
            @Gender,
            @MobileCountryCode,
            @MobileNumber,
            @DateOfBirth,
            @AddressLine,
            @Latitude,
            @Longitude,
            @ZipCode,
            @City,
            @Description
        );

        SELECT @PetParentId = [PetParentId]
        FROM [Parent].[PetParents]
        WHERE [ParentAuthIdentityId] = @ParentAuthIdentityId;

        UPDATE [Parent].[ParentAuthIdentities]
        SET [PetParentId] = @PetParentId,
            [SignUpStatus] = N'ParentProfileCompleted',
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ParentAuthIdentityId] = @ParentAuthIdentityId;

        UPDATE [Parent].[ParentDeviceTokens]
        SET [PetParentId] = @PetParentId,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ParentAuthIdentityId] = @ParentAuthIdentityId;
    END

    SELECT [PetParentId],
           [ParentAuthIdentityId],
           [FirstName],
           [LastName],
           [Gender],
           [MobileCountryCode],
           [MobileNumber],
           [DateOfBirth],
           [AddressLine],
           [Latitude],
           [Longitude],
           [ZipCode],
           [City],
           [Description],
           [ProfilePhotoUrl],
           [MobileVerifiedAtUtc],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[PetParents]
    WHERE [PetParentId] = @PetParentId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Parent].[CompletePetParentProfile].';
GO


-- 3.1d UpdatePetParentProfilePhoto --------------------------------------------
CREATE OR ALTER PROCEDURE [Parent].[UpdatePetParentProfilePhoto]
    @PetParentId UNIQUEIDENTIFIER,
    @ProfilePhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE [Parent].[PetParents]
    SET [ProfilePhotoUrl] = @ProfilePhotoUrl,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [PetParentId] = @PetParentId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51201, 'Pet parent was not found.', 1;
    END

    SELECT [PetParentId],
           [ProfilePhotoUrl],
           [UpdatedAtUtc]
    FROM [Parent].[PetParents]
    WHERE [PetParentId] = @PetParentId;
END;
GO
PRINT 'Created/updated [Parent].[UpdatePetParentProfilePhoto].';
GO


-- 3.1d2 UpdatePetProfilePhoto --------------------------------------------------
-- Sets a pet's single primary/profile photo (distinct from the gallery in
-- [Parent].[PetPhotos]). Mirror of [Parent].[UpdatePetParentProfilePhoto].
CREATE OR ALTER PROCEDURE [Parent].[UpdatePetProfilePhoto]
    @PetId UNIQUEIDENTIFIER,
    @ProfilePhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE [Parent].[Pets]
    SET [ProfilePhotoUrl] = @ProfilePhotoUrl,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [PetId] = @PetId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51220, 'Pet was not found.', 1;
    END

    SELECT [PetId],
           [ProfilePhotoUrl],
           [UpdatedAtUtc]
    FROM [Parent].[Pets]
    WHERE [PetId] = @PetId;
END;
GO
PRINT 'Created/updated [Parent].[UpdatePetProfilePhoto].';
GO


-- 3.1d3 GetPetParentProfile ----------------------------------------------------
-- Read-back of the persisted pet-parent profile, joined with the auth
-- identity so the response carries Email + IsEmailVerified without a
-- second round-trip. Empty result set = parent not found (C# maps to 404).
CREATE OR ALTER PROCEDURE [Parent].[GetPetParentProfile]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT p.[PetParentId],
           p.[FirstName],
           p.[LastName],
           p.[Gender],
           a.[Email],
           a.[IsEmailVerified],
           p.[MobileCountryCode],
           p.[MobileNumber],
           p.[DateOfBirth],
           p.[AddressLine],
           p.[Latitude],
           p.[Longitude],
           p.[ZipCode],
           p.[City],
           p.[Description],
           p.[ProfilePhotoUrl],
           p.[MobileVerifiedAtUtc],
           p.[CreatedAtUtc],
           p.[UpdatedAtUtc]
    FROM [Parent].[PetParents] p
    INNER JOIN [Parent].[ParentAuthIdentities] a
        ON a.[ParentAuthIdentityId] = p.[ParentAuthIdentityId]
    WHERE p.[PetParentId] = @PetParentId;
END;
GO
PRINT 'Created/updated [Parent].[GetPetParentProfile].';
GO


-- 3.1d4 UpdatePetParentProfile --------------------------------------------------
-- Edits the basic-profile subset: name, gender, birth date, address fields,
-- description. Deliberately untouched: mobile number (changes must go back
-- through OTP verification), latitude/longitude (no coordinates accompany
-- an address edit today), profile photo (own endpoint).
-- THROW 51208 = pet parent not found (profile update).
-- THROW 51224 = the account has been deleted; an edit would undo the
--               anonymisation [Parent].[DeletePetParent] applied.
CREATE OR ALTER PROCEDURE [Parent].[UpdatePetParentProfile]
    @PetParentId UNIQUEIDENTIFIER,
    @FirstName NVARCHAR(100),
    @LastName NVARCHAR(100),
    @Gender NVARCHAR(32),
    @DateOfBirth DATE,
    @AddressLine NVARCHAR(500),
    @ZipCode NVARCHAR(16),
    @City NVARCHAR(100),
    @Description NVARCHAR(2000)
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (
        SELECT 1 FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId AND [IsDeleted] = 1)
        THROW 51224, 'This account has been deleted and can no longer be edited.', 1;

    UPDATE [Parent].[PetParents]
    SET [FirstName] = @FirstName,
        [LastName] = @LastName,
        [Gender] = @Gender,
        [DateOfBirth] = @DateOfBirth,
        [AddressLine] = @AddressLine,
        [ZipCode] = @ZipCode,
        [City] = @City,
        [Description] = @Description,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [PetParentId] = @PetParentId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51208, 'Pet parent was not found.', 1;
    END

    SELECT p.[PetParentId],
           p.[FirstName],
           p.[LastName],
           p.[Gender],
           a.[Email],
           a.[IsEmailVerified],
           p.[MobileCountryCode],
           p.[MobileNumber],
           p.[DateOfBirth],
           p.[AddressLine],
           p.[Latitude],
           p.[Longitude],
           p.[ZipCode],
           p.[City],
           p.[Description],
           p.[ProfilePhotoUrl],
           p.[MobileVerifiedAtUtc],
           p.[CreatedAtUtc],
           p.[UpdatedAtUtc]
    FROM [Parent].[PetParents] p
    INNER JOIN [Parent].[ParentAuthIdentities] a
        ON a.[ParentAuthIdentityId] = p.[ParentAuthIdentityId]
    WHERE p.[PetParentId] = @PetParentId;
END;
GO
PRINT 'Created/updated [Parent].[UpdatePetParentProfile].';
GO


-- 3.1d2 GetPetParentByFirebaseUid --------------------------------------------
CREATE OR ALTER PROCEDURE [Parent].[GetPetParentByFirebaseUid]
    @FirebaseUserId NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    -- Resolves a Firebase user id (sub/user_id claim) to the persisted pet-parent
    -- identity, so the mobile app can re-hydrate state after a reinstall (which
    -- wipes local storage). LEFT JOIN to PetParents: the auth identity may exist
    -- without a profile row (mid-onboarding state), in which case PetParentId and
    -- the profile columns come back NULL. Empty result set means no auth identity
    -- exists for this Firebase user.
    SELECT ai.[ParentAuthIdentityId],
           ai.[PetParentId],
           ai.[FirebaseUserId],
           ai.[Email],
           ai.[IsEmailVerified],
           ai.[DisplayName],
           ai.[SignUpStatus],
           CAST(CASE WHEN p.[PetParentId] IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS [HasProfile],
           p.[MobileVerifiedAtUtc]
    FROM [Parent].[ParentAuthIdentities] AS ai
    LEFT JOIN [Parent].[PetParents] AS p
        ON p.[PetParentId] = ai.[PetParentId]
    WHERE ai.[FirebaseUserId] = @FirebaseUserId;
END;
GO
PRINT 'Created/updated [Parent].[GetPetParentByFirebaseUid].';
GO


-- 3.1e AddPetParentPet --------------------------------------------------------
CREATE OR ALTER PROCEDURE [Parent].[AddPetParentPet]
    @PetParentId UNIQUEIDENTIFIER,
    @PetType NVARCHAR(32),
    @PetName NVARCHAR(100),
    @Breed NVARCHAR(100),
    @Gender NVARCHAR(16),
    @DateOfBirth DATE,
    @Weight DECIMAL(5, 2),
    @MicrochipId NVARCHAR(32) = NULL,
    @Description NVARCHAR(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51202, 'Pet parent was not found.', 1;
    END

    DECLARE @InsertedPetId TABLE ([PetId] UNIQUEIDENTIFIER);

    INSERT INTO [Parent].[Pets]
    (
        [PetParentId],
        [PetType],
        [PetName],
        [Breed],
        [Gender],
        [DateOfBirth],
        [Weight],
        [MicrochipId],
        [Description]
    )
    OUTPUT inserted.[PetId] INTO @InsertedPetId
    VALUES
    (
        @PetParentId,
        @PetType,
        @PetName,
        @Breed,
        @Gender,
        @DateOfBirth,
        @Weight,
        @MicrochipId,
        @Description
    );

    DECLARE @PetId UNIQUEIDENTIFIER = (SELECT TOP (1) [PetId] FROM @InsertedPetId);

    SELECT [PetId],
           [PetParentId],
           [PetType],
           [PetName],
           [Breed],
           [Gender],
           [DateOfBirth],
           [Weight],
           [MicrochipId],
           [Description],
           [VaccinationStatus],
           [SterilizationStatus],
           [MedicalHistory],
           [Temperament],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [ProfilePhotoUrl],
           [VaccinationType],
           [VaccinationDose],
           [Prescription]
    FROM [Parent].[Pets]
    WHERE [PetId] = @PetId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Parent].[AddPetParentPet].';
GO


-- 3.1f UpdatePetMedicalInfo ---------------------------------------------------
CREATE OR ALTER PROCEDURE [Parent].[UpdatePetMedicalInfo]
    @PetId UNIQUEIDENTIFIER,
    @VaccinationStatus NVARCHAR(32),
    @SterilizationStatus NVARCHAR(32),
    @MedicalHistory NVARCHAR(MAX) = NULL,
    -- Temperament is optional — null when the parent hasn't set one.
    @Temperament NVARCHAR(32) = NULL,
    -- Free-text optional medical fields — null when not provided.
    @VaccinationType NVARCHAR(100) = NULL,
    @VaccinationDose NVARCHAR(64) = NULL,
    @Prescription NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE [Parent].[Pets]
    SET [VaccinationStatus] = @VaccinationStatus,
        [SterilizationStatus] = @SterilizationStatus,
        [MedicalHistory] = @MedicalHistory,
        [Temperament] = @Temperament,
        [VaccinationType] = @VaccinationType,
        [VaccinationDose] = @VaccinationDose,
        [Prescription] = @Prescription,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [PetId] = @PetId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51203, 'Pet was not found.', 1;
    END

    SELECT [PetId],
           [PetParentId],
           [PetType],
           [PetName],
           [Breed],
           [Gender],
           [DateOfBirth],
           [Weight],
           [MicrochipId],
           [Description],
           [VaccinationStatus],
           [SterilizationStatus],
           [MedicalHistory],
           [Temperament],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [ProfilePhotoUrl],
           [VaccinationType],
           [VaccinationDose],
           [Prescription]
    FROM [Parent].[Pets]
    WHERE [PetId] = @PetId;
END;
GO
PRINT 'Created/updated [Parent].[UpdatePetMedicalInfo].';
GO


-- 3.1g AddPetPhoto ------------------------------------------------------------
CREATE OR ALTER PROCEDURE [Parent].[AddPetPhoto]
    @PetId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[Pets]
        WHERE [PetId] = @PetId
    )
    BEGIN
        THROW 51204, 'Pet was not found.', 1;
    END

    DECLARE @InsertedPetPhotoId TABLE ([PetPhotoId] UNIQUEIDENTIFIER);

    INSERT INTO [Parent].[PetPhotos]
    (
        [PetId],
        [PhotoUrl]
    )
    OUTPUT inserted.[PetPhotoId] INTO @InsertedPetPhotoId
    VALUES
    (
        @PetId,
        @PhotoUrl
    );

    DECLARE @PetPhotoId UNIQUEIDENTIFIER = (SELECT TOP (1) [PetPhotoId] FROM @InsertedPetPhotoId);

    SELECT [PetPhotoId],
           [PetId],
           [PhotoUrl],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[PetPhotos]
    WHERE [PetPhotoId] = @PetPhotoId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Parent].[AddPetPhoto].';
GO


-- 3.1g2 Parent.DeletePetPhoto --------------------------------------------------
-- Removes one photo from a pet's gallery, scoped by BOTH PetId and PetPhotoId.
-- Returns the deleted URL so the API can best-effort delete the blob.
-- THROW 51215 = pet photo not found (pet photo delete).
CREATE OR ALTER PROCEDURE [Parent].[DeletePetPhoto]
    @PetId UNIQUEIDENTIFIER,
    @PetPhotoId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @PhotoUrl NVARCHAR(1000);

    BEGIN TRANSACTION;

    SELECT @PhotoUrl = [PhotoUrl]
    FROM [Parent].[PetPhotos] WITH (UPDLOCK, HOLDLOCK)
    WHERE [PetPhotoId] = @PetPhotoId
      AND [PetId] = @PetId;

    IF @PhotoUrl IS NULL
    BEGIN
        THROW 51215, 'Pet photo was not found.', 1;
    END

    DELETE FROM [Parent].[PetPhotos]
    WHERE [PetPhotoId] = @PetPhotoId;

    SELECT @PetPhotoId AS [PetPhotoId],
           @PetId AS [PetId],
           @PhotoUrl AS [PhotoUrl],
           SYSUTCDATETIME() AS [DeletedAtUtc];

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Parent].[DeletePetPhoto].';
GO


-- 3.1h GetPetParentOnboardingStatus -------------------------------------------
CREATE OR ALTER PROCEDURE [Parent].[GetPetParentOnboardingStatus]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Result set 1: parent profile + joined auth-identity flags. Empty if
    -- the parent doesn't exist — the application reader treats that as a
    -- 404 signal.
    SELECT pp.[PetParentId],
           pp.[ProfilePhotoUrl],
           pp.[MobileVerifiedAtUtc],
           pai.[IsEmailVerified]
    FROM [Parent].[PetParents] AS pp
    INNER JOIN [Parent].[ParentAuthIdentities] AS pai
        ON pai.[ParentAuthIdentityId] = pp.[ParentAuthIdentityId]
    WHERE pp.[PetParentId] = @PetParentId;

    -- Result set 2: pets summary. Each row carries a server-computed flag
    -- for whether the two required medical fields are populated.
    -- MedicalHistory and Temperament are intentionally NOT part of the
    -- completion check — both are optional (Temperament can be left empty).
    SELECT [PetId],
           [PetName],
           CASE
               WHEN [VaccinationStatus]   IS NOT NULL
                AND [SterilizationStatus] IS NOT NULL
               THEN CAST(1 AS BIT)
               ELSE CAST(0 AS BIT)
           END AS [IsMedicalInfoComplete]
    FROM [Parent].[Pets]
    -- Soft-deleted pets don't count towards onboarding: the parent no longer
    -- has them, and their (retained) medical fields would otherwise keep the
    -- pet-medical-info stage looking Complete.
    WHERE [PetParentId] = @PetParentId AND [IsDeleted] = 0
    ORDER BY [CreatedAtUtc] ASC;

    -- Result set 3: identity. Zero rows = no identity uploaded yet (stage
    -- Remaining); one row = uploaded (stage Complete) with the
    -- IdentityType the parent declared.
    SELECT [IdentityType]
    FROM [Parent].[ParentIdentities]
    WHERE [PetParentId] = @PetParentId;
END;
GO
PRINT 'Created/updated [Parent].[GetPetParentOnboardingStatus].';
GO


-- 3.1i Parent.CreateMobileVerificationOtp -------------------------------------
CREATE OR ALTER PROCEDURE [Parent].[CreateMobileVerificationOtp]
    @PetParentId UNIQUEIDENTIFIER,
    @OtpCode NVARCHAR(10)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ParentMobileOtpId UNIQUEIDENTIFIER = NEWID();
    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @MobileCountryCode NVARCHAR(8);
    DECLARE @MobileNumber NVARCHAR(32);

    SELECT @MobileCountryCode = [MobileCountryCode],
           @MobileNumber = [MobileNumber]
    FROM [Parent].[PetParents]
    WHERE [PetParentId] = @PetParentId;

    IF @MobileNumber IS NULL
    BEGIN
        THROW 51210, 'Pet parent profile was not found.', 1;
    END

    INSERT INTO [Parent].[ParentMobileOtps]
    (
        [ParentMobileOtpId],
        [PetParentId],
        [MobileCountryCode],
        [MobileNumber],
        [OtpCodeHash],
        [OtpCodeLastTwo],
        [DateSentUtc],
        [ExpiresAtUtc],
        [CreatedAtUtc],
        [UpdatedAtUtc]
    )
    VALUES
    (
        @ParentMobileOtpId,
        @PetParentId,
        @MobileCountryCode,
        @MobileNumber,
        HASHBYTES('SHA2_256', CONVERT(NVARCHAR(36), @ParentMobileOtpId) + N':' + @OtpCode),
        RIGHT(@OtpCode, 2),
        @Now,
        DATEADD(MINUTE, 10, @Now),
        @Now,
        @Now
    );

    SELECT [ParentMobileOtpId],
           [PetParentId],
           [MobileCountryCode],
           [MobileNumber],
           [DateSentUtc],
           [ExpiresAtUtc]
    FROM [Parent].[ParentMobileOtps]
    WHERE [ParentMobileOtpId] = @ParentMobileOtpId;
END;
GO
PRINT 'Created/updated [Parent].[CreateMobileVerificationOtp].';
GO


-- 3.1j Parent.VerifyMobileVerificationOtp -------------------------------------
CREATE OR ALTER PROCEDURE [Parent].[VerifyMobileVerificationOtp]
    @PetParentId UNIQUEIDENTIFIER,
    @ParentMobileOtpId UNIQUEIDENTIFIER,
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
    FROM [Parent].[ParentMobileOtps] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ParentMobileOtpId] = @ParentMobileOtpId
      AND [PetParentId] = @PetParentId;

    IF @OtpCodeHash IS NULL
    BEGIN
        THROW 51211, 'Pet parent mobile OTP entry was not found.', 1;
    END

    IF @ValidationStatus = N'Validated'
    BEGIN
        SET @IsValidated = 1;
        SET @ResponseStatus = N'Validated';
    END
    ELSE IF @ValidationStatus = N'Expired' OR @Now >= @ExpiresAtUtc
    BEGIN
        UPDATE [Parent].[ParentMobileOtps]
        SET [ValidationStatus] = N'Expired',
            [UpdatedAtUtc] = @Now
        WHERE [ParentMobileOtpId] = @ParentMobileOtpId;

        SET @ResponseStatus = N'Expired';
    END
    ELSE IF @OtpCodeHash = HASHBYTES('SHA2_256', CONVERT(NVARCHAR(36), @ParentMobileOtpId) + N':' + @OtpCode)
    BEGIN
        UPDATE [Parent].[ParentMobileOtps]
        SET [ValidationStatus] = N'Validated',
            [DateValidatedUtc] = @Now,
            [UpdatedAtUtc] = @Now
        WHERE [ParentMobileOtpId] = @ParentMobileOtpId;

        UPDATE [Parent].[PetParents]
        SET [MobileVerifiedAtUtc] = COALESCE([MobileVerifiedAtUtc], @Now),
            [UpdatedAtUtc] = @Now
        WHERE [PetParentId] = @PetParentId;

        SET @IsValidated = 1;
        SET @ResponseStatus = N'Validated';
        SET @DateValidatedUtc = @Now;
    END
    ELSE
    BEGIN
        UPDATE [Parent].[ParentMobileOtps]
        SET [FailedAttemptCount] = [FailedAttemptCount] + 1,
            [UpdatedAtUtc] = @Now
        WHERE [ParentMobileOtpId] = @ParentMobileOtpId;

        SET @ResponseStatus = N'Invalid';
    END

    SELECT [ParentMobileOtpId],
           [PetParentId],
           @IsValidated AS [IsValidated],
           @ResponseStatus AS [ValidationStatus],
           [DateSentUtc],
           [DateValidatedUtc],
           [ExpiresAtUtc]
    FROM [Parent].[ParentMobileOtps]
    WHERE [ParentMobileOtpId] = @ParentMobileOtpId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Parent].[VerifyMobileVerificationOtp].';
GO


-- 3.1k Parent.ListPetParentPets ------------------------------------------------
CREATE OR ALTER PROCEDURE [Parent].[ListPetParentPets]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Result set 1: pets for this parent. Empty when the parent has no
    -- pets (or doesn't exist) — the application returns [] rather than 404,
    -- matching typical REST list semantics.
    SELECT [PetId],
           [PetParentId],
           [PetType],
           [PetName],
           [Breed],
           [Gender],
           [DateOfBirth],
           [Weight],
           [MicrochipId],
           [Description],
           [VaccinationStatus],
           [SterilizationStatus],
           [MedicalHistory],
           [Temperament],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [ProfilePhotoUrl],
           [VaccinationType],
           [VaccinationDose],
           [Prescription]
    FROM [Parent].[Pets]
    -- Soft-deleted pets never appear in the parent's list; the row survives only
    -- so a past booking's petDetails join still resolves.
    WHERE [PetParentId] = @PetParentId AND [IsDeleted] = 0
    ORDER BY [CreatedAtUtc] ASC;

    -- Result set 2: photos for those pets. Grouped by PetId in the C# layer
    -- and nested under each pet in the response. Ordered oldest-first so the
    -- mobile gallery renders in upload order.
    SELECT ph.[PetPhotoId],
           ph.[PetId],
           ph.[PhotoUrl],
           ph.[CreatedAtUtc],
           ph.[UpdatedAtUtc]
    FROM [Parent].[PetPhotos] AS ph
    INNER JOIN [Parent].[Pets] AS p
        ON p.[PetId] = ph.[PetId]
    WHERE p.[PetParentId] = @PetParentId AND p.[IsDeleted] = 0
    ORDER BY ph.[CreatedAtUtc] ASC;

    -- Result set 3: next-consultation dates for those pets, one row per
    -- (pet, provider type). Grouped by PetId in the C# layer.
    SELECT c.[PetId],
           c.[ConsultationType],
           c.[NextConsultationDate]
    FROM [Parent].[PetNextConsultations] AS c
    INNER JOIN [Parent].[Pets] AS p
        ON p.[PetId] = c.[PetId]
    WHERE p.[PetParentId] = @PetParentId AND p.[IsDeleted] = 0
    ORDER BY c.[ConsultationType] ASC;
END;
GO
PRINT 'Created/updated [Parent].[ListPetParentPets].';
GO


-- 3.1k2 Parent.GetPetParentPet -------------------------------------------------
CREATE OR ALTER PROCEDURE [Parent].[GetPetParentPet]
    @PetId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Result set 1: the single pet (zero or one row). The application returns
    -- 404 PetNotFound when this set is empty.
    SELECT [PetId],
           [PetParentId],
           [PetType],
           [PetName],
           [Breed],
           [Gender],
           [DateOfBirth],
           [Weight],
           [MicrochipId],
           [Description],
           [VaccinationStatus],
           [SterilizationStatus],
           [MedicalHistory],
           [Temperament],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [ProfilePhotoUrl],
           [VaccinationType],
           [VaccinationDose],
           [Prescription]
    FROM [Parent].[Pets]
    -- Soft-deleted pets are invisible to both apps' pet screens: the row only
    -- survives so a past booking's petDetails join still resolves.
    WHERE [PetId] = @PetId AND [IsDeleted] = 0;

    -- Result set 2: the pet's photo gallery, oldest-first so the mobile
    -- gallery renders in upload order. Nested under the pet in the response.
    SELECT [PetPhotoId],
           [PetId],
           [PhotoUrl],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[PetPhotos]
    WHERE [PetId] = @PetId
    ORDER BY [CreatedAtUtc] ASC;

    -- Result set 3: the pet's next-consultation dates, one row per provider
    -- type (Groomer | Vet | Trainer). Written by the booking-complete flow.
    SELECT [PetId],
           [ConsultationType],
           [NextConsultationDate]
    FROM [Parent].[PetNextConsultations]
    WHERE [PetId] = @PetId
    ORDER BY [ConsultationType] ASC;
END;
GO
PRINT 'Created/updated [Parent].[GetPetParentPet].';
GO


-- 3.1k3 Parent.UpsertPetNextConsultation ----------------------------------------
-- Records (or replaces) a pet's next-consultation date for one provider type
-- (Groomer | Vet | Trainer). Called by the provider's booking-complete flow —
-- one row per (PetId, ConsultationType). THROW 51221 when the pet is missing.
CREATE OR ALTER PROCEDURE [Parent].[UpsertPetNextConsultation]
    @PetId UNIQUEIDENTIFIER,
    @ConsultationType NVARCHAR(16),
    @NextConsultationDate DATE
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1 FROM [Parent].[Pets] WITH (UPDLOCK, HOLDLOCK)
        WHERE [PetId] = @PetId)
    BEGIN
        THROW 51221, 'Pet was not found.', 1;
    END

    UPDATE [Parent].[PetNextConsultations]
    SET [NextConsultationDate] = @NextConsultationDate,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [PetId] = @PetId
      AND [ConsultationType] = @ConsultationType;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO [Parent].[PetNextConsultations]
            ([PetId], [ConsultationType], [NextConsultationDate])
        VALUES
            (@PetId, @ConsultationType, @NextConsultationDate);
    END

    SELECT [PetNextConsultationId],
           [PetId],
           [ConsultationType],
           [NextConsultationDate],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[PetNextConsultations]
    WHERE [PetId] = @PetId
      AND [ConsultationType] = @ConsultationType;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Parent].[UpsertPetNextConsultation].';
GO


-- 3.1l Parent.UpdatePetParentPet -----------------------------------------------
CREATE OR ALTER PROCEDURE [Parent].[UpdatePetParentPet]
    @PetId UNIQUEIDENTIFIER,
    @PetType NVARCHAR(32),
    @PetName NVARCHAR(100),
    @Breed NVARCHAR(100),
    @Gender NVARCHAR(16),
    @DateOfBirth DATE,
    @Weight DECIMAL(5, 2),
    @MicrochipId NVARCHAR(32) = NULL,
    @Description NVARCHAR(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE [Parent].[Pets]
    SET [PetType] = @PetType,
        [PetName] = @PetName,
        [Breed] = @Breed,
        [Gender] = @Gender,
        [DateOfBirth] = @DateOfBirth,
        [Weight] = @Weight,
        [MicrochipId] = @MicrochipId,
        [Description] = @Description,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [PetId] = @PetId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51205, 'Pet was not found.', 1;
    END

    SELECT [PetId],
           [PetParentId],
           [PetType],
           [PetName],
           [Breed],
           [Gender],
           [DateOfBirth],
           [Weight],
           [MicrochipId],
           [Description],
           [VaccinationStatus],
           [SterilizationStatus],
           [MedicalHistory],
           [Temperament],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [ProfilePhotoUrl],
           [VaccinationType],
           [VaccinationDose],
           [Prescription]
    FROM [Parent].[Pets]
    WHERE [PetId] = @PetId;
END;
GO
PRINT 'Created/updated [Parent].[UpdatePetParentPet].';
GO


-- "Delete pet" is an ANONYMISE + HIDE, not a row delete — the same shape as the
-- two account deletes ([Parent].[DeletePetParent] / [Provider].[DeleteProvider]),
-- and for the same reason: the PetId is referenced by history that is not only
-- the parent's.
--
-- [Booking].[Bookings].[PetId] points at this row and [Booking].[GetBookingDetail]
-- reads the whole petDetails block through that join. The previous version
-- deleted the row and NULLed PetId on every booking that referenced it, which
-- silently blanked the pet out of the PROVIDER's record of a job they actually
-- performed — they were left with a booking whose pet no longer existed.
--
-- What goes: everything that identifies the animal — name, microchip, photo,
-- description, and the free-text medical fields. MicrochipId is cleared rather
-- than replaced because it is a real-world ISO 11784/11785 identifier under a
-- UNIQUE index; clearing it frees the chip so the animal can be registered again.
-- What stays: the facts that keep a past booking meaningful — type, breed,
-- gender, date of birth, weight, and the vaccination / sterilization / temperament
-- statuses.
--
-- [IsDeleted] then hides the pet from every parent-facing read, from the
-- ownership filter behind /pets/{petId}/*, from onboarding status, and from
-- booking creation. Booking reads deliberately do NOT filter on it — that is the
-- history the row is being kept for.
--
-- Photo rows are removed outright (gallery media, no historical value) along with
-- the forward-looking next-consultation reminders. The blobs themselves are left
-- for a future sweep, unchanged from the previous behaviour.
--
-- The scrub is REFUSED while this PET still has unfinished jobs — any booking
-- (single-day or night-stay) naming it that is neither finished nor cancelled: a
-- request the provider hasn't answered, a confirmed job still to come, one
-- underway, or one with an open modification proposal. Mirror of the same rule in
-- [Parent].[DeletePetParent], for the same reason: a provider is holding a slot
-- for this animal, or has it in their care right now, and anonymising it out from
-- under them is not something the parent can undo. The caller is handed the list
-- to settle first (the API answers 409 PendingJobsExist). Deciding it HERE rather
-- than in the app layer is what makes it race-safe — the check runs inside the
-- same transaction that already holds UPDLOCK + HOLDLOCK on the pet row, which is
-- the row a concurrent [Booking].[CreateBooking] must read.
--
-- Idempotent: a second call on an already-deleted pet returns the original
-- DeletedAtUtc with WasAlreadyDeleted = 1 rather than re-scrubbing, and skips the
-- pending-job check (there is nothing left to refuse).
--
-- The scrub is ALSO refused while an open support ticket names this pet — part of
-- the legal hold. The pet is the subject of the job under investigation, so
-- anonymising it out from under support would erase what the ticket is about.
--
-- Returns THREE result sets:
--   1. summary — PetId, PetParentId, DeletedAtUtc, WasAlreadyDeleted, plus
--                BlockedByPendingJobs and BlockedByOpenTickets
--   2. pending jobs — empty unless BlockedByPendingJobs = 1, in which case NOTHING
--      was scrubbed and result set 1 describes an untouched pet.
--   3. open support tickets — empty unless BlockedByOpenTickets = 1, same shape and
--      same ordering the two account deletes return, so one C# reader serves all
--      three.
--
-- THROW 51214 = pet not found (pet delete).
CREATE OR ALTER PROCEDURE [Parent].[DeletePetParentPet]
    @PetId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @Exists BIT = 0;
    DECLARE @IsDeleted BIT;
    DECLARE @DeletedAtUtc DATETIME2(7);
    DECLARE @WasAlreadyDeleted BIT = 0;
    DECLARE @BlockedByPendingJobs BIT = 0;
    DECLARE @BlockedByOpenTickets BIT = 0;

    -- Open support tickets blocking the delete. Same shape and same ordering as
    -- the two account deletes return, so one C# reader serves all three.
    DECLARE @OpenTickets TABLE
    (
        [TicketId] UNIQUEIDENTIFIER NOT NULL,
        [TicketNumber] INT NOT NULL,
        [TicketType] NVARCHAR(24) NOT NULL,
        [RaisedByType] NVARCHAR(16) NOT NULL,
        [Status] NVARCHAR(48) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
    );

    -- Unfinished jobs blocking the delete. Same shape as the account delete's
    -- third result set, so both refusals hand the caller the identical payload —
    -- the last four columns are pricing inputs the caller turns into a money
    -- block, since SQL cannot reach the Cosmos offering.
    DECLARE @PendingJobs TABLE
    (
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [BookingType] NVARCHAR(16) NOT NULL,
        [JobId] NVARCHAR(32) NOT NULL,
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [ProviderName] NVARCHAR(201) NULL,
        [ServiceCategory] NVARCHAR(64) NOT NULL,
        [SubCategory] NVARCHAR(64) NOT NULL,
        [Status] NVARCHAR(48) NOT NULL,
        [ServiceDate] DATE NOT NULL,
        [StartTime] TIME(0) NULL,
        [EndTime] TIME(0) NULL,
        [PetName] NVARCHAR(100) NULL,
        [ServiceId] UNIQUEIDENTIFIER NOT NULL,
        [ServiceItemCode] NVARCHAR(64) NULL,
        [CheckOutDate] DATE NULL,
        [SnapshotUnitPrice] DECIMAL(10, 2) NULL
    );

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK: serialise against a concurrent booking create naming
    -- this pet, so the create either sees a live pet or is rejected by the
    -- IsDeleted guard — never reads "live" while this scrub is committing.
    SELECT @Exists = 1,
           @PetParentId = [PetParentId],
           @IsDeleted = [IsDeleted],
           @DeletedAtUtc = [DeletedAtUtc]
    FROM [Parent].[Pets] WITH (UPDLOCK, HOLDLOCK)
    WHERE [PetId] = @PetId;

    -- XACT_ABORT ON rolls the transaction back on THROW.
    IF @Exists = 0
    BEGIN
        THROW 51214, 'Pet was not found.', 1;
    END

    IF @IsDeleted = 1
    BEGIN
        SET @WasAlreadyDeleted = 1;
        SET @Now = ISNULL(@DeletedAtUtc, @Now);
    END
    ELSE
    BEGIN
        ------------------------------------------------------------------
        -- 0. Refuse while this pet has unfinished jobs.
        --    "Unfinished" is the complement of the terminal set, so a job that
        --    is done (COMPLETED / PAID) or dead (cancelled / declined / no-show
        --    / expired / OTP-cancelled) never blocks. Kept in step with
        --    Pawfront.Application's BookingStatuses.Terminal AND with the same
        --    list in [Parent].[DeletePetParent] — change one, change both.
        --
        --    Note a CREATED booking already past its expiry rule still reads
        --    CREATED until the sweep job flips it (every 5 minutes), so it can
        --    block for that long. Self-correcting, and blocking briefly is the
        --    safe direction.
        ------------------------------------------------------------------
        INSERT INTO @PendingJobs
            ([BookingId], [BookingType], [JobId], [ProviderId], [ProviderName],
             [ServiceCategory], [SubCategory], [Status], [ServiceDate],
             [StartTime], [EndTime], [PetName], [ServiceId], [ServiceItemCode],
             [CheckOutDate], [SnapshotUnitPrice])
        SELECT b.[BookingId],
               N'SingleDay',
               N'PF-' + FORMAT(b.[JobNumber], N'D6'),
               b.[ProviderId],
               NULLIF(LTRIM(RTRIM(ISNULL(pr.[FirstName], N'') + N' ' + ISNULL(pr.[LastName], N''))), N''),
               b.[ServiceCategory],
               b.[SubCategory],
               b.[Status],
               b.[BookingDate],
               b.[StartTime],
               b.[EndTime],
               pet.[PetName],
               b.[ServiceId],
               b.[ServiceItemCode],
               NULL,
               b.[PricePerHour]
        FROM [Booking].[Bookings] AS b
        LEFT JOIN [Provider].[Providers] AS pr ON pr.[ProviderId] = b.[ProviderId]
        LEFT JOIN [Parent].[Pets] AS pet ON pet.[PetId] = b.[PetId]
        WHERE b.[PetId] = @PetId
          AND b.[Status] NOT IN (
                N'COMPLETED', N'PAID', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED',
                N'PARENT_CANCELLED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
        UNION ALL
        SELECT n.[NightStayBookingId],
               N'NightStay',
               N'PF-' + FORMAT(n.[JobNumber], N'D6'),
               n.[ProviderId],
               NULLIF(LTRIM(RTRIM(ISNULL(pr.[FirstName], N'') + N' ' + ISNULL(pr.[LastName], N''))), N''),
               n.[ServiceCategory],
               n.[SubCategory],
               n.[Status],
               n.[CheckInDate],
               n.[DropOffTime],
               n.[PickUpTime],
               pet.[PetName],
               n.[ServiceId],
               NULL,
               n.[CheckOutDate],
               n.[PricePerNight]
        FROM [Booking].[NightStayBookings] AS n
        LEFT JOIN [Provider].[Providers] AS pr ON pr.[ProviderId] = n.[ProviderId]
        LEFT JOIN [Parent].[Pets] AS pet ON pet.[PetId] = n.[PetId]
        WHERE n.[PetId] = @PetId
          AND n.[Status] NOT IN (
                N'COMPLETED', N'PAID', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED',
                N'PARENT_CANCELLED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED');

        IF EXISTS (SELECT 1 FROM @PendingJobs)
        BEGIN
            SET @BlockedByPendingJobs = 1;
        END

        -- An open BOOKING incident naming this pet refuses the delete too — part
        -- of the legal hold. The pet is the subject of the job under
        -- investigation, so anonymising it out from under support would erase
        -- what the ticket is about.
        --
        -- Matched on [Support].[Tickets].[PetId], denormalised from the booking at
        -- ticket creation, so this is one indexed seek rather than a UNION back
        -- across both booking tables. A CHAT incident never carries a PetId and
        -- therefore never holds a pet — it is about what was said, not about an
        -- animal.
        INSERT INTO @OpenTickets
            ([TicketId], [TicketNumber], [TicketType], [RaisedByType], [Status], [CreatedAtUtc])
        SELECT [TicketId], [TicketNumber], [TicketType], [RaisedByType], [Status], [CreatedAtUtc]
        FROM [Support].[Tickets]
        WHERE [PetId] = @PetId
          AND [Status] <> N'CLOSED';

        IF EXISTS (SELECT 1 FROM @OpenTickets)
        BEGIN
            SET @BlockedByOpenTickets = 1;
        END
    END

    -- Only scrub when the pet is live AND unblocked.
    IF @IsDeleted = 0 AND @BlockedByPendingJobs = 0 AND @BlockedByOpenTickets = 0
    BEGIN
        UPDATE [Parent].[Pets]
        SET [PetName] = N'Deleted Pet',
            [MicrochipId] = NULL,
            [Description] = NULL,
            [MedicalHistory] = NULL,
            [VaccinationType] = NULL,
            [VaccinationDose] = NULL,
            [Prescription] = NULL,
            [ProfilePhotoUrl] = NULL,
            [IsDeleted] = 1,
            [DeletedAtUtc] = @Now,
            [UpdatedAtUtc] = @Now
        WHERE [PetId] = @PetId;

        DELETE FROM [Parent].[PetPhotos] WHERE [PetId] = @PetId;
        DELETE FROM [Parent].[PetNextConsultations] WHERE [PetId] = @PetId;
    END

    -- Result set 1: summary. When either Blocked* flag is 1 the pet is UNTOUCHED
    -- and [DeletedAtUtc] carries @Now only to keep the column non-nullable for the
    -- reader; it is never surfaced in that case.
    SELECT @PetId AS [PetId],
           @PetParentId AS [PetParentId],
           @Now AS [DeletedAtUtc],
           @WasAlreadyDeleted AS [WasAlreadyDeleted],
           @BlockedByPendingJobs AS [BlockedByPendingJobs],
           @BlockedByOpenTickets AS [BlockedByOpenTickets];

    -- Result set 2: the unfinished jobs that refused the delete. Empty on the
    -- normal path. Ordered soonest-first — the parent has to deal with the next
    -- one before anything else.
    SELECT [BookingId], [BookingType], [JobId], [ProviderId], [ProviderName],
           [ServiceCategory], [SubCategory], [Status], [ServiceDate],
           [StartTime], [EndTime], [PetName], [ServiceId], [ServiceItemCode],
           [CheckOutDate], [SnapshotUnitPrice]
    FROM @PendingJobs
    ORDER BY [ServiceDate] ASC, [StartTime] ASC;

    -- Result set 3: the open support tickets that refused the delete. Empty on the
    -- normal path. Oldest-first — the one that has been waiting longest is the one
    -- to chase. Same columns and same order as the two account deletes emit.
    SELECT [TicketId], [TicketNumber], [TicketType], [RaisedByType], [Status], [CreatedAtUtc]
    FROM @OpenTickets
    ORDER BY [CreatedAtUtc] ASC;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Parent].[DeletePetParentPet].';
GO


-- 3.1m Parent.UpsertPetParentIdentity ------------------------------------------
CREATE OR ALTER PROCEDURE [Parent].[UpsertPetParentIdentity]
    @PetParentId UNIQUEIDENTIFIER,
    @IdentityType NVARCHAR(32),
    @IdentityPhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51206, 'Pet parent was not found.', 1;
    END

    IF EXISTS (
        SELECT 1
        FROM [Parent].[ParentIdentities] WITH (UPDLOCK, HOLDLOCK)
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        UPDATE [Parent].[ParentIdentities]
        SET [IdentityType] = @IdentityType,
            [IdentityPhotoUrl] = @IdentityPhotoUrl,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [PetParentId] = @PetParentId;
    END
    ELSE
    BEGIN
        INSERT INTO [Parent].[ParentIdentities]
        (
            [PetParentId],
            [IdentityType],
            [IdentityPhotoUrl]
        )
        VALUES
        (
            @PetParentId,
            @IdentityType,
            @IdentityPhotoUrl
        );
    END

    SELECT [ParentIdentityId],
           [PetParentId],
           [IdentityType],
           [IdentityPhotoUrl],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[ParentIdentities]
    WHERE [PetParentId] = @PetParentId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Parent].[UpsertPetParentIdentity].';
GO


-- 3.1m2 Parent.GetPetParentIdentity --------------------------------------------
-- Reads the parent's single identity row (one per parent — UNIQUE PetParentId),
-- including the document's blob URL. Returns zero or one row; the caller maps
-- an empty result to 404 ParentIdentityNotFound.
CREATE OR ALTER PROCEDURE [Parent].[GetPetParentIdentity]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ParentIdentityId],
           [PetParentId],
           [IdentityType],
           [IdentityPhotoUrl],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[ParentIdentities]
    WHERE [PetParentId] = @PetParentId;
END;
GO
PRINT 'Created/updated [Parent].[GetPetParentIdentity].';
GO


-- 3.1n Parent.DeletePetParentIdentity --------------------------------------------
-- Removes the parent's single identity row (one per parent — UNIQUE
-- PetParentId). Returns the deleted row's IdentityType + photo URL so the
-- API can best-effort delete the blob afterwards.
-- THROW 51209 = pet parent identity not found (identity delete).
CREATE OR ALTER PROCEDURE [Parent].[DeletePetParentIdentity]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ParentIdentityId UNIQUEIDENTIFIER;
    DECLARE @IdentityType NVARCHAR(32);
    DECLARE @IdentityPhotoUrl NVARCHAR(1000);

    BEGIN TRANSACTION;

    SELECT @ParentIdentityId = [ParentIdentityId],
           @IdentityType = [IdentityType],
           @IdentityPhotoUrl = [IdentityPhotoUrl]
    FROM [Parent].[ParentIdentities] WITH (UPDLOCK, HOLDLOCK)
    WHERE [PetParentId] = @PetParentId;

    IF @ParentIdentityId IS NULL
    BEGIN
        THROW 51209, 'Pet parent identity was not found.', 1;
    END

    DELETE FROM [Parent].[ParentIdentities]
    WHERE [ParentIdentityId] = @ParentIdentityId;

    SELECT @ParentIdentityId AS [ParentIdentityId],
           @PetParentId AS [PetParentId],
           @IdentityType AS [IdentityType],
           @IdentityPhotoUrl AS [IdentityPhotoUrl],
           SYSUTCDATETIME() AS [DeletedAtUtc];

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Parent].[DeletePetParentIdentity].';
GO


-- 3.1o Parent.AddPetParentPhoto ----------------------------------------------
-- Inserts one row into [Parent].[PetParentPhotos] for a freshly-uploaded photo.
-- THROW 51212 = pet parent not found (parent photo add).
CREATE OR ALTER PROCEDURE [Parent].[AddPetParentPhoto]
    @PetParentId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51212, 'Pet parent was not found.', 1;
    END

    DECLARE @InsertedId TABLE ([PetParentPhotoId] UNIQUEIDENTIFIER);

    INSERT INTO [Parent].[PetParentPhotos]
    (
        [PetParentId],
        [PhotoUrl]
    )
    OUTPUT inserted.[PetParentPhotoId] INTO @InsertedId
    VALUES
    (
        @PetParentId,
        @PhotoUrl
    );

    DECLARE @PetParentPhotoId UNIQUEIDENTIFIER = (SELECT TOP (1) [PetParentPhotoId] FROM @InsertedId);

    SELECT [PetParentPhotoId],
           [PetParentId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Parent].[PetParentPhotos]
    WHERE [PetParentPhotoId] = @PetParentPhotoId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Parent].[AddPetParentPhoto].';
GO


-- 3.1p Parent.ListPetParentPhotos --------------------------------------------
-- Returns every gallery photo on file for the parent, oldest-first. Empty when
-- the parent has no photos (or doesn't exist) — the API returns [] not 404.
CREATE OR ALTER PROCEDURE [Parent].[ListPetParentPhotos]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [PetParentPhotoId],
           [PetParentId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Parent].[PetParentPhotos]
    WHERE [PetParentId] = @PetParentId
    ORDER BY [CreatedAtUtc] ASC;
END;
GO
PRINT 'Created/updated [Parent].[ListPetParentPhotos].';
GO


-- 3.1q Parent.DeletePetParentPhoto -------------------------------------------
-- Removes a single gallery photo, scoped by BOTH PetParentId and
-- PetParentPhotoId. Returns the deleted row's URL for best-effort blob cleanup.
-- THROW 51213 = pet parent photo not found (parent photo delete).
CREATE OR ALTER PROCEDURE [Parent].[DeletePetParentPhoto]
    @PetParentId UNIQUEIDENTIFIER,
    @PetParentPhotoId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @PhotoUrl NVARCHAR(1000);

    BEGIN TRANSACTION;

    SELECT @PhotoUrl = [PhotoUrl]
    FROM [Parent].[PetParentPhotos] WITH (UPDLOCK, HOLDLOCK)
    WHERE [PetParentPhotoId] = @PetParentPhotoId
      AND [PetParentId] = @PetParentId;

    IF @PhotoUrl IS NULL
    BEGIN
        THROW 51213, 'Pet parent photo was not found.', 1;
    END

    DELETE FROM [Parent].[PetParentPhotos]
    WHERE [PetParentPhotoId] = @PetParentPhotoId;

    SELECT @PetParentPhotoId AS [PetParentPhotoId],
           @PetParentId AS [PetParentId],
           @PhotoUrl AS [PhotoUrl],
           SYSUTCDATETIME() AS [DeletedAtUtc];

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Parent].[DeletePetParentPhoto].';
GO


-- Backs the pet-parent app's "Delete account" action as an ANONYMISE + DISABLE,
-- not a row delete. Mirror of [Provider].[DeleteProvider]. The PetParentId is
-- deliberately kept so everything that references it keeps its meaning: service
-- bookings, night-stay bookings, their audit / evidence / OTP / modification /
-- prescription children, the events the parent organised, their event ticket
-- bookings, and the [Booking].[BookingPayments] ledger are ALL retained
-- untouched. Deleting them would destroy BOTH parties' history — a provider's
-- past bookings reference this parent too.
--
-- What the sproc does, in one transaction:
--   1. Scrubs the personal fields on [Parent].[PetParents] — name becomes
--      'Deleted User', gender/DOB/mobile/address/about are replaced or cleared —
--      and sets IsDeleted = 1 + DeletedAtUtc. IsDeleted is permanent and blocks
--      profile edits (THROW 51224), since an edit would undo the anonymisation.
--   2. Scrubs the Firebase link on [Parent].[ParentAuthIdentities] so the account
--      can never be signed into again. FirebaseUserId is UNIQUE, so replacing it
--      also FREES the real Firebase uid — signing up again creates a brand-new
--      identity and a brand-new PetParentId. The mobile number on the profile row
--      is replaced for the same reason: (MobileCountryCode, MobileNumber) is
--      UNIQUE, so the real number becomes available for re-registration.
--   3. Anonymises the parent's PETS rather than deleting them, and marks them
--      IsDeleted (the same flag a per-pet delete sets). Bookings FK to PetId and
--      read the pet's details through that join, so a delete would blank out the
--      provider's own booking history. The pet's identity goes (name, microchip,
--      photo, free-text notes); the animal facts that give a past booking meaning
--      stay (type, breed, gender, DOB, weight, vaccination / sterilization
--      status, temperament). MicrochipId is cleared rather than replaced because
--      it is a real-world identifier and UNIQUE — freeing it lets the animal be
--      registered again under a new account.
--   4. Deletes only what is operational or pure PII and carries no history:
--      device tokens (stop push), mobile OTPs, the identity document, the parent
--      photo gallery, the pets' photo galleries, and the pets' next-consultation
--      reminders (forward-looking, not history).
--
-- The scrub is REFUSED while the parent still has unfinished jobs — any booking
-- (single-day or night-stay) that is neither finished nor cancelled: a request
-- the provider hasn't answered, a confirmed job still to come, one underway, or
-- one with an open modification proposal. Severing the login under a provider
-- who is holding a slot, or who is mid-job, is not something the parent can undo,
-- so the caller is handed the list to settle first (the API answers 409
-- PendingJobsExist). Deciding it HERE rather than in the app layer is what makes
-- it race-safe: the check runs inside the same transaction that already holds
-- UPDLOCK + HOLDLOCK on the parent row, which is the row a concurrent
-- [Booking].[CreateBooking] must read, so a booking landing at the same instant
-- serialises rather than slipping in behind the check.
--
-- All the placeholder values are derived from @PetParentId, so they are stable:
-- re-running on an already-deleted parent is a no-op that returns the original
-- DeletedAtUtc (@WasAlreadyDeleted = 1) instead of churning new values.
--
-- Returns four result sets, since SQL cannot reach Blob Storage:
--   1. summary — PetParentId, DeletedAtUtc, WasAlreadyDeleted, the number of pets
--                anonymised + retained counts (the retained counts document, in
--                the response, that history survived), plus BlockedByPendingJobs
--                and BlockedByOpenTickets
--   2. blob URLs — profile photo, parent gallery, identity document, pet profile
--      photos and pet galleries. Booking evidence and event banners are NOT
--      returned: those belong to records that are being kept.
--   3. pending jobs — empty unless BlockedByPendingJobs = 1, in which case
--      NOTHING was scrubbed and result sets 1 and 2 describe an untouched account.
--   4. open support tickets — empty unless BlockedByOpenTickets = 1, same
--      all-or-nothing meaning. Either flag alone refuses the delete, and both are
--      collected on the same pass so the caller can report everything outstanding
--      at once rather than one blocker at a time.
--
-- THROW 51223 = pet parent not found (account delete).
CREATE OR ALTER PROCEDURE [Parent].[DeletePetParent]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ParentAuthIdentityId UNIQUEIDENTIFIER;
    DECLARE @Exists BIT = 0;
    DECLARE @IsDeleted BIT;
    DECLARE @DeletedAtUtc DATETIME2(7);
    DECLARE @WasAlreadyDeleted BIT = 0;
    DECLARE @AnonymisedPetCount INT = 0;
    DECLARE @BlockedByPendingJobs BIT = 0;
    DECLARE @BlockedByOpenTickets BIT = 0;

    -- Open support tickets blocking the delete. Only the identifying columns: the
    -- narrative lives in the ticket's Cosmos document and the caller is being told
    -- "settle these first", not being shown the case.
    DECLARE @OpenTickets TABLE
    (
        [TicketId] UNIQUEIDENTIFIER NOT NULL,
        [TicketNumber] INT NOT NULL,
        [TicketType] NVARCHAR(24) NOT NULL,
        [RaisedByType] NVARCHAR(16) NOT NULL,
        [Status] NVARCHAR(48) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
    );

    -- Captured before the scrub so the caller can clean Blob Storage.
    DECLARE @BlobUrls TABLE
    (
        [BlobUrl] NVARCHAR(1000) NOT NULL,
        [Kind] NVARCHAR(32) NOT NULL
    );

    -- Unfinished jobs blocking the delete. Populated only when the parent still
    -- has some; the caller surfaces them so they can be cancelled or seen through.
    --
    -- The last four columns are pricing inputs, not display fields: SQL cannot
    -- reach the Cosmos offering, so it hands over the price-locked unit rate it
    -- DOES have (plus what is needed to turn a rate into a total) and the caller
    -- computes the money block — falling back to the live offering for a legacy
    -- row that froze no rate, exactly as the booking-detail read does. The
    -- provider's photo lives in that same Cosmos document and is resolved there
    -- too, which is why there is no photo column here.
    DECLARE @PendingJobs TABLE
    (
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [BookingType] NVARCHAR(16) NOT NULL,
        [JobId] NVARCHAR(32) NOT NULL,
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [ProviderName] NVARCHAR(201) NULL,
        [ServiceCategory] NVARCHAR(64) NOT NULL,
        [SubCategory] NVARCHAR(64) NOT NULL,
        [Status] NVARCHAR(48) NOT NULL,
        [ServiceDate] DATE NOT NULL,
        [StartTime] TIME(0) NULL,
        [EndTime] TIME(0) NULL,
        [PetName] NVARCHAR(100) NULL,
        [ServiceId] UNIQUEIDENTIFIER NOT NULL,
        [ServiceItemCode] NVARCHAR(64) NULL,
        -- Checkout day of a stay (exclusive — not a stayed night); NULL for a
        -- single-day booking, whose duration comes from Start/EndTime instead.
        [CheckOutDate] DATE NULL,
        -- The unit rate frozen onto the booking at creation (per hour, or per
        -- night for a stay). NULL on a legacy row created before price-locking.
        [SnapshotUnitPrice] DECIMAL(10, 2) NULL
    );

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK: serialise against a concurrent booking create or
    -- profile edit on this parent.
    SELECT @Exists = 1,
           @ParentAuthIdentityId = [ParentAuthIdentityId],
           @IsDeleted = [IsDeleted],
           @DeletedAtUtc = [DeletedAtUtc]
    FROM [Parent].[PetParents] WITH (UPDLOCK, HOLDLOCK)
    WHERE [PetParentId] = @PetParentId;

    -- XACT_ABORT ON rolls the transaction back on THROW.
    IF @Exists = 0
    BEGIN
        THROW 51223, 'Pet parent was not found.', 1;
    END

    IF @IsDeleted = 1
    BEGIN
        -- Idempotent: already anonymised. Report the original timestamp and skip
        -- straight to the result sets (nothing left to clean up out of SQL).
        SET @WasAlreadyDeleted = 1;
        SET @Now = ISNULL(@DeletedAtUtc, @Now);
    END
    ELSE
    BEGIN
        ------------------------------------------------------------------
        -- 0. Refuse while unfinished jobs exist.
        --    "Unfinished" is the complement of the terminal set, so a job that
        --    is done (COMPLETED / PAID) or dead (cancelled / declined / no-show
        --    / expired / OTP-cancelled) never blocks — only one that still has
        --    a provider waiting on it or a service still to be delivered.
        --    Both booking kinds count: a boarding stay is as live as an
        --    appointment. Kept in step with Pawfront.Application's
        --    BookingStatuses.Terminal — change one, change the other.
        --
        --    Note a CREATED booking that is already past its expiry rule still
        --    reads CREATED until the sweep job flips it (every 5 minutes), so it
        --    can block for that long. Self-correcting, and blocking briefly is
        --    the safe direction.
        ------------------------------------------------------------------
        INSERT INTO @PendingJobs
            ([BookingId], [BookingType], [JobId], [ProviderId], [ProviderName],
             [ServiceCategory], [SubCategory], [Status], [ServiceDate],
             [StartTime], [EndTime], [PetName], [ServiceId], [ServiceItemCode],
             [CheckOutDate], [SnapshotUnitPrice])
        SELECT b.[BookingId],
               N'SingleDay',
               N'PF-' + FORMAT(b.[JobNumber], N'D6'),
               b.[ProviderId],
               NULLIF(LTRIM(RTRIM(ISNULL(pr.[FirstName], N'') + N' ' + ISNULL(pr.[LastName], N''))), N''),
               b.[ServiceCategory],
               b.[SubCategory],
               b.[Status],
               b.[BookingDate],
               b.[StartTime],
               b.[EndTime],
               pet.[PetName],
               b.[ServiceId],
               b.[ServiceItemCode],
               NULL,
               b.[PricePerHour]
        FROM [Booking].[Bookings] AS b
        LEFT JOIN [Provider].[Providers] AS pr ON pr.[ProviderId] = b.[ProviderId]
        LEFT JOIN [Parent].[Pets] AS pet ON pet.[PetId] = b.[PetId]
        WHERE b.[PetParentId] = @PetParentId
          AND b.[Status] NOT IN (
                N'COMPLETED', N'PAID', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED',
                N'PARENT_CANCELLED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
        UNION ALL
        SELECT n.[NightStayBookingId],
               N'NightStay',
               N'PF-' + FORMAT(n.[JobNumber], N'D6'),
               n.[ProviderId],
               NULLIF(LTRIM(RTRIM(ISNULL(pr.[FirstName], N'') + N' ' + ISNULL(pr.[LastName], N''))), N''),
               n.[ServiceCategory],
               n.[SubCategory],
               n.[Status],
               n.[CheckInDate],
               n.[DropOffTime],
               n.[PickUpTime],
               pet.[PetName],
               n.[ServiceId],
               NULL,
               n.[CheckOutDate],
               n.[PricePerNight]
        FROM [Booking].[NightStayBookings] AS n
        LEFT JOIN [Provider].[Providers] AS pr ON pr.[ProviderId] = n.[ProviderId]
        LEFT JOIN [Parent].[Pets] AS pet ON pet.[PetId] = n.[PetId]
        WHERE n.[PetParentId] = @PetParentId
          AND n.[Status] NOT IN (
                N'COMPLETED', N'PAID', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED',
                N'PARENT_CANCELLED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED');

        IF EXISTS (SELECT 1 FROM @PendingJobs)
        BEGIN
            SET @BlockedByPendingJobs = 1;
        END

        -- An open support ticket refuses the delete too — part of the legal hold.
        -- Anonymising a party mid-investigation destroys the account support is
        -- still asking questions of, and unlike the pending-job refusal there is
        -- nothing the parent can do to clear it themselves: it lifts when the
        -- ticket closes.
        --
        -- Collected even when jobs already block, so the response names EVERYTHING
        -- standing in the way. Discovering the ticket only after cancelling every
        -- booking would be a second dead end.
        INSERT INTO @OpenTickets
            ([TicketId], [TicketNumber], [TicketType], [RaisedByType], [Status], [CreatedAtUtc])
        SELECT [TicketId], [TicketNumber], [TicketType], [RaisedByType], [Status], [CreatedAtUtc]
        FROM [Support].[Tickets]
        WHERE [PetParentId] = @PetParentId
          AND [Status] <> N'CLOSED'
          -- Only the two kinds with a COUNTERPARTY block a delete. The guard
          -- exists so a party cannot be anonymised while support is still asking
          -- questions of them about somebody else; an app issue or an event
          -- report is neither about the other party nor clearable by the user,
          -- so leaving them in would strand an account delete behind an
          -- unrelated bug report.
          AND [TicketType] IN (N'BookingIncident', N'ChatIncident');

        IF EXISTS (SELECT 1 FROM @OpenTickets)
        BEGIN
            SET @BlockedByOpenTickets = 1;
        END
    END

    -- Everything below only runs when the account is live AND unblocked.
    IF @IsDeleted = 0 AND @BlockedByPendingJobs = 0 AND @BlockedByOpenTickets = 0
    BEGIN
        INSERT INTO @BlobUrls ([BlobUrl], [Kind])
        SELECT [ProfilePhotoUrl], N'ParentProfilePhoto'
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId AND [ProfilePhotoUrl] IS NOT NULL
        UNION ALL
        SELECT [PhotoUrl], N'ParentPhoto'
        FROM [Parent].[PetParentPhotos]
        WHERE [PetParentId] = @PetParentId
        UNION ALL
        SELECT [IdentityPhotoUrl], N'ParentIdentity'
        FROM [Parent].[ParentIdentities]
        WHERE [PetParentId] = @PetParentId
        UNION ALL
        SELECT p.[ProfilePhotoUrl], N'PetProfilePhoto'
        FROM [Parent].[Pets] p
        WHERE p.[PetParentId] = @PetParentId AND p.[ProfilePhotoUrl] IS NOT NULL
        UNION ALL
        SELECT ph.[PhotoUrl], N'PetPhoto'
        FROM [Parent].[PetPhotos] ph
        INNER JOIN [Parent].[Pets] p ON p.[PetId] = ph.[PetId]
        WHERE p.[PetParentId] = @PetParentId;

        ------------------------------------------------------------------
        -- 1. Anonymise the profile row and disable the account.
        --    The mobile placeholder is derived from the PetParentId so it stays
        --    unique under UX_PetParents_MobileNumber, and frees the real number.
        ------------------------------------------------------------------
        UPDATE [Parent].[PetParents]
        SET [FirstName] = N'Deleted',
            [LastName] = N'User',
            [Gender] = N'PreferNotToSay',
            [DateOfBirth] = '1900-01-01',
            [MobileCountryCode] = N'+00',
            [MobileNumber] = N'DEL' + LEFT(REPLACE(CONVERT(NVARCHAR(36), @PetParentId), N'-', N''), 29),
            [MobileVerifiedAtUtc] = NULL,
            [AddressLine] = N'Deleted',
            [Latitude] = 0,
            [Longitude] = 0,
            [ZipCode] = N'00000',
            [City] = N'Deleted',
            [Description] = N'',
            [ProfilePhotoUrl] = NULL,
            [IsDeleted] = 1,
            [DeletedAtUtc] = @Now,
            [UpdatedAtUtc] = @Now
        WHERE [PetParentId] = @PetParentId;

        ------------------------------------------------------------------
        -- 2. Sever the Firebase login. FirebaseUserId is UNIQUE, so replacing it
        --    frees the real uid for a fresh sign-up. The PetParentId link is kept
        --    for audit — the row is unreachable by any real Firebase user now.
        ------------------------------------------------------------------
        UPDATE [Parent].[ParentAuthIdentities]
        SET [FirebaseUserId] = N'deleted:' + CONVERT(NVARCHAR(36), @PetParentId),
            [FirebaseTenantId] = NULL,
            [Email] = N'deleted+' + CONVERT(NVARCHAR(36), @PetParentId) + N'@deleted.invalid',
            [IsEmailVerified] = 0,
            [DisplayName] = NULL,
            [FirebasePhoneNumber] = NULL,
            [PhotoUrl] = NULL,
            [UpdatedAtUtc] = @Now
        WHERE [ParentAuthIdentityId] = @ParentAuthIdentityId;

        ------------------------------------------------------------------
        -- 3. Anonymise the pets. The rows stay — bookings FK to PetId and read
        --    the pet through that join — but nothing identifying survives.
        ------------------------------------------------------------------
        UPDATE [Parent].[Pets]
        SET [PetName] = N'Deleted Pet',
            [MicrochipId] = NULL,
            [Description] = NULL,
            [MedicalHistory] = NULL,
            [VaccinationType] = NULL,
            [VaccinationDose] = NULL,
            [Prescription] = NULL,
            [ProfilePhotoUrl] = NULL,
            -- The pets go with the account, so they carry the same flag a
            -- per-pet delete sets. COALESCE keeps the original timestamp on a
            -- pet the parent had already deleted individually.
            [IsDeleted] = 1,
            [DeletedAtUtc] = COALESCE([DeletedAtUtc], @Now),
            [UpdatedAtUtc] = @Now
        WHERE [PetParentId] = @PetParentId;
        SET @AnonymisedPetCount = @@ROWCOUNT;

        ------------------------------------------------------------------
        -- 4. Remove operational data + media only. Nothing here carries
        --    historical meaning: each booking already froze the parent details
        --    and address it was created with.
        ------------------------------------------------------------------
        DELETE FROM [Parent].[ParentDeviceTokens]
        WHERE [PetParentId] = @PetParentId
           OR [ParentAuthIdentityId] = @ParentAuthIdentityId;

        DELETE FROM [Parent].[ParentMobileOtps] WHERE [PetParentId] = @PetParentId;
        DELETE FROM [Parent].[ParentIdentities] WHERE [PetParentId] = @PetParentId;
        DELETE FROM [Parent].[PetParentPhotos] WHERE [PetParentId] = @PetParentId;

        DELETE ph
        FROM [Parent].[PetPhotos] ph
        INNER JOIN [Parent].[Pets] p ON p.[PetId] = ph.[PetId]
        WHERE p.[PetParentId] = @PetParentId;

        DELETE nc
        FROM [Parent].[PetNextConsultations] nc
        INNER JOIN [Parent].[Pets] p ON p.[PetId] = nc.[PetId]
        WHERE p.[PetParentId] = @PetParentId;
    END

    -- Result set 1: summary. The retained counts are reported so the caller can
    -- see that history survived the delete. When BlockedByPendingJobs = 1 the
    -- account is UNTOUCHED and every other column here is meaningless — the
    -- caller reads that flag first and goes to result set 3. [DeletedAtUtc]
    -- still carries @Now rather than NULL only to keep the column non-nullable
    -- for the reader; it is never surfaced in that case.
    SELECT @PetParentId AS [PetParentId],
           @Now AS [DeletedAtUtc],
           @WasAlreadyDeleted AS [WasAlreadyDeleted],
           @BlockedByPendingJobs AS [BlockedByPendingJobs],
           @AnonymisedPetCount AS [AnonymisedPetCount],
           (SELECT COUNT(*) FROM [Booking].[Bookings] WHERE [PetParentId] = @PetParentId)
               AS [RetainedBookingCount],
           (SELECT COUNT(*) FROM [Booking].[NightStayBookings] WHERE [PetParentId] = @PetParentId)
               AS [RetainedNightStayBookingCount],
           (SELECT COUNT(*) FROM [Event].[Events] WHERE [PetParentId] = @PetParentId)
               AS [RetainedEventCount],
           (SELECT COUNT(*) FROM [Booking].[BookingPayments] WHERE [PetParentId] = @PetParentId)
               AS [RetainedPaymentCount],
           -- Appended LAST on purpose: inserting it next to BlockedByPendingJobs,
           -- where it reads better, would shift every retained-count ordinal and
           -- break a reader deployed against the older procedure.
           @BlockedByOpenTickets AS [BlockedByOpenTickets];

    -- Result set 2: blob URLs to delete best-effort.
    SELECT [BlobUrl], [Kind] FROM @BlobUrls;

    -- Result set 3: the unfinished jobs that refused the delete. Empty on the
    -- normal path. Ordered soonest-first — the parent has to deal with the next
    -- one before anything else.
    SELECT [BookingId], [BookingType], [JobId], [ProviderId], [ProviderName],
           [ServiceCategory], [SubCategory], [Status], [ServiceDate],
           [StartTime], [EndTime], [PetName], [ServiceId], [ServiceItemCode],
           [CheckOutDate], [SnapshotUnitPrice]
    FROM @PendingJobs
    ORDER BY [ServiceDate] ASC, [StartTime] ASC;

    -- Result set 4: the open support tickets that refused the delete. Empty on the
    -- normal path. Oldest-first — the one that has been waiting longest is the one
    -- to chase.
    SELECT [TicketId], [TicketNumber], [TicketType], [RaisedByType], [Status], [CreatedAtUtc]
    FROM @OpenTickets
    ORDER BY [CreatedAtUtc] ASC;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Parent].[DeletePetParent].';
GO


-- 3.1r Provider.AddProviderPhoto ---------------------------------------------
-- Inserts one row into [Provider].[ProviderPhotos] for a freshly-uploaded photo.
-- THROW 51110 = provider not found (provider photo add).
CREATE OR ALTER PROCEDURE [Provider].[AddProviderPhoto]
    @ProviderId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000)
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
        THROW 51110, 'Provider was not found.', 1;
    END

    DECLARE @InsertedId TABLE ([ProviderPhotoId] UNIQUEIDENTIFIER);

    INSERT INTO [Provider].[ProviderPhotos]
    (
        [ProviderId],
        [PhotoUrl]
    )
    OUTPUT inserted.[ProviderPhotoId] INTO @InsertedId
    VALUES
    (
        @ProviderId,
        @PhotoUrl
    );

    DECLARE @ProviderPhotoId UNIQUEIDENTIFIER = (SELECT TOP (1) [ProviderPhotoId] FROM @InsertedId);

    SELECT [ProviderPhotoId],
           [ProviderId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Provider].[ProviderPhotos]
    WHERE [ProviderPhotoId] = @ProviderPhotoId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[AddProviderPhoto].';
GO


-- 3.1s Provider.ListProviderPhotos -------------------------------------------
-- Returns every gallery photo on file for the provider, oldest-first. Empty
-- when the provider has no photos (or doesn't exist) — the API returns [] not 404.
CREATE OR ALTER PROCEDURE [Provider].[ListProviderPhotos]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ProviderPhotoId],
           [ProviderId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Provider].[ProviderPhotos]
    WHERE [ProviderId] = @ProviderId
    ORDER BY [CreatedAtUtc] ASC;
END;
GO
PRINT 'Created/updated [Provider].[ListProviderPhotos].';
GO


-- 3.1t Provider.DeleteProviderPhoto ------------------------------------------
-- Removes a single gallery photo, scoped by BOTH ProviderId and
-- ProviderPhotoId. Returns the deleted row's URL for best-effort blob cleanup.
-- THROW 51111 = provider photo not found (provider photo delete).
CREATE OR ALTER PROCEDURE [Provider].[DeleteProviderPhoto]
    @ProviderId UNIQUEIDENTIFIER,
    @ProviderPhotoId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @PhotoUrl NVARCHAR(1000);

    BEGIN TRANSACTION;

    SELECT @PhotoUrl = [PhotoUrl]
    FROM [Provider].[ProviderPhotos] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderPhotoId] = @ProviderPhotoId
      AND [ProviderId] = @ProviderId;

    IF @PhotoUrl IS NULL
    BEGIN
        THROW 51111, 'Provider photo was not found.', 1;
    END

    DELETE FROM [Provider].[ProviderPhotos]
    WHERE [ProviderPhotoId] = @ProviderPhotoId;

    SELECT @ProviderPhotoId AS [ProviderPhotoId],
           @ProviderId AS [ProviderId],
           @PhotoUrl AS [PhotoUrl],
           SYSUTCDATETIME() AS [DeletedAtUtc];

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[DeleteProviderPhoto].';
GO


-- 3.1b Provider.ProviderServiceBanners sprocs ---------------------------------
-- Per-service banner image: upsert (validates the service belongs to the
-- provider + is active, THROW 51081 otherwise) + read.
CREATE OR ALTER PROCEDURE [Provider].[SaveProviderServiceBanner]
    @ServiceId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @BannerImageUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[ProviderServices] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [ProviderId] = @ProviderId
          AND [IsActive] = 1
    )
        THROW 51081, 'Service is not valid or active for this provider.', 1;

    UPDATE [Provider].[ProviderServiceBanners]
    SET [BannerImageUrl] = @BannerImageUrl,
        [ProviderId] = @ProviderId,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [ServiceId] = @ServiceId;

    IF @@ROWCOUNT = 0
        INSERT INTO [Provider].[ProviderServiceBanners]
            ([ServiceId], [ProviderId], [BannerImageUrl])
        VALUES (@ServiceId, @ProviderId, @BannerImageUrl);

    SELECT [ServiceId], [ProviderId], [BannerImageUrl], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Provider].[ProviderServiceBanners]
    WHERE [ServiceId] = @ServiceId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[SaveProviderServiceBanner].';
GO

CREATE OR ALTER PROCEDURE [Provider].[GetProviderServiceBanner]
    @ServiceId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ServiceId], [ProviderId], [BannerImageUrl], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Provider].[ProviderServiceBanners]
    WHERE [ServiceId] = @ServiceId;
END;
GO
PRINT 'Created/updated [Provider].[GetProviderServiceBanner].';
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
           [UpdatedAtUtc],
           [BannerImageUrl]
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
           [UpdatedAtUtc],
           [BannerImageUrl]
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;
END;
GO
PRINT 'Created/updated [Provider].[GetProviderProfile].';
GO


-- 3.2b-ii UpdateProviderProfile -----------------------------------------------
-- Edits the provider's personal details (first name, last name, gender, date of
-- birth). Mobile number + country code are deliberately not editable here — a
-- change must go back through OTP verification, and the pair is UNIQUE.
-- Mirror of [Parent].[UpdatePetParentProfile]. THROW 51113 = provider not found;
-- THROW 51115 = the account has been deleted (an edit would undo the
-- anonymisation done by [Provider].[DeleteProvider]).
CREATE OR ALTER PROCEDURE [Provider].[UpdateProviderProfile]
    @ProviderId UNIQUEIDENTIFIER,
    @FirstName NVARCHAR(100),
    @LastName NVARCHAR(100),
    @Gender NVARCHAR(32),
    @DateOfBirth DATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1 FROM [Provider].[Providers]
               WHERE [ProviderId] = @ProviderId AND [IsDeleted] = 1)
    BEGIN
        THROW 51115, 'This provider account has been deleted.', 1;
    END

    UPDATE [Provider].[Providers]
    SET [FirstName] = @FirstName,
        [LastName] = @LastName,
        [Gender] = @Gender,
        [DateOfBirth] = @DateOfBirth,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [ProviderId] = @ProviderId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51113, 'Provider profile was not found.', 1;
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
           [UpdatedAtUtc],
           [BannerImageUrl]
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;
END;
GO
PRINT 'Created/updated [Provider].[UpdateProviderProfile].';
GO


-- Backs the provider app's "Delete account" action as an ANONYMISE + DISABLE,
-- not a row delete. The ProviderId is deliberately kept so everything that
-- references it keeps its meaning: bookings, night-stay bookings, their audit /
-- evidence / OTP / modification / prescription children, the events the provider
-- organised with their ticket bookings, and the [Booking].[BookingPayments]
-- ledger are ALL retained untouched. Deleting them would destroy both parties'
-- history — a pet parent's past bookings reference this provider too.
--
-- What the sproc does, in one transaction:
--   1. Scrubs the personal fields on [Provider].[Providers] — name becomes
--      'Deleted Provider', gender/DOB/mobile/banner are replaced or cleared —
--      and sets IsActive = 0 + IsDeleted = 1 + DeletedAtUtc. IsActive = 0 is
--      what stops new bookings ([Booking].[CreateBooking] THROWs 51067 ->
--      409 ProviderInactive); IsDeleted = 1 is permanent and additionally blocks
--      reactivation and profile edits (THROW 51115).
--   2. Scrubs the Firebase link on [Provider].[ProviderAuthIdentities] so the
--      account can never be signed into again. FirebaseUserId is UNIQUE, so
--      replacing it also FREES the real Firebase uid — signing up again creates a
--      brand-new identity and a brand-new ProviderId. The mobile number is
--      replaced for the same reason: (MobileCountryCode, MobileNumber) is UNIQUE,
--      so the real number becomes available for re-registration.
--   3. Deactivates the provider's [Provider].[ProviderServices] rows. The rows
--      themselves must stay (bookings FK to ServiceId), but IsActive = 0 removes
--      them from the catalog and therefore from all five parent-facing booking
--      searches, which require an ACTIVE service row.
--   4. Deletes only what is operational or pure PII and carries no history:
--      device tokens (stop push), mobile OTPs, gallery photos, per-service
--      banners, weekly availability, closures, cancellation policy, payout
--      methods, and the service registration (the "I offer this" declaration,
--      and the row that makes the provider readable at
--      GET /providers/{providerId}). The booking-level cancellation policy is
--      unaffected — it was snapshotted onto each booking at creation.
--
-- All the placeholder values are derived from @ProviderId, so they are stable:
-- re-running on an already-deleted provider is a no-op that returns the original
-- DeletedAtUtc (@WasAlreadyDeleted = 1) instead of churning new placeholders.
--
-- Returns four result sets, since SQL cannot reach Cosmos or Blob Storage:
--   1. summary — ProviderId, DeletedAtUtc, WasAlreadyDeleted + deactivated /
--                retained counts (the retained counts document, in the response,
--                that history survived), plus BlockedByOpenTickets
--   2. service categories — partition key(s) of the Cosmos [ProviderServices]
--      offering doc to remove. That document is the provider's public service
--      LISTING (business name, prices, photos, address) and is what makes them
--      appear in Cosmos-backed discovery, so it must not outlive the account.
--      The Cosmos [Events] docs are NOT returned — the events are retained, so
--      their venue/capacity extension docs stay.
--   3. blob URLs — provider banner, gallery photos, per-service banners. Event
--      banners and booking evidence are NOT returned: those belong to records
--      that are being kept.
--   4. open support tickets — empty on the normal path. When non-empty NOTHING
--      was scrubbed: an open ticket refuses the delete as part of the legal hold,
--      and result sets 1-3 describe an untouched account.
--
-- THROW 51114 = provider profile not found (account delete).
CREATE OR ALTER PROCEDURE [Provider].[DeleteProvider]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ProviderAuthIdentityId UNIQUEIDENTIFIER;
    DECLARE @IsDeleted BIT;
    DECLARE @DeletedAtUtc DATETIME2(7);
    DECLARE @WasAlreadyDeleted BIT = 0;
    DECLARE @BlockedByOpenTickets BIT = 0;

    -- Open support tickets blocking the delete. Only the identifying columns: the
    -- narrative lives in the ticket's Cosmos document and the caller is being told
    -- "settle these first", not being shown the case.
    DECLARE @OpenTickets TABLE
    (
        [TicketId] UNIQUEIDENTIFIER NOT NULL,
        [TicketNumber] INT NOT NULL,
        [TicketType] NVARCHAR(24) NOT NULL,
        [RaisedByType] NVARCHAR(16) NOT NULL,
        [Status] NVARCHAR(48) NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
    );

    -- Captured before the scrub so the caller can clean the other stores.
    DECLARE @ServiceCategories TABLE ([ServiceCategory] NVARCHAR(64) NOT NULL);
    DECLARE @BlobUrls TABLE
    (
        [BlobUrl] NVARCHAR(1000) NOT NULL,
        [Kind] NVARCHAR(32) NOT NULL
    );

    DECLARE @DeactivatedServiceCount INT = 0;

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK: serialise against a concurrent booking create or
    -- active-status toggle on this provider.
    SELECT @ProviderAuthIdentityId = [ProviderAuthIdentityId],
           @IsDeleted = [IsDeleted],
           @DeletedAtUtc = [DeletedAtUtc]
    FROM [Provider].[Providers] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId;

    -- XACT_ABORT ON rolls the transaction back on THROW.
    IF @ProviderAuthIdentityId IS NULL
    BEGIN
        THROW 51114, 'Provider profile was not found.', 1;
    END

    IF @IsDeleted = 1
    BEGIN
        -- Idempotent: already anonymised. Report the original timestamp and skip
        -- straight to the result sets (nothing left to clean up out of SQL).
        SET @WasAlreadyDeleted = 1;
        SET @Now = ISNULL(@DeletedAtUtc, @Now);
    END
    ELSE
    BEGIN
        -- An open support ticket refuses the delete — part of the legal hold.
        -- Anonymising a party mid-investigation destroys the account support is
        -- still asking questions of. This is the provider delete's FIRST refusal
        -- path (unlike the parent's, which already refuses on unfinished jobs), so
        -- callers that assumed it always succeeds must now read the flag.
        --
        -- Unlike every other refusal in this codebase there is nothing the
        -- provider can do to clear it themselves: it lifts when support closes the
        -- ticket, and that is the intent.
        INSERT INTO @OpenTickets
            ([TicketId], [TicketNumber], [TicketType], [RaisedByType], [Status], [CreatedAtUtc])
        SELECT [TicketId], [TicketNumber], [TicketType], [RaisedByType], [Status], [CreatedAtUtc]
        FROM [Support].[Tickets]
        WHERE [ProviderId] = @ProviderId
          AND [Status] <> N'CLOSED'
          -- Only the two kinds with a COUNTERPARTY block a delete — see the twin
          -- comment in [Parent].[DeletePetParent]. An app issue or an event
          -- report is not about the other party, and the user cannot close it
          -- themselves, so it must not stand in the way of their own deletion.
          AND [TicketType] IN (N'BookingIncident', N'ChatIncident');

        IF EXISTS (SELECT 1 FROM @OpenTickets)
        BEGIN
            SET @BlockedByOpenTickets = 1;
        END
    END

    -- Everything below only runs when the account is live AND unblocked.
    IF @IsDeleted = 0 AND @BlockedByOpenTickets = 0
    BEGIN
        INSERT INTO @ServiceCategories ([ServiceCategory])
        SELECT [ServiceCategory]
        FROM [Provider].[ProviderServiceRegistrations]
        WHERE [ProviderId] = @ProviderId;

        INSERT INTO @BlobUrls ([BlobUrl], [Kind])
        SELECT [BannerImageUrl], N'ProviderBanner'
        FROM [Provider].[Providers]
        WHERE [ProviderId] = @ProviderId AND [BannerImageUrl] IS NOT NULL
        UNION ALL
        SELECT [PhotoUrl], N'ProviderPhoto'
        FROM [Provider].[ProviderPhotos]
        WHERE [ProviderId] = @ProviderId
        UNION ALL
        SELECT [BannerImageUrl], N'ServiceBanner'
        FROM [Provider].[ProviderServiceBanners]
        WHERE [ProviderId] = @ProviderId;

        ------------------------------------------------------------------
        -- 1. Anonymise the profile row and disable the account.
        --    The mobile placeholder is derived from the ProviderId so it stays
        --    unique under UX_Providers_MobileNumber, and frees the real number.
        ------------------------------------------------------------------
        UPDATE [Provider].[Providers]
        SET [FirstName] = N'Deleted',
            [LastName] = N'Provider',
            [Gender] = N'PreferNotToSay',
            [DateOfBirth] = '1900-01-01',
            [MobileCountryCode] = N'+00',
            [MobileNumber] = N'DEL' + LEFT(REPLACE(CONVERT(NVARCHAR(36), @ProviderId), N'-', N''), 29),
            [MobileVerifiedAtUtc] = NULL,
            [BannerImageUrl] = NULL,
            [IsActive] = 0,
            [IsDeleted] = 1,
            [DeletedAtUtc] = @Now,
            [UpdatedAtUtc] = @Now
        WHERE [ProviderId] = @ProviderId;

        ------------------------------------------------------------------
        -- 2. Sever the Firebase login. FirebaseUserId is UNIQUE, so replacing it
        --    frees the real uid for a fresh sign-up. The ProviderId link is kept
        --    for audit — the row is unreachable by any real Firebase user now.
        ------------------------------------------------------------------
        UPDATE [Provider].[ProviderAuthIdentities]
        SET [FirebaseUserId] = N'deleted:' + CONVERT(NVARCHAR(36), @ProviderId),
            [FirebaseTenantId] = NULL,
            [Email] = N'deleted+' + CONVERT(NVARCHAR(36), @ProviderId) + N'@deleted.invalid',
            [IsEmailVerified] = 0,
            [DisplayName] = NULL,
            [FirebasePhoneNumber] = NULL,
            [PhotoUrl] = NULL,
            [UpdatedAtUtc] = @Now
        WHERE [ProviderAuthIdentityId] = @ProviderAuthIdentityId;

        ------------------------------------------------------------------
        -- 3. Deactivate the bookable services. The rows stay — bookings FK to
        --    ServiceId — but an inactive service is out of the catalog and out
        --    of all five parent-facing searches.
        ------------------------------------------------------------------
        UPDATE [Provider].[ProviderServices]
        SET [IsActive] = 0,
            [UpdatedAtUtc] = @Now
        WHERE [ProviderId] = @ProviderId
          AND [IsActive] = 1;
        SET @DeactivatedServiceCount = @@ROWCOUNT;

        ------------------------------------------------------------------
        -- 4. Remove operational config + media only. Nothing here carries
        --    historical meaning: each booking already froze the cancellation
        --    policy it was created under.
        ------------------------------------------------------------------
        DELETE FROM [Provider].[ProviderDeviceTokens]
        WHERE [ProviderId] = @ProviderId
           OR [ProviderAuthIdentityId] = @ProviderAuthIdentityId;

        DELETE FROM [Provider].[ProviderMobileOtps] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderPhotos] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderServiceBanners] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderWeeklyAvailability] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderClosures] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderCancellationPolicies] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderPayoutMethods] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderServiceRegistrations] WHERE [ProviderId] = @ProviderId;
    END

    -- Result set 1: summary. The retained counts are reported so the caller can
    -- see that history survived the delete. When BlockedByOpenTickets = 1 the
    -- account is UNTOUCHED and every other column here is meaningless — the caller
    -- reads that flag first and goes to result set 4.
    SELECT @ProviderId AS [ProviderId],
           @Now AS [DeletedAtUtc],
           @WasAlreadyDeleted AS [WasAlreadyDeleted],
           @DeactivatedServiceCount AS [DeactivatedServiceCount],
           (SELECT COUNT(*) FROM [Booking].[Bookings] WHERE [ProviderId] = @ProviderId)
               AS [RetainedBookingCount],
           (SELECT COUNT(*) FROM [Booking].[NightStayBookings] WHERE [ProviderId] = @ProviderId)
               AS [RetainedNightStayBookingCount],
           (SELECT COUNT(*) FROM [Event].[Events] WHERE [ProviderId] = @ProviderId)
               AS [RetainedEventCount],
           (SELECT COUNT(*) FROM [Booking].[BookingPayments] WHERE [ProviderId] = @ProviderId)
               AS [RetainedPaymentCount],
           -- Appended LAST on purpose: inserting it next to WasAlreadyDeleted,
           -- where it reads better, would shift every retained-count ordinal and
           -- break a reader deployed against the older procedure.
           @BlockedByOpenTickets AS [BlockedByOpenTickets];

    -- Result set 2: Cosmos [ProviderServices] partition keys (the public listing).
    SELECT [ServiceCategory] FROM @ServiceCategories;

    -- Result set 3: blob URLs to delete best-effort.
    SELECT [BlobUrl], [Kind] FROM @BlobUrls;

    -- Result set 4: the open support tickets that refused the delete. Empty on the
    -- normal path, in which case result sets 1-3 describe a completed delete.
    -- Oldest-first — the one that has been waiting longest is the one to chase.
    SELECT [TicketId], [TicketNumber], [TicketType], [RaisedByType], [Status], [CreatedAtUtc]
    FROM @OpenTickets
    ORDER BY [CreatedAtUtc] ASC;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Provider].[DeleteProvider].';
GO


-- 3.2b-i UpdateProviderBannerImage --------------------------------------------
CREATE OR ALTER PROCEDURE [Provider].[UpdateProviderBannerImage]
    @ProviderId UNIQUEIDENTIFIER,
    @BannerImageUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Provider-level banner (one per provider, overwritten on re-upload). The
    -- per-service banner lives in [Provider].[ProviderServiceBanners] and is a
    -- separate image.
    UPDATE [Provider].[Providers]
    SET [BannerImageUrl] = @BannerImageUrl,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [ProviderId] = @ProviderId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51112, 'Provider was not found.', 1;
    END

    SELECT [ProviderId],
           [BannerImageUrl],
           [UpdatedAtUtc]
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;
END;
GO
PRINT 'Created/updated [Provider].[UpdateProviderBannerImage].';
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
    @IsActive BIT,
    -- "Honour bookings & deactivate": the provider has been shown the future
    -- bookings and commits to serving them, so deactivate for NEW bookings and
    -- leave the existing ones alone. Defaults to 0, which keeps the historic
    -- refusal — the app shows the conflict list first, then resubmits with 1.
    @AcknowledgeExistingBookings BIT = 0
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

    -- A deleted account stays disabled permanently — reactivating it would make
    -- an anonymised provider bookable again.
    IF EXISTS (SELECT 1 FROM [Provider].[Providers]
               WHERE [ProviderId] = @ProviderId AND [IsDeleted] = 1)
    BEGIN
        THROW 51115, 'This provider account has been deleted.', 1;
    END

    DECLARE @HonouredBookingCount INT = 0;

    -- When DEACTIVATING, collect the future active (non-cancelled) bookings
    -- across ALL of this provider's services. A booking is "in the future" when
    -- its date is strictly after today, OR it's today but hasn't ended yet.
    -- UPDLOCK + HOLDLOCK serialises us against concurrent Booking.CreateBooking
    -- so no booking can sneak in between the check and the flip. That still
    -- matters on the acknowledge path: the provider agreed to honour the
    -- bookings they were SHOWN, so one landing mid-flip must be rejected by the
    -- flag rather than silently added to their commitments.
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
          AND b.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
          AND (
              b.[BookingDate] > @Today
              -- "Ended" means COALESCE([ActualEndTime], [EndTime]): a job the
              -- provider already finished early today stops counting as an
              -- outstanding commitment when it is completed, not when it was
              -- scheduled to end.
              OR (b.[BookingDate] = @Today AND COALESCE(b.[ActualEndTime], b.[EndTime]) > @NowTime)
          );

        SELECT @HonouredBookingCount = COUNT(*) FROM @Conflicts;

        -- Without the acknowledgement the bookings BLOCK the deactivation, as
        -- they always have. With it they are honoured: the flip goes ahead and
        -- the count travels back on the success row so the caller can confirm
        -- how many jobs the provider just committed to. Nothing about those
        -- bookings changes — [IsActive] is only ever read by the three booking
        -- CREATE procedures, so an existing job stays startable and completable.
        IF @HonouredBookingCount > 0 AND @AcknowledgeExistingBookings = 0
        BEGIN
            -- Conflict-shape result set: 10 columns (was 8 before custom-job
            -- support landed). The Application reader detects this shape vs
            -- the 4-column success shape and emits the BookingsExist variant.
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

    -- Success-shape result set: 4 columns. [HonouredBookingCount] is 0 on
    -- activation and on a deactivation with nothing outstanding.
    SELECT @ProviderId AS [ProviderId],
           @IsActive AS [IsActive],
           SYSUTCDATETIME() AS [UpdatedAtUtc],
           @HonouredBookingCount AS [HonouredBookingCount];

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


CREATE OR ALTER PROCEDURE [Booking].[CreateBooking]
    @ProviderId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @PetId UNIQUEIDENTIFIER = NULL,
    @ServiceId UNIQUEIDENTIFIER,
    @ServiceCategory NVARCHAR(64),
    @SubCategory NVARCHAR(64),
    @ServiceItemCode NVARCHAR(64) = NULL,
    @BookingDate DATE,
    @StartTime TIME(0),
    @EndTime TIME(0),
    -- Free-text notes the parent attaches to the job at booking time (e.g.
    -- access instructions, the pet's quirks). Optional; surfaced on the
    -- booking-detail read. Stored on App rows too (not a Custom-only column).
    @JobNotes NVARCHAR(2000) = NULL,
    -- Where the service is delivered, as chosen by the parent at booking time:
    -- 'ParentLocation' or 'ProviderLocation'. Optional (NULL for provider-host
    -- and legacy callers); the booking-detail read resolves the address live.
    @LocationType NVARCHAR(32) = NULL,
    -- Snapshot of the offering's unit rate at booking time (per-hour for DayCare,
    -- the flat fee for Vet/Trainer/grooming). Locks the price in so a later rate
    -- change by the provider never re-prices this booking. NULL only for legacy
    -- callers that don't pass it (the detail read falls back to the live rate).
    @PricePerHour DECIMAL(10, 2) = NULL,
    -- Provider business address, resolved by the caller (Cosmos service doc +
    -- registration coordinates) and passed in so a ProviderLocation booking can
    -- snapshot the "where the service happens" address. Ignored for ParentLocation
    -- (the parent's address is snapshotted from SQL) and when no location is set.
    @SnapshotProviderAddressLine NVARCHAR(500) = NULL,
    @SnapshotProviderCity NVARCHAR(200) = NULL,
    @SnapshotProviderZipCode NVARCHAR(32) = NULL,
    @SnapshotProviderLatitude DECIMAL(9, 6) = NULL,
    @SnapshotProviderLongitude DECIMAL(9, 6) = NULL,
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
    BEGIN
        THROW 51061, 'Provider was not found.', 1;
    END

    -- Master Active/Inactive switch — when the provider has flipped themselves
    -- inactive, NO new bookings are accepted on ANY of their services. The
    -- UPDLOCK + HOLDLOCK above serialises us against a concurrent
    -- SetProviderActiveStatus call, so the check is race-safe.
    IF @ProviderIsActive = 0
    BEGIN
        THROW 51067, 'Provider is currently inactive and is not accepting new bookings.', 1;
    END

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51060, 'Pet parent was not found.', 1;
    END

    -- Either party having blocked the other refuses the booking. A block is no
    -- longer the chat-only remedy it began as: it severs the pair across the
    -- product, and a new booking is the most consequential thing it has to stop.
    --
    -- HOLDLOCK, not a plain read. [Block].[BlockParticipant] takes UPDLOCK +
    -- HOLDLOCK over this same key range before it captures the unfinished jobs it
    -- is about to cancel, so the range lock here is what makes the two serialise:
    -- without it a booking created at that instant would commit after the block
    -- and after the job list was taken, and would survive it.
    --
    -- The two directions THROW DIFFERENT codes so the API can word the refusal
    -- without leaking. Only the caller's OWN block may be named ("unblock to
    -- book"); one placed against them must read as a neutral "not available",
    -- because naming it would confirm the other party acted -- the one thing a
    -- block must never do. SQL reports which side acted and C# decides what this
    -- actor is allowed to know, because this procedure is called by BOTH hosts and
    -- has no actor of its own.
    --
    -- If the two have blocked each other, the parent's is reported. That is
    -- deterministic rather than meaningful: the pair is severed either way, and
    -- the only cost is that a provider in a mutual block reads the neutral wording
    -- instead of being told about their own block.
    DECLARE @BlockedByParent BIT = 0;
    DECLARE @BlockedByProvider BIT = 0;

    SELECT @BlockedByParent = MAX(CASE WHEN [BlockerType] = N'PetParent' THEN 1 ELSE 0 END),
           @BlockedByProvider = MAX(CASE WHEN [BlockerType] = N'Provider' THEN 1 ELSE 0 END)
    FROM [Block].[BlockedParticipants] WITH (HOLDLOCK)
    WHERE ([BlockerType] = N'PetParent' AND [BlockerId] = @PetParentId
           AND [BlockedType] = N'Provider' AND [BlockedId] = @ProviderId)
       OR ([BlockerType] = N'Provider' AND [BlockerId] = @ProviderId
           AND [BlockedType] = N'PetParent' AND [BlockedId] = @PetParentId);

    IF @BlockedByParent = 1
    BEGIN
        THROW 51370, 'The pet parent has blocked this provider.', 1;
    END

    IF @BlockedByProvider = 1
    BEGIN
        THROW 51371, 'The provider has blocked this pet parent.', 1;
    END

    -- Defense-in-depth: the API validates pet ownership before calling, but a
    -- direct sproc caller must not be able to pin someone else's pet on a booking.
    IF @PetId IS NOT NULL AND NOT EXISTS (
        SELECT 1
        FROM [Parent].[Pets]
        WHERE [PetId] = @PetId
          AND [PetParentId] = @PetParentId
          -- A soft-deleted pet can't be booked. The row survives only to keep
          -- EXISTING bookings readable; it is not a pet the parent still has.
          AND [IsDeleted] = 0
    )
    BEGIN
        THROW 51068, 'Pet was not found or does not belong to the pet parent.', 1;
    END

    -- Validate that the ServiceId belongs to the provider and is active.
    -- UPDLOCK + HOLDLOCK serialises us against concurrent DeactivateProviderService
    -- so a service can't disappear between our check and the insert.
    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[ProviderServices] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [ProviderId] = @ProviderId
          AND [IsActive] = 1
    )
    BEGIN
        THROW 51066, 'Service is not valid or active for this provider.', 1;
    END

    -- Reject a duplicate booking for the same pet: if this pet already has an
    -- active (non-cancelled) booking on THIS service overlapping the requested
    -- window, block it — a pet can't be in two places for the same slot. The
    -- client can't fully prevent this (two devices, races), so it's enforced here
    -- under the SAME UPDLOCK + HOLDLOCK range as the capacity count below (fully
    -- race-safe: a concurrent duplicate serialises behind us and then sees our row).
    -- Only applies to App bookings that name a pet; Custom walk-ins carry no @PetId.
    -- The existing booking's window ends at COALESCE([ActualEndTime], [EndTime]):
    -- once its job has finished early the pet is demonstrably free again, so it
    -- must not block a fresh booking in the time that was released.
    IF @PetId IS NOT NULL AND EXISTS (
        SELECT 1
        FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [PetId] = @PetId
          AND [BookingDate] = @BookingDate
          AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
          AND [StartTime] < @EndTime
          AND COALESCE([ActualEndTime], [EndTime]) > @StartTime
    )
    BEGIN
        THROW 51069, 'This pet already has a booking for this slot.', 1;
    END

    -- Race-safe capacity check: count active (non-cancelled) bookings overlapping
    -- the requested window FOR THIS SERVICE, holding UPDLOCK + HOLDLOCK so
    -- concurrent CreateBooking calls on the same service serialise. DayCare and
    -- NightStay each have their own capacity bucket. A booking holds its slot in
    -- every status except the two cancelled ones — and only up to
    -- COALESCE([ActualEndTime], [EndTime]), so a job that finished early has
    -- already handed its remaining hours back and does not count against them.
    -- This is the gate the slot grid promises: [Booking].[GetBookingsForDate]
    -- uses the identical expression, so what is shown as free is what is
    -- admitted here.
    DECLARE @Concurrent INT;
    SELECT @Concurrent = COUNT(*)
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ServiceId] = @ServiceId
      AND [BookingDate] = @BookingDate
      AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
      AND [StartTime] < @EndTime
      AND COALESCE([ActualEndTime], [EndTime]) > @StartTime;

    IF @Concurrent >= @Capacity
    BEGIN
        THROW 51062, 'No remaining capacity for this slot.', 1;
    END

    -- Snapshot the provider's current cancellation policy so a later policy change
    -- never re-rules this booking. NULL = no restriction (itself a valid snapshot).
    DECLARE @CancellationPolicyHours INT =
        (SELECT [MinimumHoursBeforeCancellation]
         FROM [Provider].[ProviderCancellationPolicies]
         WHERE [ProviderId] = @ProviderId);

    -- Snapshot the SELECTED service-location address. ParentLocation → the parent's
    -- profile address (in SQL); ProviderLocation → the provider's business address,
    -- resolved by the caller (Cosmos) and passed in. Frozen so a later edit to
    -- either party's address never moves this booking.
    DECLARE @SnapshotAddressLine NVARCHAR(500) = NULL;
    DECLARE @SnapshotCity NVARCHAR(200) = NULL;
    DECLARE @SnapshotZipCode NVARCHAR(32) = NULL;
    DECLARE @SnapshotLatitude DECIMAL(9, 6) = NULL;
    DECLARE @SnapshotLongitude DECIMAL(9, 6) = NULL;

    IF @LocationType = N'ParentLocation'
    BEGIN
        SELECT @SnapshotAddressLine = [AddressLine],
               @SnapshotCity        = [City],
               @SnapshotZipCode     = [ZipCode],
               @SnapshotLatitude    = [Latitude],
               @SnapshotLongitude   = [Longitude]
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId;
    END
    ELSE IF @LocationType = N'ProviderLocation'
    BEGIN
        SET @SnapshotAddressLine = @SnapshotProviderAddressLine;
        SET @SnapshotCity        = @SnapshotProviderCity;
        SET @SnapshotZipCode     = @SnapshotProviderZipCode;
        SET @SnapshotLatitude    = @SnapshotProviderLatitude;
        SET @SnapshotLongitude   = @SnapshotProviderLongitude;
    END

    DECLARE @InsertedBookingId TABLE ([BookingId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[Bookings]
    (
        [ProviderId],
        [PetParentId],
        [PetId],
        [ServiceId],
        [ServiceCategory],
        [SubCategory],
        [ServiceItemCode],
        [BookingDate],
        [StartTime],
        [EndTime],
        [JobNotes],
        [LocationType],
        [PricePerHour],
        [CancellationPolicyHours],
        [SnapshotAddressLine],
        [SnapshotCity],
        [SnapshotZipCode],
        [SnapshotLatitude],
        [SnapshotLongitude]
    )
    OUTPUT inserted.[BookingId] INTO @InsertedBookingId
    VALUES
    (
        @ProviderId,
        @PetParentId,
        @PetId,
        @ServiceId,
        @ServiceCategory,
        @SubCategory,
        @ServiceItemCode,
        @BookingDate,
        @StartTime,
        @EndTime,
        @JobNotes,
        @LocationType,
        @PricePerHour,
        @CancellationPolicyHours,
        @SnapshotAddressLine,
        @SnapshotCity,
        @SnapshotZipCode,
        @SnapshotLatitude,
        @SnapshotLongitude
    );

    DECLARE @BookingId UNIQUEIDENTIFIER = (SELECT TOP (1) [BookingId] FROM @InsertedBookingId);

    -- Seed the audit trail with the creation entry (Status defaults to CREATED).
    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, NULL, N'CREATED', N'System', NULL, N'Booking created');

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [ServiceItemCode],
           [Source],
           [CustomerName],
           [CustomerMobileCountryCode],
           [CustomerMobile],
           [AnimalType],
           [PetName],
           [ServiceLocation],
           [CustomerLocation],
           [PricePerHour],
           [JobNotes],
           [PetId]
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
           [PricePerHour], [JobNotes], [PetId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;
END;
GO
PRINT 'Created/updated [Booking].[GetBooking].';
GO


-- 3.16a Booking.GetBookingDetail ---------------------------------------------
-- Enriched single-booking read backing the booking-detail endpoints. Returns the
-- base columns PLUS [JobNumber], payout fields, and the joined pet-parent / pet
-- records so App bookings (whose customer/pet columns are NULL — those are
-- Custom-walk-in only) can surface customer + pet details. The flat
-- [Booking].[GetBooking] above is left untouched for the internal callers.
CREATE OR ALTER PROCEDURE [Booking].[GetBookingDetail]
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT b.[BookingId],
           b.[JobNumber],
           b.[ProviderId],
           b.[PetParentId],
           b.[ServiceId],
           b.[ServiceCategory],
           b.[SubCategory],
           b.[BookingDate],
           b.[StartTime],
           b.[EndTime],
           b.[Status],
           b.[CreatedAtUtc],
           b.[UpdatedAtUtc],
           b.[CancelledAtUtc],
           b.[ServiceItemCode],
           b.[Source],
           b.[CustomerName],
           b.[CustomerMobileCountryCode],
           b.[CustomerMobile],
           b.[AnimalType],
           b.[PetName],
           b.[ServiceLocation],
           b.[CustomerLocation],
           b.[PricePerHour],
           b.[JobNotes],
           b.[PetId],
           b.[PayoutStatus],
           b.[PayoutId],
           pp.[FirstName]         AS [ParentFirstName],
           pp.[LastName]          AS [ParentLastName],
           pp.[Gender]            AS [ParentGender],
           pp.[MobileCountryCode] AS [ParentMobileCountryCode],
           pp.[MobileNumber]      AS [ParentMobileNumber],
           pp.[ProfilePhotoUrl]   AS [ParentPhotoUrl],
           pet.[PetName]          AS [PetProfileName],
           pet.[PetType]          AS [PetType],
           pet.[Gender]           AS [PetGender],
           pet.[ProfilePhotoUrl]  AS [PetPhotoUrl],
           prov.[FirstName]         AS [ProviderFirstName],
           prov.[LastName]          AS [ProviderLastName],
           prov.[Gender]            AS [ProviderGender],
           prov.[MobileCountryCode] AS [ProviderMobileCountryCode],
           prov.[MobileNumber]      AS [ProviderMobileNumber],
           pet.[Breed]              AS [PetBreed],
           pet.[VaccinationStatus]  AS [PetVaccinationStatus],
           pet.[VaccinationType]    AS [PetVaccinationType],
           pet.[VaccinationDose]    AS [PetVaccinationDose],
           pet.[Prescription]       AS [PetPrescription],
           pet.[SterilizationStatus] AS [PetSterilizationStatus],
           pet.[MedicalHistory]      AS [PetMedicalHistory],
           pet.[Temperament]         AS [PetTemperament],
           b.[LocationType],
           pp.[AddressLine]         AS [ParentAddressLine],
           pp.[City]                AS [ParentCity],
           pp.[ZipCode]             AS [ParentZipCode],
           pp.[Latitude]            AS [ParentLatitude],
           pp.[Longitude]           AS [ParentLongitude],
           -- Vet prescription (per-visit snapshot) + the pet's rolling Vet
           -- next-consultation. Appended LAST so existing reader ordinals hold.
           CASE WHEN rx.[BookingId] IS NULL THEN 0 ELSE 1 END AS [HasPrescription],
           rx.[PrescriptionText],
           rx.[IsPetVaccinated],
           rx.[Vaccinations]        AS [PrescriptionVaccinations],
           nc.[NextConsultationDate] AS [NextConsultationDate],
           -- Snapshots captured at booking time; the detail read PREFERS these over
           -- the live provider policy / resolved address (legacy rows fall back to
           -- live). Appended LAST so existing reader ordinals stay stable.
           b.[CancellationPolicyHours],
           b.[SnapshotAddressLine],
           b.[SnapshotCity],
           b.[SnapshotZipCode],
           b.[SnapshotLatitude],
           b.[SnapshotLongitude],
           -- Payment ledger join. HOW the money changed hands ('Cash'/'Digital')
           -- is recorded only on the ledger row, never on the booking, so the
           -- payment block could not report it without this. Both columns stay
           -- NULL until the provider marks the booking PAID. Appended LAST so
           -- existing reader ordinals stay stable.
           pay.[PaymentMethod] AS [PayoutMethod],
           pay.[PaidAtUtc]
    FROM [Booking].[Bookings] AS b
    LEFT JOIN [Parent].[PetParents] AS pp
        ON pp.[PetParentId] = b.[PetParentId]
    LEFT JOIN [Parent].[Pets] AS pet
        ON pet.[PetId] = b.[PetId]
    LEFT JOIN [Provider].[Providers] AS prov
        ON prov.[ProviderId] = b.[ProviderId]
    LEFT JOIN [Booking].[BookingPrescriptions] AS rx
        ON rx.[BookingId] = b.[BookingId]
    LEFT JOIN [Parent].[PetNextConsultations] AS nc
        ON nc.[PetId] = b.[PetId] AND nc.[ConsultationType] = N'Vet'
    -- BookingType discriminates which booking table BookingId points at — the
    -- ledger is shared by single-day and night-stay bookings and has no FK.
    LEFT JOIN [Booking].[BookingPayments] AS pay
        ON pay.[BookingId] = b.[BookingId] AND pay.[BookingType] = N'SingleDay'
    WHERE b.[BookingId] = @BookingId;
END;
GO
PRINT 'Created/updated [Booking].[GetBookingDetail].';
GO


-- 3.16b Booking.UpsertBookingPrescription ------------------------------------
-- Records (or replaces) the vet's per-visit prescription for a booking. Only the
-- booking's provider, only a Vet service, only once the job is underway
-- (IN_PROGRESS; the retired ENDING kept for legacy rows) or COMPLETED. The
-- next-consultation date is not stored here (it lives on PetNextConsultations and
-- is joined into the read). Returns the saved row + the pet's Vet next-consult.
CREATE OR ALTER PROCEDURE [Booking].[UpsertBookingPrescription]
    @BookingId        UNIQUEIDENTIFIER,
    @ProviderId       UNIQUEIDENTIFIER,
    @PrescriptionText NVARCHAR(4000),
    @IsPetVaccinated  BIT,
    @Vaccinations     NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @RowProviderId      UNIQUEIDENTIFIER;
    DECLARE @RowServiceCategory NVARCHAR(64);
    DECLARE @RowStatus          NVARCHAR(48);

    SELECT @RowProviderId      = [ProviderId],
           @RowServiceCategory = [ServiceCategory],
           @RowStatus          = [Status]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    IF @RowProviderId IS NULL
        THROW 51290, 'Booking not found.', 1;

    IF @RowProviderId <> @ProviderId
        THROW 51291, 'You are not the provider on this booking.', 1;

    IF @RowServiceCategory <> N'Vet'
        THROW 51292, 'A prescription can only be recorded on a Vet booking.', 1;

    IF @RowStatus NOT IN (N'IN_PROGRESS', N'ENDING', N'COMPLETED')
        THROW 51293, 'A prescription can only be recorded once the job has started or completed.', 1;

    UPDATE [Booking].[BookingPrescriptions]
    SET [PrescriptionText] = @PrescriptionText,
        [IsPetVaccinated]  = @IsPetVaccinated,
        [Vaccinations]     = @Vaccinations,
        [UpdatedAtUtc]     = SYSUTCDATETIME()
    WHERE [BookingId] = @BookingId;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO [Booking].[BookingPrescriptions]
            ([BookingId], [PrescriptionText], [IsPetVaccinated], [Vaccinations])
        VALUES
            (@BookingId, @PrescriptionText, @IsPetVaccinated, @Vaccinations);
    END

    SELECT bp.[BookingId],
           bp.[PrescriptionText],
           bp.[IsPetVaccinated],
           bp.[Vaccinations],
           nc.[NextConsultationDate],
           bp.[CreatedAtUtc],
           bp.[UpdatedAtUtc]
    FROM [Booking].[BookingPrescriptions] AS bp
    INNER JOIN [Booking].[Bookings] AS b
        ON b.[BookingId] = bp.[BookingId]
    LEFT JOIN [Parent].[PetNextConsultations] AS nc
        ON nc.[PetId] = b.[PetId] AND nc.[ConsultationType] = N'Vet'
    WHERE bp.[BookingId] = @BookingId;
END;
GO
PRINT 'Created/updated [Booking].[UpsertBookingPrescription].';
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

    IF @CurrentStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
        THROW 51065, 'Booking is already cancelled.', 1;

    UPDATE [Booking].[Bookings]
    SET [Status] = N'PARENT_CANCELLED',
        [CancelledAtUtc] = @Now,
        [UpdatedAtUtc] = @Now
    WHERE [BookingId] = @BookingId;

    -- Audit the cancellation (the booker is, by definition, the parent here).
    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES (@BookingId, @CurrentStatus, N'PARENT_CANCELLED', N'Parent', @PetParentId, NULL);

    SELECT [BookingId], [ProviderId], [PetParentId], [ServiceId], [ServiceCategory], [SubCategory],
           [BookingDate], [StartTime], [EndTime], [Status],
           [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc], [ServiceItemCode],
           [Source], [CustomerName], [CustomerMobileCountryCode], [CustomerMobile],
           [AnimalType], [PetName], [ServiceLocation], [CustomerLocation],
           [PricePerHour], [JobNotes], [PetId]
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
           [PricePerHour], [JobNotes], [PetId]
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
           [PricePerHour], [JobNotes], [PetId],
           -- Frozen-at-creation extras for the parent "my bookings" cards.
           -- Appended LAST so the shared booking-row reader's ordinals hold.
           [LocationType], [CancellationPolicyHours],
           [SnapshotAddressLine], [SnapshotCity], [SnapshotZipCode],
           [SnapshotLatitude], [SnapshotLongitude]
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

    -- A booking holds its slot in every status except the two cancelled ones
    -- and PROVIDER_DECLINED. Night-stay bookings live in
    -- [Booking].[NightStayBookings]; a stay covering this date
    -- (CheckInDate <= date < CheckOutDate) occupies its NightStay bucket for
    -- the WHOLE night, so it is surfaced as a full-day window. Rows only match
    -- a NightStay ServiceId, so DayCare & co. see none.
    -- A job that FINISHED EARLY holds only the time it actually used: the
    -- occupied window ends at COALESCE([ActualEndTime], [EndTime]), so the
    -- unused remainder is offered to other parents. NULL for everything that
    -- did not finish early. The BOOKED [EndTime] is untouched and is still what
    -- the booking is priced and read against.
    SELECT [StartTime],
           COALESCE([ActualEndTime], [EndTime]) AS [EndTime]
    FROM [Booking].[Bookings]
    WHERE [ServiceId] = @ServiceId
      AND [BookingDate] = @BookingDate
      AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')

    UNION ALL

    SELECT CAST(N'00:00:00' AS TIME(0)) AS [StartTime],
           CAST(N'23:59:59' AS TIME(0)) AS [EndTime]
    FROM [Booking].[NightStayBookings]
    WHERE [ServiceId] = @ServiceId
      AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
      AND [CheckInDate] <= @BookingDate
      AND COALESCE([ActualCheckOutDate], [CheckOutDate]) > @BookingDate

    ORDER BY [StartTime];
END;
GO
PRINT 'Created/updated [Booking].[GetBookingsForDate].';
GO

-- 3.20a Booking.GetAgendaForDate ---------------------------------------------
-- Backs the parent-facing daily-agenda surface. Same rows (and the same
-- occupied-slot predicate) as [Booking].[GetBookingsForDate] above, but carries
-- the identity the agenda needs: who booked it, its job number, and its status.
-- The agenda MUST agree with the slot grid on what is occupied, so the two
-- predicates are kept identical; change one, change the other.
--
-- [PetParentId] is what lets the caller's OWN jobs be told apart from everyone
-- else's: the API masks the status + job id of rows belonging to a different
-- parent. NULL for Custom walk-ins, which therefore always mask.
CREATE OR ALTER PROCEDURE [Booking].[GetAgendaForDate]
    @ServiceId   UNIQUEIDENTIFIER,
    @BookingDate DATE
AS
BEGIN
    SET NOCOUNT ON;

    SELECT N'SingleDay' AS [BookingType],
           [BookingId],
           [JobNumber],
           [PetParentId],
           [StartTime],
           -- Early finish releases the remainder; identical expression to
           -- [Booking].[GetBookingsForDate], which this MUST agree with.
           COALESCE([ActualEndTime], [EndTime]) AS [EndTime],
           [Status]
    FROM [Booking].[Bookings]
    WHERE [ServiceId] = @ServiceId
      AND [BookingDate] = @BookingDate
      AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')

    UNION ALL

    SELECT N'NightStay' AS [BookingType],
           [NightStayBookingId] AS [BookingId],
           [JobNumber],
           [PetParentId],
           CAST(N'00:00:00' AS TIME(0)) AS [StartTime],
           CAST(N'23:59:59' AS TIME(0)) AS [EndTime],
           [Status]
    FROM [Booking].[NightStayBookings]
    WHERE [ServiceId] = @ServiceId
      AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
      AND [CheckInDate] <= @BookingDate
      AND COALESCE([ActualCheckOutDate], [CheckOutDate]) > @BookingDate

    ORDER BY [StartTime], [EndTime];
END;
GO
PRINT 'Created/updated [Booking].[GetAgendaForDate].';
GO


-- 3.20b Booking.GetNightStayOccupancy ----------------------------------------
-- Per-night occupancy for a NightStay service: how many active stays (not
-- cancelled/declined) cover each night in [@FromNight, @ToNight] (inclusive;
-- a stay covers night n when CheckInDate <= n < CheckOutDate). Backs the
-- date-granular NightStay availability surface. Every night in the range is
-- returned, zero occupancy included.
CREATE OR ALTER PROCEDURE [Booking].[GetNightStayOccupancy]
    @ServiceId UNIQUEIDENTIFIER,
    @FromNight DATE,
    @ToNight   DATE
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH [Nights] AS
    (
        SELECT @FromNight AS [Night]
        UNION ALL
        SELECT DATEADD(DAY, 1, [Night])
        FROM [Nights]
        WHERE [Night] < @ToNight
    )
    SELECT n.[Night],
           COUNT(b.[NightStayBookingId]) AS [ActiveStays]
    FROM [Nights] n
    LEFT JOIN [Booking].[NightStayBookings] b
        ON b.[ServiceId] = @ServiceId
       AND b.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
       AND b.[CheckInDate] <= n.[Night]
       -- A stay that ended EARLY stops holding a place from the day the pet
       -- actually went home, freeing the remaining nights.
       AND COALESCE(b.[ActualCheckOutDate], b.[CheckOutDate]) > n.[Night]
    GROUP BY n.[Night]
    ORDER BY n.[Night]
    OPTION (MAXRECURSION 366);
END;
GO
PRINT 'Created/updated [Booking].[GetNightStayOccupancy].';
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

    -- Custom and App bookings share one capacity bucket per ServiceId. A booking
    -- holds its slot in every status except the two cancelled ones.
    DECLARE @Concurrent INT;
    SELECT @Concurrent = COUNT(*)
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ServiceId] = @ServiceId
      AND [BookingDate] = @BookingDate
      AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
      AND [StartTime] < @EndTime
      -- A booking holds its slot only up to COALESCE([ActualEndTime], [EndTime]),
      -- so hours handed back by a job that finished early do not count against
      -- capacity. App and Custom bookings share one bucket and both sprocs use
      -- this identical expression, as does [Booking].[GetBookingsForDate] — so
      -- what the slot grid shows as free is exactly what is admitted here.
      AND COALESCE([ActualEndTime], [EndTime]) > @StartTime;

    IF @Concurrent >= @Capacity
        THROW 51062, 'No remaining capacity for this slot.', 1;

    -- Snapshot the provider's current cancellation policy (NULL = no restriction).
    -- Custom walk-ins carry no LocationType, so no address snapshot applies.
    DECLARE @CancellationPolicyHours INT =
        (SELECT [MinimumHoursBeforeCancellation]
         FROM [Provider].[ProviderCancellationPolicies]
         WHERE [ProviderId] = @ProviderId);

    DECLARE @InsertedBookingId TABLE ([BookingId] UNIQUEIDENTIFIER);

    -- Provider-added walk-in is the provider's own job — already confirmed.
    INSERT INTO [Booking].[Bookings]
    ([ProviderId], [PetParentId], [ServiceId], [ServiceCategory], [SubCategory],
     [ServiceItemCode], [BookingDate], [StartTime], [EndTime], [Status],
     [Source], [CustomerName], [CustomerMobileCountryCode], [CustomerMobile],
     [AnimalType], [PetName], [ServiceLocation], [CustomerLocation],
     [PricePerHour], [JobNotes], [CancellationPolicyHours])
    OUTPUT inserted.[BookingId] INTO @InsertedBookingId
    VALUES
    (@ProviderId, NULL, @ServiceId, @ServiceCategory, @SubCategory,
     NULL, @BookingDate, @StartTime, @EndTime, N'CONFIRMED',
     N'Custom', @CustomerName, @CustomerMobileCountryCode, @CustomerMobile,
     @AnimalType, @PetName, @ServiceLocation, @CustomerLocation,
     @PricePerHour, @JobNotes, @CancellationPolicyHours);

    DECLARE @BookingId UNIQUEIDENTIFIER = (SELECT TOP (1) [BookingId] FROM @InsertedBookingId);

    -- Seed the audit trail with the creation entry (walk-ins start CONFIRMED).
    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES (@BookingId, NULL, N'CONFIRMED', N'System', NULL, N'Booking created');

    SELECT [BookingId], [ProviderId], [PetParentId], [ServiceId], [ServiceCategory], [SubCategory],
           [BookingDate], [StartTime], [EndTime], [Status],
           [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc], [ServiceItemCode],
           [Source], [CustomerName], [CustomerMobileCountryCode], [CustomerMobile],
           [AnimalType], [PetName], [ServiceLocation], [CustomerLocation],
           [PricePerHour], [JobNotes], [PetId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Booking].[CreateCustomBooking].';
GO

-- 3.20c Booking.UpdateCustomBooking ------------------------------------------
-- Provider edits a Custom walk-in they recorded earlier ("edit this private job").
-- Walk-ins were create-only until 2026-08-24, so a provider's later corrections to
-- the price, the service or the notes lived on their phone and never reached the
-- server — which meant the amount the app showed and the amount in
-- [Booking].[BookingAmounts] (and therefore in earnings) could differ with nothing
-- to reconcile them.
--
-- CUSTOM WALK-INS ONLY. An App booking is a two-party agreement and is changed
-- through the modification flow, where the counterparty gets to accept or decline.
-- A walk-in is the provider's own record of their own job, so there is nobody to
-- ask — which is exactly why this is a plain edit and not a modification.
--
-- WHAT CAN BE EDITED WHEN:
--   * price, customer details, pet details, location text and notes — while the
--     booking is CONFIRMED, IN_PROGRESS **or COMPLETED**. Allowing a completed job
--     to be re-priced is deliberate: correcting the money after the fact is the
--     main thing this exists for, and a walk-in can never be marked PAID, so there
--     is no ledger row to contradict and no [PayoutId] to invalidate.
--   * the SERVICE and the SCHEDULE (date / times) — only while CONFIRMED. Moving
--     the window of a job that has already started or finished is incoherent, and
--     would mean re-running a capacity check against a slot in the past. THROW
--     51384 rather than silently ignoring those fields, so a client cannot believe
--     it saved a change it did not.
--
-- Statuses that mean the job did NOT happen (cancelled, no-show, expired) refuse
-- the edit outright: there is nothing left to correct, and re-pricing a cancelled
-- job would put money back into a figure that has already settled.
--
-- Capacity is re-checked exactly as [Booking].[CreateCustomBooking] does, under the
-- same UPDLOCK + HOLDLOCK, and MUST stay identical to it — Custom and App bookings
-- share one per-service bucket. The one difference is that this booking excludes
-- ITSELF from the count, or moving a job by five minutes would collide with itself.
--
-- THROWs: 51380 not found, 51381 not the provider on this booking, 51382 not a
-- walk-in, 51383 the job did not happen and cannot be edited, 51384 schedule or
-- service change after the job started; plus 51066 (unknown/inactive service) and
-- 51062 (no capacity) shared with the create path.
CREATE OR ALTER PROCEDURE [Booking].[UpdateCustomBooking]
    @BookingId                 UNIQUEIDENTIFIER,
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

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @Source NVARCHAR(16);
    DECLARE @CurrentServiceId UNIQUEIDENTIFIER;
    DECLARE @CurrentBookingDate DATE;
    DECLARE @CurrentStartTime TIME(0);
    DECLARE @CurrentEndTime TIME(0);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status],
           @RowProvider = [ProviderId],
           @Source = [Source],
           @CurrentServiceId = [ServiceId],
           @CurrentBookingDate = [BookingDate],
           @CurrentStartTime = [StartTime],
           @CurrentEndTime = [EndTime]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51380, 'Booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51381, 'You are not the provider on this booking.', 1;
    END

    IF @Source <> N'Custom'
    BEGIN
        THROW 51382, 'Only a private walk-in booking can be edited here.', 1;
    END

    IF @CurrentStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                          N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED',
                          N'OTP_MAX_ATTEMPTS_EXCEEDED')
    BEGIN
        THROW 51383, 'This job did not happen and can no longer be edited.', 1;
    END

    DECLARE @ScheduleChanged BIT =
        CASE WHEN @ServiceId <> @CurrentServiceId
                  OR @BookingDate <> @CurrentBookingDate
                  OR @StartTime <> @CurrentStartTime
                  OR @EndTime <> @CurrentEndTime
             THEN 1 ELSE 0 END;

    IF @ScheduleChanged = 1 AND @CurrentStatus <> N'CONFIRMED'
    BEGIN
        THROW 51384, 'The service and schedule can only be changed before the job starts.', 1;
    END

    -- Only worth re-validating when the window or the service actually moved. An
    -- edit that merely corrects the price must not fail because the provider has
    -- since deactivated that service, or because the slot has filled up with the
    -- bookings that came after this one.
    IF @ScheduleChanged = 1
    BEGIN
        IF NOT EXISTS (
            SELECT 1
            FROM [Provider].[ProviderServices] WITH (UPDLOCK, HOLDLOCK)
            WHERE [ServiceId] = @ServiceId
              AND [ProviderId] = @ProviderId
              AND [IsActive] = 1
        )
        BEGIN
            THROW 51066, 'Service is not valid or active for this provider.', 1;
        END

        DECLARE @Concurrent INT;
        SELECT @Concurrent = COUNT(*)
        FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [BookingDate] = @BookingDate
          AND [BookingId] <> @BookingId
          AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
          AND [StartTime] < @EndTime
          AND COALESCE([ActualEndTime], [EndTime]) > @StartTime;

        IF @Concurrent >= @Capacity
        BEGIN
            THROW 51062, 'No remaining capacity for this slot.', 1;
        END
    END

    UPDATE [Booking].[Bookings]
    SET [ServiceId]                 = @ServiceId,
        [ServiceCategory]           = @ServiceCategory,
        [SubCategory]               = @SubCategory,
        [CustomerName]              = @CustomerName,
        [CustomerMobileCountryCode] = @CustomerMobileCountryCode,
        [CustomerMobile]            = @CustomerMobile,
        [AnimalType]                = @AnimalType,
        [PetName]                   = @PetName,
        [BookingDate]               = @BookingDate,
        [StartTime]                 = @StartTime,
        [EndTime]                   = @EndTime,
        [ServiceLocation]           = @ServiceLocation,
        [CustomerLocation]          = @CustomerLocation,
        [PricePerHour]              = @PricePerHour,
        [JobNotes]                  = @JobNotes,
        [UpdatedAtUtc]              = @Now
    WHERE [BookingId] = @BookingId;

    -- The status does not move, so this is not a transition — but the audit trail
    -- is the only record that the job's terms were rewritten, and on a one-party
    -- booking there is no counterparty who would otherwise notice. A row with
    -- FromStatus = ToStatus reads correctly as "edited, not transitioned".
    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, @CurrentStatus, N'Provider', @ProviderId,
         CASE WHEN @ScheduleChanged = 1
              THEN N'Walk-in edited by the provider (service or schedule changed)'
              ELSE N'Walk-in edited by the provider' END);

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [ServiceItemCode],
           [Source],
           [CustomerName],
           [CustomerMobileCountryCode],
           [CustomerMobile],
           [AnimalType],
           [PetName],
           [ServiceLocation],
           [CustomerLocation],
           [PricePerHour],
           [JobNotes],
           [PetId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Booking].[UpdateCustomBooking].';
GO


-- 3.20c Booking.UpdateBookingStatus ------------------------------------------
-- Moves a booking to a new lifecycle status and writes an audit row, atomically.
-- @Actor ('Provider'|'Parent') + @ActorId come from the authenticated route.
-- THROWs: 51120 not found, 51121 not a party, 51122 status not allowed for actor,
-- 51123 terminal, 51124 unchanged, 51125 invalid actor/status value,
-- 51126 transition not allowed from the current status,
-- 51128 no-show reported earlier than 30 minutes after the scheduled start,
-- 51129 booking expired (CREATED for 24+ hours - no longer acceptable). REJECT
--        ONLY: the EXPIRED status is written by the scheduled external job, not
--        here; this sproc never changes status on the basis of elapsed time.
-- 51153 booking expired (still CREATED with under 2 hours to the service - see
--        BR-53). Also REJECT ONLY, same reasoning as 51129.
CREATE OR ALTER PROCEDURE [Booking].[UpdateBookingStatus]
    @BookingId UNIQUEIDENTIFIER,
    @NewStatus NVARCHAR(48),
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @Note NVARCHAR(500) = NULL,
    -- The acting party's position. Only ever populated by the two no-show routes
    -- (and the legacy /status shim when it is used to set a no-show, which the API
    -- gates identically so that path cannot become a way to skip the capture).
    -- See [Booking].[StartBooking] for why these are defaulted to NULL.
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Static validation of the inputs (defense-in-depth; the API validates too).
    IF @Actor NOT IN (N'Provider', N'Parent')
    BEGIN
        THROW 51125, 'Actor must be Provider or Parent.', 1;
    END

    IF @NewStatus NOT IN (N'CONFIRMED', N'PROVIDER_DECLINED', N'COMPLETED',
                          N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                          N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
    BEGIN
        THROW 51125, 'Unknown or non-engine booking status.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @BookingDate DATE;
    DECLARE @StartTime TIME(0);
    DECLARE @EndTime TIME(0);
    DECLARE @CreatedAtUtc DATETIME2(7);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status],
           @ProviderId = [ProviderId],
           @PetParentId = [PetParentId],
           @BookingDate = [BookingDate],
           @StartTime = [StartTime],
           @EndTime = [EndTime],
           @CreatedAtUtc = [CreatedAtUtc]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51120, 'Booking was not found.', 1;
    END

    -- The actor must be a party to this booking.
    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51121, 'You are not a party to this booking.', 1;
    END

    -- A booking left pending (CREATED) for 24+ hours has expired: reject the
    -- attempted transition — the provider can no longer accept it.
    -- REJECT ONLY: the EXPIRED status is deliberately NOT written here. Status
    -- changes driven by elapsed time belong to the scheduled external job, which
    -- is the single writer for them; this guard just stops a late accept from
    -- slipping through before that job runs. The row therefore stays in CREATED
    -- until the job settles it.
    IF @CurrentStatus = N'CREATED' AND @Now >= DATEADD(HOUR, 24, @CreatedAtUtc)
    BEGIN
        THROW 51129, 'Booking has expired after 24 hours awaiting provider acceptance and can no longer change.', 1;
    END

    -- BR-53: a booking still in CREATED with under 2 hours to the service has
    -- expired too — the provider is out of time to accept it, and the same
    -- cutoff (serviceStart - 2h) is the one BR-01 uses to refuse a fresh booking
    -- for that slot. REJECT ONLY, for the same reason as the guard above: the
    -- scheduled external job is the single writer of EXPIRED, so the row stays
    -- in CREATED until it runs. Without this guard the rule would only hold to
    -- the job's 5-minute granularity, and a provider could accept minutes before
    -- the service starts.
    IF @CurrentStatus = N'CREATED'
       AND DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @StartTime),
                   CAST(@BookingDate AS DATETIME2(7))) < DATEADD(HOUR, 2, @Now)
    BEGIN
        THROW 51153, 'Booking has expired: it was never accepted and the service now starts in under 2 hours.', 1;
    END

    -- The status must be one this actor is allowed to set via the engine.
    -- A no-show always names the OTHER party: the provider reports the parent's
    -- no-show, the parent reports the provider's.
    IF (@Actor = N'Provider'
            AND @NewStatus NOT IN (N'CONFIRMED', N'PROVIDER_DECLINED', N'COMPLETED', N'PROVIDER_CANCELLED', N'PARENT_NO_SHOW'))
       OR (@Actor = N'Parent'
            AND @NewStatus NOT IN (N'PARENT_CANCELLED', N'PROVIDER_NO_SHOW'))
    BEGIN
        THROW 51122, 'This status is not permitted for this actor.', 1;
    END

    -- A booking in a terminal state can't change further.
    IF @CurrentStatus IN (N'COMPLETED', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                          N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
    BEGIN
        THROW 51123, 'Booking is in a terminal state and cannot change.', 1;
    END

    IF @CurrentStatus = @NewStatus
    BEGIN
        THROW 51124, 'Booking is already in the requested status.', 1;
    END

    -- From-state rules for the engine transitions.
    IF (@NewStatus = N'CONFIRMED'        AND @CurrentStatus <> N'CREATED')
       OR (@NewStatus = N'PROVIDER_DECLINED' AND @CurrentStatus <> N'CREATED')
       OR (@NewStatus = N'COMPLETED'     AND @CurrentStatus NOT IN (N'IN_PROGRESS', N'ENDING'))
       OR (@NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
           AND @CurrentStatus NOT IN (N'CONFIRMED',
                                      N'PROVIDER_ACCEPTED_MODIFICATION', N'PARENT_ACCEPTED_MODIFICATION',
                                      N'PROVIDER_DECLINED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                      N'START_JOB'))
    BEGIN
        THROW 51126, 'This transition is not allowed from the current status.', 1;
    END

    -- A cancel is allowed from any non-terminal state EXCEPT once the job is
    -- actively underway (IN_PROGRESS; the retired ENDING kept for legacy rows)
    -- — by then it runs to completion.
    IF @NewStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED')
       AND @CurrentStatus IN (N'IN_PROGRESS', N'ENDING')
    BEGIN
        THROW 51149, 'The job is already in progress and can no longer be cancelled.', 1;
    END

    -- A no-show can only be reported once the counterparty is actually late:
    -- 30 minutes past the booking's scheduled start (all times are UTC).
    IF @NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
    BEGIN
        DECLARE @StartsAtUtc DATETIME2(7) =
            DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @StartTime),
                    CAST(@BookingDate AS DATETIME2(7)));
        IF @Now < DATEADD(MINUTE, 30, @StartsAtUtc)
        BEGIN
            THROW 51128, 'A no-show can only be reported 30 minutes after the booking''s scheduled start.', 1;
        END
    END

    -- COMPLETED is reachable here through the legacy /status shim as well as
    -- through [Booking].[CompleteBooking], so an early finish must release the
    -- rest of the booked window from BOTH paths — otherwise which endpoint the
    -- provider happened to tap would decide whether the slot came back. Same
    -- rule and the same clamps as the dedicated sproc; see it for the reasoning.
    DECLARE @ActualEndTime TIME(0) = NULL;

    IF @NewStatus = N'COMPLETED' AND CAST(@Now AS DATE) = @BookingDate
    BEGIN
        DECLARE @NowTime TIME(0) = CAST(@Now AS TIME(0));
        IF @NowTime < @EndTime
        BEGIN
            SET @ActualEndTime = CASE WHEN @NowTime < @StartTime THEN @StartTime ELSE @NowTime END;
        END
    END

    UPDATE [Booking].[Bookings]
    SET [Status] = @NewStatus,
        [UpdatedAtUtc] = @Now,
        -- Only ever written on the COMPLETED transition; every other status
        -- leaves whatever is there alone (it is NULL for all of them anyway,
        -- since COMPLETED is terminal apart from the move to PAID).
        [ActualEndTime] = CASE
            WHEN @NewStatus = N'COMPLETED' THEN @ActualEndTime
            ELSE [ActualEndTime]
        END,
        -- A no-show ends the job with nobody having performed and nobody owing,
        -- so the payout is settled as 'NO_PAYOUT' rather than left reading
        -- 'Pending' forever. Terminal, and safe to overwrite unconditionally
        -- here: the from-state guards above only admit a no-show from a
        -- confirmed-equivalent status or START_JOB, none of which can already
        -- have been paid (PAID is only reachable from COMPLETED, and is itself
        -- terminal). EXPIRED is settled the same way by the sweep, which is its
        -- only writer.
        [PayoutStatus] = CASE
            WHEN @NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW') THEN N'NO_PAYOUT'
            ELSE [PayoutStatus]
        END,
        [CancelledAtUtc] = CASE
            WHEN @NewStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED') THEN @Now
            ELSE [CancelledAtUtc]
        END
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

    -- Where the reporting party was when they marked the counterparty absent.
    -- Written in the same transaction as the status flip: a no-show is terminal
    -- and frees capacity, so it must never be possible to have one on record with
    -- no idea where the person reporting it stood.
    IF @NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
       AND @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[BookingLocationEvents]
            ([BookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@BookingId, N'NoShowMarked', @Actor, @ActorId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    -- Notify the OTHER party, inside this transaction so the notification can
    -- never exist without the status change (or vice versa). The actor never gets
    -- one: they tapped it and saw the result — the "relevance rule".
    DECLARE @NotificationType NVARCHAR(64) =
        CASE @NewStatus
            WHEN N'CONFIRMED'          THEN N'BOOKING_ACCEPTED'
            WHEN N'PROVIDER_DECLINED'  THEN N'BOOKING_DECLINED'
            WHEN N'PROVIDER_CANCELLED' THEN N'BOOKING_CANCELLED_BY_PROVIDER'
            WHEN N'PARENT_CANCELLED'   THEN N'BOOKING_CANCELLED_BY_PARENT'
            WHEN N'COMPLETED'          THEN N'BOOKING_COMPLETED'
            WHEN N'PARENT_NO_SHOW'     THEN N'BOOKING_NO_SHOW_REPORTED'
            WHEN N'PROVIDER_NO_SHOW'   THEN N'BOOKING_NO_SHOW_REPORTED'
        END;

    IF @NotificationType IS NOT NULL
    BEGIN
        -- A no-show always names the absent party, which is what lets one
        -- template read correctly in both directions.
        DECLARE @AbsentParty NVARCHAR(32) =
            CASE @NewStatus
                WHEN N'PARENT_NO_SHOW'   THEN N'the customer'
                WHEN N'PROVIDER_NO_SHOW' THEN N'the provider'
            END;

        DECLARE @Audience NVARCHAR(16) =
            CASE WHEN @Actor = N'Provider' THEN N'PetParent' ELSE N'Provider' END;

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = 0,
            @Audience = @Audience,
            @NotificationType = @NotificationType,
            @AbsentParty = @AbsentParty;
    END

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [ServiceItemCode],
           [Source],
           [CustomerName],
           [CustomerMobileCountryCode],
           [CustomerMobile],
           [AnimalType],
           [PetName],
           [ServiceLocation],
           [CustomerLocation],
           [PricePerHour],
           [JobNotes],
           [PetId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Booking].[UpdateBookingStatus].';
GO


-- 3.20d Booking.ListBookingStatusHistory -------------------------------------
-- Full status-change audit trail for a booking, oldest-first. Empty when none
-- (or unknown booking). Authorization is enforced at the endpoint layer.
CREATE OR ALTER PROCEDURE [Booking].[ListBookingStatusHistory]
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingStatusHistoryId],
           [BookingId],
           [FromStatus],
           [ToStatus],
           [ChangedByActor],
           [ChangedByActorId],
           [Note],
           [ChangedAtUtc]
    FROM [Booking].[BookingStatusHistory]
    WHERE [BookingId] = @BookingId
    ORDER BY [ChangedAtUtc] ASC, [BookingStatusHistoryId] ASC;
END;
GO
PRINT 'Created/updated [Booking].[ListBookingStatusHistory].';
GO


-- Race-safe insert of a multi-night boarding booking. Mirrors
-- [Booking].[CreateBooking] but capacity is enforced PER NIGHT across
-- [@CheckInDate, @CheckOutDate) rather than by time-overlap on a single date.
-- THROWs: 51230 provider not found, 51231 provider inactive, 51232 pet parent
-- not found, 51233 pet not found / not owned, 51234 service unknown/inactive/
-- not owned/not a NightStay service, 51235 no capacity on one or more nights,
-- 51370 / 51371 one of the two has blocked the other (which code says which
-- side placed it, so the API can name the caller's own block and stay neutral
-- about one placed against them).
CREATE OR ALTER PROCEDURE [Booking].[CreateNightStayBooking]
    @ProviderId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @PetId UNIQUEIDENTIFIER = NULL,
    @ServiceId UNIQUEIDENTIFIER,
    @ServiceCategory NVARCHAR(64),
    @SubCategory NVARCHAR(64),
    @CheckInDate DATE,
    @CheckOutDate DATE,
    @DropOffTime TIME(0),
    @PickUpTime TIME(0),
    -- Optional free-text notes the parent attaches to the stay (feeding
    -- instructions, the pet's quirks, etc.). Surfaced on the detail read.
    @JobNotes NVARCHAR(2000) = NULL,
    -- Where the service is delivered: 'ParentLocation' or 'ProviderLocation'.
    -- Optional (NULL for legacy callers); the detail read resolves the address.
    @LocationType NVARCHAR(32) = NULL,
    -- Snapshot of the offering's per-night rate at booking time. Locks the price
    -- in so a later rate change never re-prices this stay. NULL only for legacy
    -- callers (the detail read falls back to the live offering rate).
    @PricePerNight DECIMAL(10, 2) = NULL,
    -- Provider business address, resolved by the caller (Cosmos service doc +
    -- registration coordinates) and passed in so a ProviderLocation stay can
    -- snapshot the service-location address. Ignored for ParentLocation (the
    -- parent's address is snapshotted from SQL) and when no location is set.
    @SnapshotProviderAddressLine NVARCHAR(500) = NULL,
    @SnapshotProviderCity NVARCHAR(200) = NULL,
    @SnapshotProviderZipCode NVARCHAR(32) = NULL,
    @SnapshotProviderLatitude DECIMAL(9, 6) = NULL,
    @SnapshotProviderLongitude DECIMAL(9, 6) = NULL,
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
    BEGIN
        THROW 51230, 'Provider was not found.', 1;
    END

    -- Master Active/Inactive switch — when the provider has flipped themselves
    -- inactive, NO new bookings are accepted on ANY of their services. The
    -- UPDLOCK + HOLDLOCK above serialises us against a concurrent
    -- SetProviderActiveStatus call, so the check is race-safe.
    IF @ProviderIsActive = 0
    BEGIN
        THROW 51231, 'Provider is currently inactive and is not accepting new bookings.', 1;
    END

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51232, 'Pet parent was not found.', 1;
    END

    -- Either party having blocked the other refuses the booking. A block is no
    -- longer the chat-only remedy it began as: it severs the pair across the
    -- product, and a new booking is the most consequential thing it has to stop.
    --
    -- HOLDLOCK, not a plain read. [Block].[BlockParticipant] takes UPDLOCK +
    -- HOLDLOCK over this same key range before it captures the unfinished jobs it
    -- is about to cancel, so the range lock here is what makes the two serialise:
    -- without it a booking created at that instant would commit after the block
    -- and after the job list was taken, and would survive it.
    --
    -- The two directions THROW DIFFERENT codes so the API can word the refusal
    -- without leaking. Only the caller's OWN block may be named ("unblock to
    -- book"); one placed against them must read as a neutral "not available",
    -- because naming it would confirm the other party acted -- the one thing a
    -- block must never do. SQL reports which side acted and C# decides what this
    -- actor is allowed to know, because this procedure is called by BOTH hosts and
    -- has no actor of its own.
    --
    -- If the two have blocked each other, the parent's is reported. That is
    -- deterministic rather than meaningful: the pair is severed either way, and
    -- the only cost is that a provider in a mutual block reads the neutral wording
    -- instead of being told about their own block.
    DECLARE @BlockedByParent BIT = 0;
    DECLARE @BlockedByProvider BIT = 0;

    SELECT @BlockedByParent = MAX(CASE WHEN [BlockerType] = N'PetParent' THEN 1 ELSE 0 END),
           @BlockedByProvider = MAX(CASE WHEN [BlockerType] = N'Provider' THEN 1 ELSE 0 END)
    FROM [Block].[BlockedParticipants] WITH (HOLDLOCK)
    WHERE ([BlockerType] = N'PetParent' AND [BlockerId] = @PetParentId
           AND [BlockedType] = N'Provider' AND [BlockedId] = @ProviderId)
       OR ([BlockerType] = N'Provider' AND [BlockerId] = @ProviderId
           AND [BlockedType] = N'PetParent' AND [BlockedId] = @PetParentId);

    IF @BlockedByParent = 1
    BEGIN
        THROW 51370, 'The pet parent has blocked this provider.', 1;
    END

    IF @BlockedByProvider = 1
    BEGIN
        THROW 51371, 'The provider has blocked this pet parent.', 1;
    END

    -- Defense-in-depth: the API validates pet ownership before calling, but a
    -- direct sproc caller must not be able to pin someone else's pet on a stay.
    IF @PetId IS NOT NULL AND NOT EXISTS (
        SELECT 1
        FROM [Parent].[Pets]
        WHERE [PetId] = @PetId
          AND [PetParentId] = @PetParentId
          -- A soft-deleted pet can't be booked. The row survives only to keep
          -- EXISTING stays readable; it is not a pet the parent still has.
          AND [IsDeleted] = 0
    )
    BEGIN
        THROW 51233, 'Pet was not found or does not belong to the pet parent.', 1;
    END

    -- ServiceId must belong to the provider, be active, AND be a NightStay
    -- service. UPDLOCK + HOLDLOCK serialises us against DeactivateProviderService.
    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[ProviderServices] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [ProviderId] = @ProviderId
          AND [IsActive] = 1
          AND [ServiceType] = N'NightStay'
    )
    BEGIN
        THROW 51234, 'Service is not a valid, active NightStay service for this provider.', 1;
    END

    -- Reject a duplicate stay for the same pet: if this pet already has an active
    -- (non-cancelled) stay on THIS service whose date range overlaps
    -- [@CheckInDate, @CheckOutDate), block it — a pet can't board in two places at
    -- once. Ranges overlap when existing.CheckInDate < @CheckOutDate AND
    -- existing effective checkout > @CheckInDate (checkout day is not a stayed
    -- night). The existing stay's range ends at
    -- COALESCE([ActualCheckOutDate], [CheckOutDate]): once the pet has actually
    -- gone home it is free to board again on the nights that were released.
    -- Enforced under UPDLOCK + HOLDLOCK so a concurrent duplicate serialises.
    IF @PetId IS NOT NULL AND EXISTS (
        SELECT 1
        FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [PetId] = @PetId
          AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
          AND [CheckInDate] < @CheckOutDate
          AND COALESCE([ActualCheckOutDate], [CheckOutDate]) > @CheckInDate
    )
    BEGIN
        THROW 51239, 'This pet already has a booking for these dates.', 1;
    END

    -- Per-night capacity check. Enumerate every stayed night in
    -- [@CheckInDate, @CheckOutDate) and count active bookings whose range
    -- covers that night (existing.CheckInDate <= night < existing effective
    -- checkout). A stay that ended EARLY covers nights only up to
    -- COALESCE([ActualCheckOutDate], [CheckOutDate]), so the nights it gave back
    -- are genuinely bookable here — matching what
    -- [Booking].[GetNightStayOccupancy] showed the parent.
    -- UPDLOCK + HOLDLOCK serialises concurrent creates on this service so the
    -- (N+1)-th overlapping stay is rejected once a night is full.
    DECLARE @FullNight DATE;

    ;WITH [Nights] AS
    (
        SELECT @CheckInDate AS [Night]
        UNION ALL
        SELECT DATEADD(DAY, 1, [Night])
        FROM [Nights]
        WHERE DATEADD(DAY, 1, [Night]) < @CheckOutDate
    )
    SELECT TOP (1) @FullNight = n.[Night]
    FROM [Nights] n
    LEFT JOIN [Booking].[NightStayBookings] b WITH (UPDLOCK, HOLDLOCK)
        ON b.[ServiceId] = @ServiceId
       AND b.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
       AND b.[CheckInDate] <= n.[Night]
       AND COALESCE(b.[ActualCheckOutDate], b.[CheckOutDate]) > n.[Night]
    GROUP BY n.[Night]
    HAVING COUNT(b.[NightStayBookingId]) >= @Capacity
    OPTION (MAXRECURSION 366);

    IF @FullNight IS NOT NULL
    BEGIN
        THROW 51235, 'No remaining capacity for one or more nights in the stay.', 1;
    END

    -- Snapshot the provider's current cancellation policy so a later policy change
    -- never re-rules this stay. NULL = no restriction (itself a valid snapshot).
    DECLARE @CancellationPolicyHours INT =
        (SELECT [MinimumHoursBeforeCancellation]
         FROM [Provider].[ProviderCancellationPolicies]
         WHERE [ProviderId] = @ProviderId);

    -- Snapshot the SELECTED service-location address (see [Booking].[CreateBooking]).
    DECLARE @SnapshotAddressLine NVARCHAR(500) = NULL;
    DECLARE @SnapshotCity NVARCHAR(200) = NULL;
    DECLARE @SnapshotZipCode NVARCHAR(32) = NULL;
    DECLARE @SnapshotLatitude DECIMAL(9, 6) = NULL;
    DECLARE @SnapshotLongitude DECIMAL(9, 6) = NULL;

    IF @LocationType = N'ParentLocation'
    BEGIN
        SELECT @SnapshotAddressLine = [AddressLine],
               @SnapshotCity        = [City],
               @SnapshotZipCode     = [ZipCode],
               @SnapshotLatitude    = [Latitude],
               @SnapshotLongitude   = [Longitude]
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId;
    END
    ELSE IF @LocationType = N'ProviderLocation'
    BEGIN
        SET @SnapshotAddressLine = @SnapshotProviderAddressLine;
        SET @SnapshotCity        = @SnapshotProviderCity;
        SET @SnapshotZipCode     = @SnapshotProviderZipCode;
        SET @SnapshotLatitude    = @SnapshotProviderLatitude;
        SET @SnapshotLongitude   = @SnapshotProviderLongitude;
    END

    DECLARE @InsertedId TABLE ([NightStayBookingId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[NightStayBookings]
    (
        [ProviderId],
        [PetParentId],
        [PetId],
        [ServiceId],
        [ServiceCategory],
        [SubCategory],
        [CheckInDate],
        [CheckOutDate],
        [DropOffTime],
        [PickUpTime],
        [JobNotes],
        [LocationType],
        [PricePerNight],
        [CancellationPolicyHours],
        [SnapshotAddressLine],
        [SnapshotCity],
        [SnapshotZipCode],
        [SnapshotLatitude],
        [SnapshotLongitude]
    )
    OUTPUT inserted.[NightStayBookingId] INTO @InsertedId
    VALUES
    (
        @ProviderId,
        @PetParentId,
        @PetId,
        @ServiceId,
        @ServiceCategory,
        @SubCategory,
        @CheckInDate,
        @CheckOutDate,
        @DropOffTime,
        @PickUpTime,
        @JobNotes,
        @LocationType,
        @PricePerNight,
        @CancellationPolicyHours,
        @SnapshotAddressLine,
        @SnapshotCity,
        @SnapshotZipCode,
        @SnapshotLatitude,
        @SnapshotLongitude
    );

    DECLARE @NightStayBookingId UNIQUEIDENTIFIER =
        (SELECT TOP (1) [NightStayBookingId] FROM @InsertedId);

    -- Seed the audit trail with the creation entry (Status defaults to CREATED).
    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, NULL, N'CREATED', N'System', NULL, N'Night stay booking created');

    SELECT [NightStayBookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [CheckInDate],
           [CheckOutDate],
           [DropOffTime],
           [PickUpTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Booking].[CreateNightStayBooking].';
GO

CREATE OR ALTER PROCEDURE [Booking].[GetNightStayBooking]
    @NightStayBookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [NightStayBookingId], [ProviderId], [PetParentId], [ServiceId], [ServiceCategory],
           [SubCategory], [CheckInDate], [CheckOutDate], [DropOffTime], [PickUpTime],
           [Status], [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc], [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;
END;
GO
PRINT 'Created/updated [Booking].[GetNightStayBooking].';
GO

-- Enriched night-stay read backing the night-stay booking-detail endpoint.
-- Mirrors [Booking].[GetBookingDetail]: base columns PLUS JobNumber, payout
-- fields, and the joined pet-parent / pet records. Night-stay is App-only, so
-- the customer + pet details always come from the joins.
CREATE OR ALTER PROCEDURE [Booking].[GetNightStayBookingDetail]
    @NightStayBookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT b.[NightStayBookingId],
           b.[JobNumber],
           b.[ProviderId],
           b.[PetParentId],
           b.[ServiceId],
           b.[ServiceCategory],
           b.[SubCategory],
           b.[CheckInDate],
           b.[CheckOutDate],
           b.[DropOffTime],
           b.[PickUpTime],
           b.[Status],
           b.[CreatedAtUtc],
           b.[UpdatedAtUtc],
           b.[CancelledAtUtc],
           b.[PetId],
           b.[PayoutStatus],
           b.[PayoutId],
           b.[PricePerNight],
           pp.[FirstName]         AS [ParentFirstName],
           pp.[LastName]          AS [ParentLastName],
           pp.[Gender]            AS [ParentGender],
           pp.[MobileCountryCode] AS [ParentMobileCountryCode],
           pp.[MobileNumber]      AS [ParentMobileNumber],
           pp.[ProfilePhotoUrl]   AS [ParentPhotoUrl],
           pet.[PetName]          AS [PetProfileName],
           pet.[PetType]          AS [PetType],
           pet.[Gender]           AS [PetGender],
           pet.[ProfilePhotoUrl]  AS [PetPhotoUrl],
           prov.[FirstName]         AS [ProviderFirstName],
           prov.[LastName]          AS [ProviderLastName],
           prov.[Gender]            AS [ProviderGender],
           prov.[MobileCountryCode] AS [ProviderMobileCountryCode],
           prov.[MobileNumber]      AS [ProviderMobileNumber],
           pet.[Breed]              AS [PetBreed],
           pet.[VaccinationStatus]  AS [PetVaccinationStatus],
           pet.[VaccinationType]    AS [PetVaccinationType],
           pet.[VaccinationDose]    AS [PetVaccinationDose],
           pet.[Prescription]       AS [PetPrescription],
           pet.[SterilizationStatus] AS [PetSterilizationStatus],
           pet.[MedicalHistory]      AS [PetMedicalHistory],
           pet.[Temperament]         AS [PetTemperament],
           b.[JobNotes],
           b.[LocationType],
           pp.[AddressLine]         AS [ParentAddressLine],
           pp.[City]                AS [ParentCity],
           pp.[ZipCode]             AS [ParentZipCode],
           pp.[Latitude]            AS [ParentLatitude],
           pp.[Longitude]           AS [ParentLongitude],
           -- Snapshots captured at booking time; the detail read PREFERS these over
           -- the live provider policy / resolved address. Appended LAST for stable ordinals.
           b.[CancellationPolicyHours],
           b.[SnapshotAddressLine],
           b.[SnapshotCity],
           b.[SnapshotZipCode],
           b.[SnapshotLatitude],
           b.[SnapshotLongitude],
           -- Payment ledger join. HOW the money changed hands ('Cash'/'Digital')
           -- is recorded only on the ledger row, never on the booking. Both
           -- columns stay NULL until the provider marks the stay PAID. Appended
           -- LAST so existing reader ordinals stay stable.
           pay.[PaymentMethod] AS [PayoutMethod],
           pay.[PaidAtUtc]
    FROM [Booking].[NightStayBookings] AS b
    LEFT JOIN [Parent].[PetParents] AS pp
        ON pp.[PetParentId] = b.[PetParentId]
    LEFT JOIN [Parent].[Pets] AS pet
        ON pet.[PetId] = b.[PetId]
    LEFT JOIN [Provider].[Providers] AS prov
        ON prov.[ProviderId] = b.[ProviderId]
    -- BookingType discriminates which booking table BookingId points at — the
    -- ledger is shared by single-day and night-stay bookings and has no FK.
    LEFT JOIN [Booking].[BookingPayments] AS pay
        ON pay.[BookingId] = b.[NightStayBookingId] AND pay.[BookingType] = N'NightStay'
    WHERE b.[NightStayBookingId] = @NightStayBookingId;
END;
GO
PRINT 'Created/updated [Booking].[GetNightStayBookingDetail].';
GO

-- Parent-initiated cancel. Sets PARENT_CANCELLED + frees the per-night capacity.
-- THROWs: 51236 not found, 51237 not the booker, 51238 already cancelled.
CREATE OR ALTER PROCEDURE [Booking].[CancelNightStayBooking]
    @NightStayBookingId UNIQUEIDENTIFIER,
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
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51236, 'Night stay booking was not found.', 1;
    END

    IF @CurrentParent <> @PetParentId
    BEGIN
        THROW 51237, 'Only the original booker can cancel this booking.', 1;
    END

    IF @CurrentStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
    BEGIN
        THROW 51238, 'Night stay booking is already cancelled.', 1;
    END

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'PARENT_CANCELLED',
        [CancelledAtUtc] = @Now,
        [UpdatedAtUtc] = @Now
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, N'PARENT_CANCELLED', N'Parent', @PetParentId, NULL);

    SELECT [NightStayBookingId], [ProviderId], [PetParentId], [ServiceId], [ServiceCategory],
           [SubCategory], [CheckInDate], [CheckOutDate], [DropOffTime], [PickUpTime],
           [Status], [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc], [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Booking].[CancelNightStayBooking].';
GO

-- Moves a night-stay booking to a new lifecycle status + writes an audit row,
-- atomically. Settable per actor: Provider -> CONFIRMED/COMPLETED/APPROVAL_NEEDED/
-- PROVIDER_CANCELLED; Parent -> APPROVAL_NEEDED/COMPLETED/PARENT_CANCELLED.
-- THROWs: 51240 not found, 51241 not a party, 51242 not allowed for actor,
-- 51243 terminal, 51244 unchanged, 51245 invalid actor/status value,
-- 51246 transition not allowed from the current status,
-- 51248 no-show reported earlier than 30 minutes after check-in + drop-off,
-- 51249 booking expired (CREATED for 24+ hours - no longer acceptable). REJECT
--        ONLY: the EXPIRED status is written by the scheduled external job, not
--        here; this sproc never changes status on the basis of elapsed time.
-- 51273 booking expired (still CREATED with under 2 hours to check-in +
--        drop-off - see BR-53). Also REJECT ONLY, same reasoning as 51249.
CREATE OR ALTER PROCEDURE [Booking].[UpdateNightStayBookingStatus]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @NewStatus NVARCHAR(48),
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @Note NVARCHAR(500) = NULL,
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Actor NOT IN (N'Provider', N'Parent')
    BEGIN
        THROW 51245, 'Actor must be Provider or Parent.', 1;
    END

    IF @NewStatus NOT IN (N'CONFIRMED', N'PROVIDER_DECLINED', N'COMPLETED',
                          N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                          N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
    BEGIN
        THROW 51245, 'Unknown or non-engine booking status.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @CheckInDate DATE;
    DECLARE @CheckOutDate DATE;
    DECLARE @DropOffTime TIME(0);
    DECLARE @CreatedAtUtc DATETIME2(7);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status],
           @ProviderId = [ProviderId],
           @PetParentId = [PetParentId],
           @CheckInDate = [CheckInDate],
           @CheckOutDate = [CheckOutDate],
           @DropOffTime = [DropOffTime],
           @CreatedAtUtc = [CreatedAtUtc]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51240, 'Night stay booking was not found.', 1;
    END

    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51241, 'You are not a party to this booking.', 1;
    END

    -- A booking left pending (CREATED) for 24+ hours has expired: reject the
    -- attempted transition — the provider can no longer accept it.
    -- REJECT ONLY: the EXPIRED status is deliberately NOT written here. Status
    -- changes driven by elapsed time belong to the scheduled external job, which
    -- is the single writer for them; this guard just stops a late accept from
    -- slipping through before that job runs. The row therefore stays in CREATED
    -- until the job settles it.
    IF @CurrentStatus = N'CREATED' AND @Now >= DATEADD(HOUR, 24, @CreatedAtUtc)
    BEGIN
        THROW 51249, 'Booking has expired after 24 hours awaiting provider acceptance and can no longer change.', 1;
    END

    -- BR-53 (mirror of the single-day 51153 guard): a stay still in CREATED with
    -- under 2 hours to serviceStart — CheckInDate + DropOffTime for a stay — has
    -- expired; the provider is out of time to accept it. REJECT ONLY: the
    -- scheduled external job is the single writer of EXPIRED, so the row stays in
    -- CREATED until it runs.
    IF @CurrentStatus = N'CREATED'
       AND DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @DropOffTime),
                   CAST(@CheckInDate AS DATETIME2(7))) < DATEADD(HOUR, 2, @Now)
    BEGIN
        THROW 51273, 'Booking has expired: it was never accepted and the stay now begins in under 2 hours.', 1;
    END

    -- A no-show always names the OTHER party: the provider reports the parent's
    -- no-show, the parent reports the provider's.
    IF (@Actor = N'Provider'
            AND @NewStatus NOT IN (N'CONFIRMED', N'PROVIDER_DECLINED', N'COMPLETED', N'PROVIDER_CANCELLED', N'PARENT_NO_SHOW'))
       OR (@Actor = N'Parent'
            AND @NewStatus NOT IN (N'PARENT_CANCELLED', N'PROVIDER_NO_SHOW'))
    BEGIN
        THROW 51242, 'This status is not permitted for this actor.', 1;
    END

    IF @CurrentStatus IN (N'COMPLETED', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                          N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
    BEGIN
        THROW 51243, 'Booking is in a terminal state and cannot change.', 1;
    END

    IF @CurrentStatus = @NewStatus
    BEGIN
        THROW 51244, 'Booking is already in the requested status.', 1;
    END

    IF (@NewStatus = N'CONFIRMED'           AND @CurrentStatus <> N'CREATED')
       OR (@NewStatus = N'PROVIDER_DECLINED' AND @CurrentStatus <> N'CREATED')
       OR (@NewStatus = N'COMPLETED'        AND @CurrentStatus NOT IN (N'IN_PROGRESS', N'ENDING'))
       OR (@NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
           AND @CurrentStatus NOT IN (N'CONFIRMED',
                                      N'PROVIDER_ACCEPTED_MODIFICATION', N'PARENT_ACCEPTED_MODIFICATION',
                                      N'PROVIDER_DECLINED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                                      N'START_JOB'))
    BEGIN
        THROW 51246, 'This transition is not allowed from the current status.', 1;
    END

    -- A cancel is allowed from any non-terminal state EXCEPT once the job is
    -- actively underway (IN_PROGRESS; the retired ENDING kept for legacy rows)
    -- — by then it runs to completion.
    IF @NewStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED')
       AND @CurrentStatus IN (N'IN_PROGRESS', N'ENDING')
    BEGIN
        THROW 51269, 'The job is already in progress and can no longer be cancelled.', 1;
    END

    -- A no-show can only be reported once the counterparty is actually late:
    -- 2 HOURS past the stay's scheduled check-in (check-in date + drop-off
    -- time; all times are UTC). A boarding hand-over is a slower affair than a
    -- single-day appointment, whose gate stays at 30 minutes — so a 09:00
    -- check-in is reportable from 11:00.
    --
    -- Neither party has to wait it out: if the stay is still unstarted when the
    -- check-in day ends, the scheduled external job settles it automatically at
    -- midnight UTC (START_JOB -> PARENT_NO_SHOW, since the provider was there
    -- and issued the code; anything else -> PROVIDER_NO_SHOW, since they never
    -- even tapped Start). That settlement no longer happens in this database.
    IF @NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
    BEGIN
        DECLARE @StartsAtUtc DATETIME2(7) =
            DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @DropOffTime),
                    CAST(@CheckInDate AS DATETIME2(7)));
        IF @Now < DATEADD(HOUR, 2, @StartsAtUtc)
        BEGIN
            THROW 51248, 'A no-show can only be reported 2 hours after the stay''s scheduled check-in.', 1;
        END
    END

    -- COMPLETED is reachable here through the legacy /status shim as well as
    -- through [Booking].[CompleteNightStayBooking], so an early pickup must
    -- release the remaining nights from BOTH paths — otherwise which endpoint
    -- the provider happened to tap would decide whether the nights came back.
    -- Same rule and clamps as the dedicated sproc; see it for the reasoning.
    DECLARE @Today DATE = CAST(@Now AS DATE);
    DECLARE @ActualCheckOutDate DATE = NULL;

    IF @NewStatus = N'COMPLETED' AND @Today < @CheckOutDate
    BEGIN
        SET @ActualCheckOutDate =
            CASE WHEN @Today <= @CheckInDate THEN DATEADD(DAY, 1, @CheckInDate) ELSE @Today END;
    END

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = @NewStatus,
        [UpdatedAtUtc] = @Now,
        -- Only ever written on the COMPLETED transition; every other status
        -- leaves whatever is there alone.
        [ActualCheckOutDate] = CASE
            WHEN @NewStatus = N'COMPLETED' THEN @ActualCheckOutDate
            ELSE [ActualCheckOutDate]
        END,
        -- Mirror of Booking.UpdateBookingStatus: a no-show settles the payout as
        -- 'NO_PAYOUT' instead of leaving it reading 'Pending' forever.
        [PayoutStatus] = CASE
            WHEN @NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW') THEN N'NO_PAYOUT'
            ELSE [PayoutStatus]
        END,
        [CancelledAtUtc] = CASE
            WHEN @NewStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED') THEN @Now
            ELSE [CancelledAtUtc]
        END
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

    -- Where the reporting party was when they marked the counterparty absent.
    IF @NewStatus IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW')
       AND @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[NightStayBookingLocationEvents]
            ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@NightStayBookingId, N'NoShowMarked', @Actor, @ActorId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    -- Mirror of Booking.UpdateBookingStatus: notify the OTHER party, in this
    -- transaction, never the actor who tapped it.
    DECLARE @NotificationType NVARCHAR(64) =
        CASE @NewStatus
            WHEN N'CONFIRMED'          THEN N'BOOKING_ACCEPTED'
            WHEN N'PROVIDER_DECLINED'  THEN N'BOOKING_DECLINED'
            WHEN N'PROVIDER_CANCELLED' THEN N'BOOKING_CANCELLED_BY_PROVIDER'
            WHEN N'PARENT_CANCELLED'   THEN N'BOOKING_CANCELLED_BY_PARENT'
            WHEN N'COMPLETED'          THEN N'BOOKING_COMPLETED'
            WHEN N'PARENT_NO_SHOW'     THEN N'BOOKING_NO_SHOW_REPORTED'
            WHEN N'PROVIDER_NO_SHOW'   THEN N'BOOKING_NO_SHOW_REPORTED'
        END;

    IF @NotificationType IS NOT NULL
    BEGIN
        DECLARE @AbsentParty NVARCHAR(32) =
            CASE @NewStatus
                WHEN N'PARENT_NO_SHOW'   THEN N'the customer'
                WHEN N'PROVIDER_NO_SHOW' THEN N'the provider'
            END;

        DECLARE @Audience NVARCHAR(16) =
            CASE WHEN @Actor = N'Provider' THEN N'PetParent' ELSE N'Provider' END;

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @NightStayBookingId,
            @IsNightStay = 1,
            @Audience = @Audience,
            @NotificationType = @NotificationType,
            @AbsentParty = @AbsentParty;
    END

    SELECT [NightStayBookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [CheckInDate],
           [CheckOutDate],
           [DropOffTime],
           [PickUpTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Booking].[UpdateNightStayBookingStatus].';
GO


-- Retired (2026-08-02): the periodic booking expiry sweep used to live here as
-- ONE sproc, [Booking].[ExpireStaleBookings], run every 10 minutes by an
-- in-process hosted service in BOTH API hosts (a real bug -- two uncoordinated
-- instances sweeping the same rows). It has been replaced by THREE separate
-- sprocs below (Booking.ExpireStaleCreatedBookings / RevertExpiredParent-
-- ModificationRequests / SettleUnstartedJobsAsNoShow -- see docs/booking-rules.md
-- BR-17/BR-30/BR-38), called in that order every 5 minutes by a single Azure
-- Functions timer trigger (src/Pawfront.Functions) -- one dedicated app, so the
-- Functions runtime's own distributed timer lock rules out the double-sweep bug
-- without any extra code here.
--
-- Nothing in this script changes a booking's status on the basis of elapsed time
-- any more. The remaining time checks, in the status-engine and
-- modification-respond sprocs, REJECT a late transition (THROW 51129 / 51249 /
-- 51152 / 51272) without writing anything; the three sprocs below are the only
-- writers of time-driven status changes.
--
-- The old sproc is DROPPED rather than left in place so a stray caller -- an
-- older host build still running the retired hosted service, a leftover Agent /
-- Elastic Job step -- cannot keep flipping statuses behind the new job's back.
--
-- Statuses the sweep used to produce (EXPIRED, JOB_EXPIRED, PARENT_NO_SHOW,
-- PROVIDER_NO_SHOW) remain valid and stay in every CHECK constraint and
-- capacity-freeing predicate below: existing rows still carry them, and the
-- three sprocs below write the same values.
IF OBJECT_ID(N'[Booking].[ExpireStaleBookings]', N'P') IS NOT NULL
BEGIN
    DROP PROCEDURE [Booking].[ExpireStaleBookings];
    PRINT 'Dropped [Booking].[ExpireStaleBookings]; booking expiry now runs outside the database.';
END
GO

-- A booking still sitting in CREATED -- nobody has accepted it -- expires on
-- EITHER of two triggers, both of which mean the same thing: the provider has
-- run out of time to accept.
--   * BR-17: it has been pending for @PendingHours (default 24).
--   * BR-53: the service now starts in under @LeadTimeHours (default 2), i.e.
--     serviceStart < @Now + @LeadTimeHours. That is the SAME cutoff BR-01 uses
--     to decide a booking is too soon to be created, so an unaccepted booking
--     dies exactly when a fresh one for that slot could no longer be made.
--     serviceStart is BookingDate + StartTime (single-day) and
--     CheckInDate + DropOffTime (night stay), matching the modification-window
--     and lead-time arithmetic elsewhere. Comparison is strict (<), so a
--     booking whose service is exactly @LeadTimeHours away survives this tick --
--     mirroring BookingLeadTime.IsTooSoon.
-- A booking already past its start time is caught by the same test.
--
-- Run periodically by the scheduled external job (Azure Function, timer-
-- triggered, replaces the retired in-database Booking.ExpireStaleBookings sweep
-- -- see docs/booking-rules.md). Idempotent and race-safe: the UPDATE only
-- touches rows still in CREATED, so a concurrent accept on the same row
-- serialises on the row lock and one of the two loses.
-- The status-engine sprocs (UpdateBookingStatus / UpdateNightStayBookingStatus)
-- REJECT an accept attempted on a booking that either trigger has caught
-- (THROW 51129 / 51153 and their night-stay mirrors 51249 / 51273) without
-- writing anything -- this sproc is the only writer of EXPIRED.
-- Returns one row: (ExpiredBookings, ExpiredNightStayBookings).
CREATE OR ALTER PROCEDURE [Booking].[ExpireStaleCreatedBookings]
    @PendingHours INT = 24,
    @LeadTimeHours INT = 2
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @PendingCutoff DATETIME2(7) = DATEADD(HOUR, -@PendingHours, @Now);
    DECLARE @LeadTimeCutoff DATETIME2(7) = DATEADD(HOUR, @LeadTimeHours, @Now);
    DECLARE @PendingNote NVARCHAR(500) =
        N'Automatically expired after ' + CAST(@PendingHours AS NVARCHAR(8))
        + N' hours awaiting provider acceptance.';
    DECLARE @LeadTimeNote NVARCHAR(500) =
        N'Automatically expired: never accepted, and the service now starts in under '
        + CAST(@LeadTimeHours AS NVARCHAR(8)) + N' hours.';

    -- [Reason] records WHICH trigger fired, so the audit row explains itself.
    -- The pending trigger wins when both apply â€” it is the older claim on the row.
    DECLARE @ExpiredBookings TABLE (
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [Reason] NVARCHAR(16) NOT NULL);
    DECLARE @ExpiredNightStays TABLE (
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
        [Reason] NVARCHAR(16) NOT NULL);

    BEGIN TRANSACTION;

    UPDATE [Booking].[Bookings]
    SET [Status] = N'EXPIRED',
        -- Never accepted, so no money can ever move on it. This job is the ONLY
        -- writer of EXPIRED (the status-engine sprocs reject a late transition
        -- without writing), so it is the only place to settle the payout.
        [PayoutStatus] = N'NO_PAYOUT',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[BookingId],
           CASE WHEN deleted.[CreatedAtUtc] <= @PendingCutoff THEN N'Pending' ELSE N'LeadTime' END
    INTO @ExpiredBookings ([BookingId], [Reason])
    WHERE [Status] = N'CREATED'
      AND ([CreatedAtUtc] <= @PendingCutoff
           OR DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), [StartTime]),
                      CAST([BookingDate] AS DATETIME2(7))) < @LeadTimeCutoff);

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [BookingId], N'CREATED', N'EXPIRED', N'System', NULL,
           CASE WHEN [Reason] = N'Pending' THEN @PendingNote ELSE @LeadTimeNote END
    FROM @ExpiredBookings;

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'EXPIRED',
        [PayoutStatus] = N'NO_PAYOUT',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[NightStayBookingId],
           CASE WHEN deleted.[CreatedAtUtc] <= @PendingCutoff THEN N'Pending' ELSE N'LeadTime' END
    INTO @ExpiredNightStays ([NightStayBookingId], [Reason])
    WHERE [Status] = N'CREATED'
      AND ([CreatedAtUtc] <= @PendingCutoff
           OR DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), [DropOffTime]),
                      CAST([CheckInDate] AS DATETIME2(7))) < @LeadTimeCutoff);

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [NightStayBookingId], N'CREATED', N'EXPIRED', N'System', NULL,
           CASE WHEN [Reason] = N'Pending' THEN @PendingNote ELSE @LeadTimeNote END
    FROM @ExpiredNightStays;

    -- ONE expiry event, TWO notifications (V3 cards P-S1 + V-S2) â€” the parent is
    -- told to re-book and lands on the booking; the provider is told they lost the
    -- job and lands on Payouts -> Ignored Jobs. Deliberately a single trigger with
    -- two recipients rather than two independent timers, so the two can never
    -- disagree about whether the booking expired.
    DECLARE @BookingId UNIQUEIDENTIFIER;
    DECLARE @IsNightStay BIT;

    DECLARE expired_bookings CURSOR LOCAL FAST_FORWARD FOR
        SELECT [BookingId], 0 FROM @ExpiredBookings
        UNION ALL
        SELECT [NightStayBookingId], 1 FROM @ExpiredNightStays;

    OPEN expired_bookings;
    FETCH NEXT FROM expired_bookings INTO @BookingId, @IsNightStay;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = N'PetParent',
            @NotificationType = N'BOOKING_EXPIRED_FOR_PARENT';

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = N'Provider',
            @NotificationType = N'BOOKING_EXPIRED_FOR_PROVIDER';

        FETCH NEXT FROM expired_bookings INTO @BookingId, @IsNightStay;
    END

    CLOSE expired_bookings;
    DEALLOCATE expired_bookings;

    COMMIT TRANSACTION;

    SELECT
        (SELECT COUNT(*) FROM @ExpiredBookings) AS [ExpiredBookings],
        (SELECT COUNT(*) FROM @ExpiredNightStays) AS [ExpiredNightStayBookings];
END;
GO
PRINT 'Created/updated [Booking].[ExpireStaleCreatedBookings].';
GO

-- Retired (2026-08-02): [Booking].[RevertExpiredParentModificationRequests] is
-- replaced by [Booking].[RevertExpiredModificationRequests] below, now that the
-- rule covers BOTH proposal directions -- the old name's "Parent" would have
-- been misleading once MODIFICATION_REQUEST_BY_PROVIDER rows are reverted here
-- too. Dropped rather than left in place so a stray caller can't keep acting on
-- the old parent-only behavior.
IF OBJECT_ID(N'[Booking].[RevertExpiredParentModificationRequests]', N'P') IS NOT NULL
BEGIN
    DROP PROCEDURE [Booking].[RevertExpiredParentModificationRequests];
    PRINT 'Dropped [Booking].[RevertExpiredParentModificationRequests]; replaced by RevertExpiredModificationRequests.';
END
GO

-- BR-30 (widened 2026-08-02 to cover BOTH proposal directions -- previously
-- PARENT-only, see the retired BR-31 note in docs/booking-rules.md): an
-- unanswered modification proposal, from EITHER party, expires 2 hours before
-- the service starts (BookingDate + StartTime for a single-day booking,
-- CheckInDate + DropOffTime for a stay; all UTC) -- the staging row is discarded
-- and the booking REVERTS to CONFIRMED (NOT terminal: it goes back into a
-- startable state so neither party is left blocked by a stale proposal).
--
-- Run periodically by the scheduled external job, BEFORE
-- Booking.SettleUnstartedJobsAsNoShow in the same tick -- a booking whose
-- proposal expires AND whose provider's working day has also ended should
-- settle as a no-show in that same pass rather than waiting for the next one.
--
-- The status-engine's respond sprocs (RespondBookingModification /
-- RespondNightStayBookingModification) REJECT a response landing after the
-- cutoff (THROW 51152 / 51272) without performing the revert -- this sproc is
-- the only writer of the CONFIRMED revert + the staging-row delete.
-- Returns one row: (RevertedBookings, RevertedNightStayBookings).
CREATE OR ALTER PROCEDURE [Booking].[RevertExpiredModificationRequests]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    DECLARE @TimedOutNote NVARCHAR(500) =
        N'Modification request timed out: 24 hours passed with no response.';
    DECLARE @CutoffNote NVARCHAR(500) =
        N'Modification request expired unanswered 2 hours before the service start time.';

    -- FromStatus is captured per row (not a hardcoded literal) since a reverted
    -- booking may have come from either MODIFICATION_REQUEST_BY_PARENT or
    -- MODIFICATION_REQUEST_BY_PROVIDER. [Arm] records WHICH deadline fired.
    DECLARE @RevertedBookings TABLE (
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [FromStatus] NVARCHAR(48) NOT NULL,
        [Arm] NVARCHAR(16) NOT NULL);
    DECLARE @RevertedNightStays TABLE (
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
        [FromStatus] NVARCHAR(48) NOT NULL,
        [Arm] NVARCHAR(16) NOT NULL);

    BEGIN TRANSACTION;

    -- --- Single-day -------------------------------------------------------
    -- The arm is decided BEFORE the update, from the staging row's age, because
    -- the staging row is deleted below and OUTPUT cannot see the joined table.
    -- A booking whose 24h lapsed AND whose service is under 2h away reports
    -- 'Cutoff': it is the more specific explanation, and the one the app's own
    -- "modifications close 2 hours before" copy already tells the user about.
    DECLARE @ExpiredSingleDay TABLE (
        [BookingId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        [Arm] NVARCHAR(16) NOT NULL);

    INSERT INTO @ExpiredSingleDay ([BookingId], [Arm])
    SELECT b.[BookingId],
           CASE
               WHEN @Now >= DATEADD(HOUR, -2,
                        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), b.[StartTime]),
                                CAST(b.[BookingDate] AS DATETIME2(7))))
               THEN N'Cutoff'
               ELSE N'TimedOut'
           END
    FROM [Booking].[Bookings] b
    INNER JOIN [Booking].[BookingModifications] m ON m.[BookingId] = b.[BookingId]
    WHERE b.[Status] IN (N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER')
      AND (
            -- 2-hour pre-service cutoff
            @Now >= DATEADD(HOUR, -2,
                DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), b.[StartTime]),
                        CAST(b.[BookingDate] AS DATETIME2(7))))
            -- 24-hour review window
            OR @Now >= DATEADD(HOUR, 24, m.[CreatedAtUtc])
          );

    UPDATE b
    SET [Status] = N'CONFIRMED',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[BookingId], deleted.[Status], e.[Arm] INTO @RevertedBookings
    FROM [Booking].[Bookings] b
    INNER JOIN @ExpiredSingleDay e ON e.[BookingId] = b.[BookingId];

    DELETE m
    FROM [Booking].[BookingModifications] m
    INNER JOIN @RevertedBookings r ON r.[BookingId] = m.[BookingId];

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [BookingId], [FromStatus], N'CONFIRMED', N'System', NULL,
           CASE WHEN [Arm] = N'Cutoff' THEN @CutoffNote ELSE @TimedOutNote END
    FROM @RevertedBookings;

    -- --- Night stay -------------------------------------------------------
    DECLARE @ExpiredNightStay TABLE (
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        [Arm] NVARCHAR(16) NOT NULL);

    INSERT INTO @ExpiredNightStay ([NightStayBookingId], [Arm])
    SELECT n.[NightStayBookingId],
           CASE
               WHEN @Now >= DATEADD(HOUR, -2,
                        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                                CAST(n.[CheckInDate] AS DATETIME2(7))))
               THEN N'Cutoff'
               ELSE N'TimedOut'
           END
    FROM [Booking].[NightStayBookings] n
    INNER JOIN [Booking].[NightStayBookingModifications] m
        ON m.[NightStayBookingId] = n.[NightStayBookingId]
    WHERE n.[Status] IN (N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER')
      AND (
            @Now >= DATEADD(HOUR, -2,
                DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                        CAST(n.[CheckInDate] AS DATETIME2(7))))
            OR @Now >= DATEADD(HOUR, 24, m.[CreatedAtUtc])
          );

    UPDATE n
    SET [Status] = N'CONFIRMED',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[NightStayBookingId], deleted.[Status], e.[Arm] INTO @RevertedNightStays
    FROM [Booking].[NightStayBookings] n
    INNER JOIN @ExpiredNightStay e ON e.[NightStayBookingId] = n.[NightStayBookingId];

    DELETE m
    FROM [Booking].[NightStayBookingModifications] m
    INNER JOIN @RevertedNightStays r ON r.[NightStayBookingId] = m.[NightStayBookingId];

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [NightStayBookingId], [FromStatus], N'CONFIRMED', N'System', NULL,
           CASE WHEN [Arm] = N'Cutoff' THEN @CutoffNote ELSE @TimedOutNote END
    FROM @RevertedNightStays;

    -- --- Notify BOTH parties ----------------------------------------------
    -- Nobody tapped anything here â€” the system decided it â€” so the relevance rule
    -- puts it on both apps (V3 cards P-S2/V-S3 for the 24-hour arm, P-S3/V-S4 for
    -- the 2-hour cutoff). Cursor rather than a set-based insert because
    -- EnqueueBookingNotification is a sproc: it builds the whole data object per
    -- booking, and duplicating that JSON here is exactly the drift the helper
    -- exists to prevent. Volumes are single-digit per tick.
    DECLARE @BookingId UNIQUEIDENTIFIER;
    DECLARE @Arm NVARCHAR(16);
    DECLARE @Type NVARCHAR(64);
    DECLARE @IsNightStay BIT;

    DECLARE expired_modifications CURSOR LOCAL FAST_FORWARD FOR
        SELECT [BookingId], [Arm], 0 FROM @RevertedBookings
        UNION ALL
        SELECT [NightStayBookingId], [Arm], 1 FROM @RevertedNightStays;

    OPEN expired_modifications;
    FETCH NEXT FROM expired_modifications INTO @BookingId, @Arm, @IsNightStay;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @Type = CASE WHEN @Arm = N'Cutoff'
                         THEN N'BOOKING_MODIFICATION_EXPIRED'
                         ELSE N'BOOKING_MODIFICATION_TIMED_OUT' END;

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = N'PetParent',
            @NotificationType = @Type;

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = N'Provider',
            @NotificationType = @Type;

        FETCH NEXT FROM expired_modifications INTO @BookingId, @Arm, @IsNightStay;
    END

    CLOSE expired_modifications;
    DEALLOCATE expired_modifications;

    COMMIT TRANSACTION;

    SELECT
        (SELECT COUNT(*) FROM @RevertedBookings) AS [RevertedBookings],
        (SELECT COUNT(*) FROM @RevertedNightStays) AS [RevertedNightStayBookings];
END;
GO
PRINT 'Created/updated [Booking].[RevertExpiredModificationRequests].';
GO

-- BR-38 (revised 2026-08-02): an accepted job that is still unstarted once the
-- PROVIDER'S WORKING DAY ends settles itself as a no-show. Blame is read from
-- the only evidence the system has, not guessed:
--   * sitting in START_JOB      -> PARENT_NO_SHOW  (the provider was there and
--     had the start code issued to the parent, who never handed it back)
--   * still confirmed-equivalent -> PROVIDER_NO_SHOW (the provider never so
--     much as tapped Start)
--
-- Single-day cutoff = the LATER of (a) the provider's closing time on the
-- booking date, from Provider.ProviderWeeklyAvailability for that weekday, and
-- (b) the booking's own EndTime. Keying off closing time (not the booking's own
-- end) is the point: a provider running badly late has not no-showed just
-- because a slot came and went — they still have the rest of their day to serve
-- it. Taking the LATER of the two guards against a provider who has narrowed
-- their hours since the booking was made: a booking is never settled while its
-- own window is still running. No weekly-availability row for that weekday, or
-- one marked closed, falls back to midnight UTC at the end of the booking date
-- (the calendar day is the only "day" there is to know about). The break window
-- is not consulted, matching the working-hours gate on /start-job.
--
-- Night-stay cutoff is UNCHANGED: the check-in day ends (midnight UTC). A stay
-- is date-granular (BR-06) and the weekly time grid never governs it, so there
-- is no "working day" to key off — midnight already is the end of the day.
--
-- 1970-01-04 was a Sunday, so DATEDIFF(DAY, '19700104', <date>) % 7 gives
-- 0 = Sunday, matching System.DayOfWeek / the [DayOfWeek] column, independently
-- of the server's DATEFIRST setting (same trick used in StartBooking.sql /
-- StartNightStayBooking.sql).
--
-- Run periodically by the scheduled external job, AFTER
-- Booking.RevertExpiredModificationRequests in the same tick, so a
-- booking whose proposal expires AND whose provider's working day has also
-- ended settles as a no-show in the same pass. No no-show equivalent of the
-- 51128/51248 grace-window guard exists in the status engine for this
-- auto-settlement path — reporting manually stays the fast path (30+ minutes /
-- 2+ hours after the scheduled start); this sproc is purely the backstop for
-- when neither party bothers.
-- Returns one row: (ProviderNoShowBookings, ParentNoShowBookings,
--                   ProviderNoShowNightStayBookings, ParentNoShowNightStayBookings).
CREATE OR ALTER PROCEDURE [Booking].[SettleUnstartedJobsAsNoShow]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Today DATE = CAST(@Now AS DATE);
    DECLARE @ProviderNoShowJobNote NVARCHAR(500) =
        N'Automatically marked: the provider never started the job before their working day ended.';
    DECLARE @ParentNoShowJobNote NVARCHAR(500) =
        N'Automatically marked: the start code was issued but never verified before the provider''s working day ended.';
    DECLARE @ProviderNoShowStayNote NVARCHAR(500) =
        N'Automatically marked: the provider never started the stay on the check-in day.';
    DECLARE @ParentNoShowStayNote NVARCHAR(500) =
        N'Automatically marked: the start code was issued on the check-in day but never verified.';

    DECLARE @NoShowBookings TABLE (
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [FromStatus] NVARCHAR(48) NOT NULL,
        [ToStatus] NVARCHAR(48) NOT NULL);
    DECLARE @NoShowNightStays TABLE (
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
        [FromStatus] NVARCHAR(48) NOT NULL,
        [ToStatus] NVARCHAR(48) NOT NULL);

    BEGIN TRANSACTION;

    -- Single-day: cutoff = the later of the provider's closing time on
    -- BookingDate and the booking's own EndTime.
    UPDATE b
    SET [Status] = CASE WHEN b.[Status] = N'START_JOB' THEN N'PARENT_NO_SHOW' ELSE N'PROVIDER_NO_SHOW' END,
        -- Nobody performed and nobody owes, so the payout is settled terminally
        -- rather than left reading 'Pending'. Same value the manual report writes
        -- in Booking.UpdateBookingStatus — a settled no-show must look identical
        -- whether a party tapped it or this job derived it.
        [PayoutStatus] = N'NO_PAYOUT',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[BookingId], deleted.[Status], inserted.[Status] INTO @NoShowBookings
    FROM [Booking].[Bookings] b
    LEFT JOIN [Provider].[ProviderWeeklyAvailability] wa
        ON wa.[ProviderId] = b.[ProviderId]
       AND wa.[DayOfWeek] = CAST(DATEDIFF(DAY, '19700104', b.[BookingDate]) % 7 AS TINYINT)
    CROSS APPLY (
        SELECT
            [BookingEndsAtUtc] =
                DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), b.[EndTime]),
                        CAST(b.[BookingDate] AS DATETIME2(7))),
            [ProviderClosesAtUtc] = CASE
                WHEN wa.[IsOpen] = 1
                    THEN DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), wa.[EndTime]),
                                 CAST(b.[BookingDate] AS DATETIME2(7)))
                ELSE DATEADD(DAY, 1, CAST(b.[BookingDate] AS DATETIME2(7)))
            END
    ) AS [Cutoffs]
    WHERE b.[Status] IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION', N'PARENT_ACCEPTED_MODIFICATION',
                         N'PROVIDER_DECLINED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION', N'START_JOB')
      -- A Custom walk-in is NEVER settled here (2026-08-24). A no-show is a
      -- statement that one PARTY failed to appear, and a walk-in has only one
      -- party: the provider recording their own job. Settling it marked the
      -- provider a no-show — and stamped NO_PAYOUT — for work they had actually
      -- done, purely because the walk-in had no way to be started. (It now has
      -- one: [Booking].[StartBooking] takes it straight to IN_PROGRESS.) A walk-in
      -- the provider simply never finishes now rests at CONFIRMED, which is honest
      -- — nobody was stood up — and stays out of every earnings figure until they
      -- complete it.
      AND b.[Source] <> N'Custom'
      AND @Now >= (CASE WHEN [Cutoffs].[BookingEndsAtUtc] >= [Cutoffs].[ProviderClosesAtUtc]
                        THEN [Cutoffs].[BookingEndsAtUtc] ELSE [Cutoffs].[ProviderClosesAtUtc] END);

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [BookingId], [FromStatus], [ToStatus], N'System', NULL,
           CASE WHEN [ToStatus] = N'PARENT_NO_SHOW' THEN @ParentNoShowJobNote ELSE @ProviderNoShowJobNote END
    FROM @NoShowBookings;

    -- Night stay: unchanged — check-in day ended (midnight UTC), no working-
    -- hours join, mirroring the retired sweep exactly.
    UPDATE [Booking].[NightStayBookings]
    SET [Status] = CASE
            WHEN [Status] = N'START_JOB' THEN N'PARENT_NO_SHOW'
            ELSE N'PROVIDER_NO_SHOW'
        END,
        [PayoutStatus] = N'NO_PAYOUT',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[NightStayBookingId], deleted.[Status], inserted.[Status] INTO @NoShowNightStays
    WHERE [Status] IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION', N'PARENT_ACCEPTED_MODIFICATION',
                       N'PROVIDER_DECLINED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION', N'START_JOB')
      AND [CheckInDate] < @Today;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [NightStayBookingId], [FromStatus], [ToStatus], N'System', NULL,
           CASE WHEN [ToStatus] = N'PARENT_NO_SHOW' THEN @ParentNoShowStayNote ELSE @ProviderNoShowStayNote END
    FROM @NoShowNightStays;

    -- BOTH parties are told, because nobody reported it — the system derived it
    -- from the job never starting (V3 cards P-S8/V-S9 and P-S12/V-S11). Contrast
    -- Booking.UpdateBookingStatus, where a party REPORTS the no-show and only the
    -- counterparty hears about it.
    DECLARE @BookingId UNIQUEIDENTIFIER;
    DECLARE @SettledStatus NVARCHAR(48);
    DECLARE @IsNightStay BIT;
    DECLARE @AbsentParty NVARCHAR(32);

    DECLARE settled_no_shows CURSOR LOCAL FAST_FORWARD FOR
        SELECT [BookingId], [ToStatus], 0 FROM @NoShowBookings
        UNION ALL
        SELECT [NightStayBookingId], [ToStatus], 1 FROM @NoShowNightStays;

    OPEN settled_no_shows;
    FETCH NEXT FROM settled_no_shows INTO @BookingId, @SettledStatus, @IsNightStay;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- Naming the absent party is what lets one template read correctly on
        -- both apps and in either direction.
        SET @AbsentParty = CASE WHEN @SettledStatus = N'PARENT_NO_SHOW'
                                THEN N'the customer' ELSE N'the provider' END;

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = N'PetParent',
            @NotificationType = N'BOOKING_NO_SHOW_AUTO_SETTLED',
            @AbsentParty = @AbsentParty;

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = N'Provider',
            @NotificationType = N'BOOKING_NO_SHOW_AUTO_SETTLED',
            @AbsentParty = @AbsentParty;

        FETCH NEXT FROM settled_no_shows INTO @BookingId, @SettledStatus, @IsNightStay;
    END

    CLOSE settled_no_shows;
    DEALLOCATE settled_no_shows;

    COMMIT TRANSACTION;

    SELECT
        (SELECT COUNT(*) FROM @NoShowBookings WHERE [ToStatus] = N'PROVIDER_NO_SHOW')
            AS [ProviderNoShowBookings],
        (SELECT COUNT(*) FROM @NoShowBookings WHERE [ToStatus] = N'PARENT_NO_SHOW')
            AS [ParentNoShowBookings],
        (SELECT COUNT(*) FROM @NoShowNightStays WHERE [ToStatus] = N'PROVIDER_NO_SHOW')
            AS [ProviderNoShowNightStayBookings],
        (SELECT COUNT(*) FROM @NoShowNightStays WHERE [ToStatus] = N'PARENT_NO_SHOW')
            AS [ParentNoShowNightStayBookings];
END;
GO
PRINT 'Created/updated [Booking].[SettleUnstartedJobsAsNoShow].';
GO

-- Time-driven booking REMINDERS and NUDGES (V3 cards P-S4..P-S7, P-S9, P-S10,
-- P-S13, P-S14, V-S5..V-S8, V-S10, V-S12). Purely additive: changes NO booking
-- status and writes no audit rows, which is why it can run every minute (the
-- "starts in 5 minutes" reminder needs that precision). Re-firing across ticks is
-- prevented by the outbox filtered UNIQUE DedupeKey, NOT by state on the booking.
-- Run by BookingReminderFunction, separate from the 5-minute BookingSweepFunction.
CREATE OR ALTER PROCEDURE [Booking].[SendBookingReminders]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Today DATE = CAST(@Now AS DATE);
    DECLARE @Tomorrow DATE = DATEADD(DAY, 1, @Today);

    -- Every rule below concerns a booking within a couple of days of now: the
    -- earliest is the T-24h reminder (tomorrow) and the latest the closing-time
    -- nudges (today). Bounding the scan keeps this off a full-history seek on a
    -- job that runs every minute; without it, any long-abandoned live booking
    -- would be re-examined 1,440 times a day.
    DECLARE @WindowFrom DATE = DATEADD(DAY, -2, @Today);
    DECLARE @WindowTo DATE = DATEADD(DAY, 2, @Today);

    -- Confirmed-equivalent: the five statuses a live, accepted booking can rest
    -- in. Kept as a table so every arm below tests the same set — the same list
    -- BookingStatuses.ConfirmedEquivalent holds in C#.
    DECLARE @Live TABLE ([Status] NVARCHAR(48) NOT NULL PRIMARY KEY);
    INSERT INTO @Live ([Status]) VALUES
        (N'CONFIRMED'),
        (N'PROVIDER_ACCEPTED_MODIFICATION'), (N'PARENT_ACCEPTED_MODIFICATION'),
        (N'PROVIDER_DECLINED_MODIFICATION'), (N'PARENT_DECLINED_MODIFICATION');

    -- One work list for the whole sproc: (booking, isNightStay, type, audience).
    -- Building it first and enqueuing once at the end keeps the cursor to a
    -- single pass, and keeps each rule readable as one INSERT..SELECT.
    DECLARE @Due TABLE (
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [IsNightStay] BIT NOT NULL,
        [NotificationType] NVARCHAR(64) NOT NULL,
        [Audience] NVARCHAR(16) NOT NULL,
        -- The provider's closing INSTANT, not a bare clock time: the notification
        -- is rendered in the recipient's timezone, and converting a time-of-day
        -- needs the date it falls on.
        [ClosingAtUtc] DATETIME2(0) NULL,
        PRIMARY KEY ([BookingId], [NotificationType], [Audience]));

    -- Single-day bookings with their derived instants. Night-stay is handled
    -- separately per arm, since a stay has no hourly window: only the day-before
    -- and starting-soon reminders apply to it, both measured from drop-off.
    DECLARE @SingleDay TABLE (
        [BookingId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        [Status] NVARCHAR(48) NOT NULL,
        -- The booked calendar day. Carried alongside [StartsAtUtc] rather than
        -- re-derived from it, because the day-before arm is a statement ABOUT the
        -- calendar and reads better tested against one.
        [ServiceDate] DATE NOT NULL,
        [StartsAtUtc] DATETIME2(7) NOT NULL,
        [EndsAtUtc] DATETIME2(7) NOT NULL,
        -- The provider's closing time on the booking date, when they have saved
        -- weekly hours for that weekday. NULL means "no hours on file", which is
        -- treated as "never closes" — the same posture the start-job gate takes.
        -- It doubles as the {closingTime} the pick-up nudges quote, which is why
        -- no separate display column is kept.
        [ClosesAtUtc] DATETIME2(7) NULL);

    INSERT INTO @SingleDay ([BookingId], [Status], [ServiceDate], [StartsAtUtc], [EndsAtUtc], [ClosesAtUtc])
    SELECT b.[BookingId],
           b.[Status],
           b.[BookingDate],
           DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), b.[StartTime]),
                   CAST(b.[BookingDate] AS DATETIME2(7))),
           DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), b.[EndTime]),
                   CAST(b.[BookingDate] AS DATETIME2(7))),
           CASE WHEN w.[EndTime] IS NOT NULL
                THEN DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), w.[EndTime]),
                             CAST(b.[BookingDate] AS DATETIME2(7))) END
    FROM [Booking].[Bookings] b
    LEFT JOIN [Provider].[ProviderWeeklyAvailability] w
        ON w.[ProviderId] = b.[ProviderId]
       -- DATEPART(WEEKDAY) is @@DATEFIRST-dependent; this arithmetic is not.
       -- 0 = Sunday, matching how the availability rows are stored.
       AND w.[DayOfWeek] = (DATEDIFF(DAY, '19000107', b.[BookingDate]) % 7)
       AND w.[IsOpen] = 1
    WHERE b.[BookingDate] BETWEEN @WindowFrom AND @WindowTo
      AND (b.[Status] IN (SELECT [Status] FROM @Live)
           OR b.[Status] IN (N'START_JOB', N'IN_PROGRESS'));

    -- ================================================================
    -- 1. "service tomorrow"  (P-S4 / V-S5) — both parties
    --
    -- THE SERVICE MUST BE ON TOMORROW'S CALENDAR DAY. That gate is the whole rule,
    -- and its absence was a reported bug: the arm used to test only "is the start
    -- within the next 24 hours", which is a DIFFERENT statement — a job booked at
    -- 10:00 for 16:00 the SAME day satisfies it the instant it is created, so the
    -- provider got a card reading "tomorrow at 16:00" about a job later that
    -- afternoon. Anything under a day away is not tomorrow; it is today, and the
    -- T-5min arm below is what covers it.
    --
    -- The 24-hour floor is KEPT on top of the date gate, so the card still lands
    -- roughly a day ahead (09:00 today for a 09:00 job tomorrow) rather than the
    -- instant the clock rolls past midnight. Between them the window is open rather
    -- than instantaneous, because a tick can be missed; the dedupe key is what
    -- keeps it to one send.
    -- ================================================================
    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId], 0, N'BOOKING_REMINDER_DAY_BEFORE', a.[Audience]
    FROM @SingleDay s
    CROSS JOIN (VALUES (N'PetParent'), (N'Provider')) AS a([Audience])
    WHERE s.[Status] IN (SELECT [Status] FROM @Live)
      AND s.[ServiceDate] = @Tomorrow
      AND @Now >= DATEADD(HOUR, -24, s.[StartsAtUtc])
      AND @Now < s.[StartsAtUtc];

    -- A stay's "service" is the drop-off on the check-in day, so the same gate
    -- applies to that date.
    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT n.[NightStayBookingId], 1, N'BOOKING_REMINDER_DAY_BEFORE', a.[Audience]
    FROM [Booking].[NightStayBookings] n
    CROSS JOIN (VALUES (N'PetParent'), (N'Provider')) AS a([Audience])
    WHERE n.[CheckInDate] = @Tomorrow
      AND n.[Status] IN (SELECT [Status] FROM @Live)
      AND @Now >= DATEADD(HOUR, -24,
              DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                      CAST(n.[CheckInDate] AS DATETIME2(7))))
      AND @Now < DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                         CAST(n.[CheckInDate] AS DATETIME2(7)));

    -- ================================================================
    -- 2. T-5min "starts soon"  (P-S5 / V-S6) — both parties
    -- This arm is why the reminder job runs every minute rather than every five:
    -- on a 5-minute cadence "starts in 5 minutes" could arrive anywhere from 0 to
    -- 5 minutes out, which is exactly the message it must not get wrong.
    -- ================================================================
    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId], 0, N'BOOKING_REMINDER_STARTING_SOON', a.[Audience]
    FROM @SingleDay s
    CROSS JOIN (VALUES (N'PetParent'), (N'Provider')) AS a([Audience])
    WHERE s.[Status] IN (SELECT [Status] FROM @Live)
      AND @Now >= DATEADD(MINUTE, -5, s.[StartsAtUtc])
      AND @Now < s.[StartsAtUtc];

    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT n.[NightStayBookingId], 1, N'BOOKING_REMINDER_STARTING_SOON', a.[Audience]
    FROM [Booking].[NightStayBookings] n
    CROSS JOIN (VALUES (N'PetParent'), (N'Provider')) AS a([Audience])
    WHERE n.[CheckInDate] BETWEEN @WindowFrom AND @WindowTo
      AND n.[Status] IN (SELECT [Status] FROM @Live)
      AND @Now >= DATEADD(MINUTE, -5,
              DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                      CAST(n.[CheckInDate] AS DATETIME2(7))))
      AND @Now < DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                         CAST(n.[CheckInDate] AS DATETIME2(7)));

    -- ================================================================
    -- 3. Half-way through the window, still not started  (P-S6 / V-S7) — both
    -- Single-day only: a stay has no hourly window to be half-way through.
    -- ================================================================
    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId], 0, N'BOOKING_NOT_STARTED_HALFWAY', a.[Audience]
    FROM @SingleDay s
    CROSS JOIN (VALUES (N'PetParent'), (N'Provider')) AS a([Audience])
    WHERE s.[Status] IN (SELECT [Status] FROM @Live)
      AND @Now >= DATEADD(SECOND, DATEDIFF(SECOND, s.[StartsAtUtc], s.[EndsAtUtc]) / 2, s.[StartsAtUtc])
      AND @Now < s.[EndsAtUtc];

    -- ================================================================
    -- 4. The whole window elapsed, still not started  (P-S7 / V-S8) — both
    -- Bounded by the provider's closing time, after which BR-38's no-show settle
    -- takes over and nagging would be wrong. No hours on file => unbounded, since
    -- there is no closing time to have passed.
    -- ================================================================
    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId], 0, N'BOOKING_NOT_STARTED_WINDOW_ENDED', a.[Audience]
    FROM @SingleDay s
    CROSS JOIN (VALUES (N'PetParent'), (N'Provider')) AS a([Audience])
    WHERE s.[Status] IN (SELECT [Status] FROM @Live)
      AND @Now >= s.[EndsAtUtc]
      AND (s.[ClosesAtUtc] IS NULL OR @Now < s.[ClosesAtUtc]);

    -- ================================================================
    -- 5. Start-code nudges  (P-S9 / P-S10 / V-S10)
    -- The booking sits in START_JOB: the provider tapped Start and the code was
    -- issued, but it has not been entered. Which message the PARENT gets depends
    -- on whether they have actually opened the code — [SeenAtUtc], stamped by
    -- Booking.IssueBookingStartOtp when their booking-detail read surfaces it.
    --   not seen  -> "You're late for your appointment"  (P-S9)
    --   seen      -> "Share your OTP now, or you'll be marked as a no-show" (P-S10)
    -- The PROVIDER always gets the one nudge (V-S10): from their side there is
    -- only one situation — they haven't entered a code.
    -- ================================================================
    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId],
           0,
           CASE WHEN o.[SeenAtUtc] IS NULL
                THEN N'BOOKING_START_OTP_NOT_SEEN'
                ELSE N'BOOKING_START_OTP_NOT_SHARED' END,
           N'PetParent'
    FROM @SingleDay s
    OUTER APPLY (
        SELECT TOP (1) [SeenAtUtc]
        FROM [Booking].[BookingStartOtps]
        WHERE [BookingId] = s.[BookingId]
        ORDER BY [IssuedAtUtc] DESC) o
    WHERE s.[Status] = N'START_JOB'
      AND @Now >= s.[EndsAtUtc]
      AND (s.[ClosesAtUtc] IS NULL OR @Now < s.[ClosesAtUtc]);

    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId], 0, N'BOOKING_START_OTP_NOT_ENTERED', N'Provider'
    FROM @SingleDay s
    WHERE s.[Status] = N'START_JOB'
      AND @Now >= s.[EndsAtUtc]
      AND (s.[ClosesAtUtc] IS NULL OR @Now < s.[ClosesAtUtc]);

    -- ================================================================
    -- 6. Pick-up and closure  (P-S13 / P-S14 / V-S12)
    -- The job IS under way (IN_PROGRESS), so these are about collecting the pet.
    --   completion time passed        -> parent  (P-S13)
    --   provider closing, pet still in -> parent (P-S14) + provider (V-S12)
    -- ================================================================
    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId], 0, N'BOOKING_COMPLETION_TIME_PASSED', N'PetParent'
    FROM @SingleDay s
    WHERE s.[Status] = N'IN_PROGRESS'
      AND @Now >= s.[EndsAtUtc]
      AND (s.[ClosesAtUtc] IS NULL OR @Now < s.[ClosesAtUtc]);

    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience], [ClosingAtUtc])
    SELECT s.[BookingId], 0, N'BOOKING_PICKUP_OVERDUE', N'PetParent', s.[ClosesAtUtc]
    FROM @SingleDay s
    WHERE s.[Status] = N'IN_PROGRESS'
      AND s.[ClosesAtUtc] IS NOT NULL
      AND @Now >= s.[ClosesAtUtc];

    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience], [ClosingAtUtc])
    SELECT s.[BookingId], 0, N'BOOKING_NOT_MARKED_COMPLETE', N'Provider', s.[ClosesAtUtc]
    FROM @SingleDay s
    WHERE s.[Status] = N'IN_PROGRESS'
      AND s.[ClosesAtUtc] IS NOT NULL
      AND @Now >= s.[ClosesAtUtc];

    -- ================================================================
    -- Enqueue. Every row here relies on the outbox's DedupeKey to collapse
    -- repeats across ticks — see the header.
    -- ================================================================
    DECLARE @BookingId UNIQUEIDENTIFIER;
    DECLARE @IsNightStay BIT;
    DECLARE @Type NVARCHAR(64);
    DECLARE @Audience NVARCHAR(16);
    DECLARE @ClosingAtUtc DATETIME2(0);

    DECLARE due_reminders CURSOR LOCAL FAST_FORWARD FOR
        SELECT [BookingId], [IsNightStay], [NotificationType], [Audience], [ClosingAtUtc] FROM @Due;

    OPEN due_reminders;
    FETCH NEXT FROM due_reminders INTO @BookingId, @IsNightStay, @Type, @Audience, @ClosingAtUtc;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = @Audience,
            @NotificationType = @Type,
            @ClosingAtUtc = @ClosingAtUtc;

        FETCH NEXT FROM due_reminders INTO @BookingId, @IsNightStay, @Type, @Audience, @ClosingAtUtc;
    END

    CLOSE due_reminders;
    DEALLOCATE due_reminders;

    SELECT
        (SELECT COUNT(*) FROM @Due WHERE [NotificationType] = N'BOOKING_REMINDER_DAY_BEFORE')
            AS [DayBeforeReminders],
        (SELECT COUNT(*) FROM @Due WHERE [NotificationType] = N'BOOKING_REMINDER_STARTING_SOON')
            AS [StartingSoonReminders],
        (SELECT COUNT(*) FROM @Due WHERE [NotificationType] IN
            (N'BOOKING_NOT_STARTED_HALFWAY', N'BOOKING_NOT_STARTED_WINDOW_ENDED'))
            AS [NotStartedNudges],
        (SELECT COUNT(*) FROM @Due WHERE [NotificationType] IN
            (N'BOOKING_START_OTP_NOT_SEEN', N'BOOKING_START_OTP_NOT_SHARED',
             N'BOOKING_START_OTP_NOT_ENTERED'))
            AS [StartOtpNudges],
        (SELECT COUNT(*) FROM @Due WHERE [NotificationType] IN
            (N'BOOKING_COMPLETION_TIME_PASSED', N'BOOKING_PICKUP_OVERDUE',
             N'BOOKING_NOT_MARKED_COMPLETE'))
            AS [PickupNudges];
END;
GO
PRINT 'Created/updated [Booking].[SendBookingReminders].';
GO

CREATE OR ALTER PROCEDURE [Booking].[ListNightStayBookingsByProvider]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceId UNIQUEIDENTIFIER = NULL,
    @OnDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- @OnDate narrows to stays that include that night (CheckInDate <= date <
    -- CheckOutDate) — the provider-day view. Omit it to return full history.
    SELECT [NightStayBookingId], [ProviderId], [PetParentId], [ServiceId], [ServiceCategory],
           [SubCategory], [CheckInDate], [CheckOutDate], [DropOffTime], [PickUpTime],
           [Status], [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc], [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [ProviderId] = @ProviderId
      AND (@ServiceId IS NULL OR [ServiceId] = @ServiceId)
      AND (@OnDate IS NULL OR (@OnDate >= [CheckInDate] AND @OnDate < [CheckOutDate]))
    ORDER BY [CheckInDate] DESC, [CheckOutDate] DESC;
END;
GO
PRINT 'Created/updated [Booking].[ListNightStayBookingsByProvider].';
GO

CREATE OR ALTER PROCEDURE [Booking].[ListNightStayBookingsByPetParent]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [NightStayBookingId], [ProviderId], [PetParentId], [ServiceId], [ServiceCategory],
           [SubCategory], [CheckInDate], [CheckOutDate], [DropOffTime], [PickUpTime],
           [Status], [CreatedAtUtc], [UpdatedAtUtc], [CancelledAtUtc], [PetId],
           -- Frozen-at-creation extras for the parent "my bookings" cards.
           -- Appended LAST so the shared night-stay row reader's ordinals hold.
           [PricePerNight], [LocationType], [CancellationPolicyHours],
           [SnapshotAddressLine], [SnapshotCity], [SnapshotZipCode],
           [SnapshotLatitude], [SnapshotLongitude]
    FROM [Booking].[NightStayBookings]
    WHERE [PetParentId] = @PetParentId
    ORDER BY [CheckInDate] DESC, [CheckOutDate] DESC;
END;
GO
PRINT 'Created/updated [Booking].[ListNightStayBookingsByPetParent].';
GO

CREATE OR ALTER PROCEDURE [Booking].[ListNightStayBookingStatusHistory]
    @NightStayBookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [NightStayBookingStatusHistoryId],
           [NightStayBookingId],
           [FromStatus],
           [ToStatus],
           [ChangedByActor],
           [ChangedByActorId],
           [Note],
           [ChangedAtUtc]
    FROM [Booking].[NightStayBookingStatusHistory]
    WHERE [NightStayBookingId] = @NightStayBookingId
    ORDER BY [ChangedAtUtc] ASC, [NightStayBookingStatusHistoryId] ASC;
END;
GO
PRINT 'Created/updated [Booking].[ListNightStayBookingStatusHistory].';
GO


-- 3.9z Booking start-OTP / evidence / modification sprocs (job lifecycle) --
-- Job flow: StartBooking (confirmed-equivalent -> START_JOB, issues the
-- parent-facing start-OTP, working-hours gate) -> VerifyBookingStartOtp (START_JOB ->
-- IN_PROGRESS) -> CompleteBooking (IN_PROGRESS -> COMPLETED, no OTP). The
-- start-OTP lives in [Booking].[BookingStartOtps].
CREATE OR ALTER PROCEDURE [Booking].[IssueBookingStartOtp]
    @BookingId UNIQUEIDENTIFIER,
    @NewCode NVARCHAR(6),
    @TtlMinutes INT = 10,
    -- The parent's position when the code was put on screen. See
    -- [Booking].[StartBooking] for why these are defaulted to NULL.
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @RowPetParent UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    -- [ProviderId] is NOT NULL, so it doubles as the existence check — unlike
    -- [PetParentId], which is legitimately NULL on a Custom walk-in.
    SELECT @RowProvider = [ProviderId], @RowPetParent = [PetParentId]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @RowProvider IS NULL
    BEGIN
        THROW 51130, 'Booking was not found.', 1;
    END

    -- Expire any Pending OTPs that have passed their window.
    UPDATE [Booking].[BookingStartOtps]
    SET [Status] = N'Expired'
    WHERE [BookingId] = @BookingId
      AND [Status] = N'Pending'
      AND [ExpiresAtUtc] <= @Now;

    DECLARE @ActiveId UNIQUEIDENTIFIER;
    SELECT TOP (1) @ActiveId = [BookingStartOtpId]
    FROM [Booking].[BookingStartOtps] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId
      AND [Status] = N'Pending'
      AND [ExpiresAtUtc] > @Now
    ORDER BY [IssuedAtUtc] DESC;

    IF @ActiveId IS NULL
    BEGIN
        DECLARE @Inserted TABLE ([BookingStartOtpId] UNIQUEIDENTIFIER);
        INSERT INTO [Booking].[BookingStartOtps]
            ([BookingId], [OtpCode], [ExpiresAtUtc])
        OUTPUT inserted.[BookingStartOtpId] INTO @Inserted
        VALUES (@BookingId, @NewCode, DATEADD(MINUTE, @TtlMinutes, @Now));

        SELECT @ActiveId = [BookingStartOtpId] FROM @Inserted;
    END

    -- This sproc runs precisely when the parent is shown the code, so it is the
    -- honest place to record that they have seen it. COALESCE keeps the FIRST
    -- sighting: the nudge that branches on this asks "have they opened it at
    -- all?", and re-opening the screen must not reset that answer.
    UPDATE [Booking].[BookingStartOtps]
    SET [SeenAtUtc] = COALESCE([SeenAtUtc], @Now)
    WHERE [BookingStartOtpId] = @ActiveId;

    -- Where the parent was when they showed the code. Unlike [SeenAtUtc] above
    -- this is NOT collapsed to the first sighting: re-opening the screen is a
    -- fresh claim about where they are, and each one is worth keeping.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL AND @RowPetParent IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[BookingLocationEvents]
            ([BookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@BookingId, N'StartOtpShown', N'Parent', @RowPetParent,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    SELECT [BookingStartOtpId], [BookingId], [OtpCode], [Status], [IssuedAtUtc], [ExpiresAtUtc]
    FROM [Booking].[BookingStartOtps]
    WHERE [BookingStartOtpId] = @ActiveId;

    COMMIT TRANSACTION;
END;
GO

-- The end-OTP leg was retired 2026-07-23; the old procedure is dropped so a
-- stray caller cannot keep driving it.
DROP PROCEDURE IF EXISTS [Booking].[StartBookingWithOtp];
GO
-- Provider taps "Start Job" on a single-day booking: a transition from a
-- confirmed-equivalent (live) state to START_JOB that ALSO issues the parent-facing
-- start-OTP (@NewCode, @TtlMinutes) atomically. Two gates, both on "right now" (UTC):
-- the booking's own [BookingDate] must be TODAY, and the provider must be inside
-- their own weekly working hours ([Provider].[ProviderWeeklyAvailability]). The
-- time-of-day within the booked window is deliberately NOT checked — a provider
-- running early or late can still start the job, as long as it is the service day.
-- The parent reads the issued code to the provider, who enters it via
-- [Booking].[VerifyBookingStartOtp] to move the job to IN_PROGRESS.
-- THROWs: 51131 not found, 51132 forbidden, 51133 not in a startable state,
-- 51144 not the booking's service date, 51137 outside the provider's working hours.
--
-- A CUSTOM WALK-IN ([Source] = 'Custom') takes a different path (2026-08-24): it
-- goes straight to IN_PROGRESS with no start-OTP, no service-date gate, no
-- working-hours gate and no push. Every one of those exists to protect a PARENT who
-- is waiting, and a walk-in has none — its [PetParentId] is NULL. Because the OTP is
-- readable ONLY through the parent host's ownership-filtered route, a walk-in could
-- never be verified, never reached IN_PROGRESS, and so never reached COMPLETED or
-- the provider's earnings; it sat at CONFIRMED until the no-show sweep settled it as
-- PROVIDER_NO_SHOW for work the provider had actually done. So 51144 and 51137
-- cannot fire on a walk-in, and the provider's next call is /complete, not
-- /start-job/verify.
--
-- This is also where the ARRIVAL geolocation lands. The provider answers "have you
-- arrived at the customer's location?" (ParentLocation bookings) or "has the
-- customer arrived?" (ProviderLocation bookings) and that answer is what puts them
-- on this call, so their fix is written here — inside the same transaction, so a
-- booking can never reach START_JOB with its arrival evidence lost to a separate
-- failed write. Which of the two questions was asked follows from the booking's own
-- [LocationType] and is therefore not stored again (or trusted from the client);
-- both record the single trigger 'ArrivalConfirmed'.
CREATE OR ALTER PROCEDURE [Booking].[StartBooking]
    @BookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @NewCode NVARCHAR(6),
    @TtlMinutes INT = 10,
    -- The provider's position when they confirmed arrival. Defaulted to NULL so
    -- the procedure stays callable without them (which is what lets the SQL be
    -- deployed before the API); the API itself rejects a start-job request that
    -- arrives without a usable fix, so in practice they are always supplied.
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @BookingDate DATE;
    DECLARE @Source NVARCHAR(16);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId], @BookingDate = [BookingDate],
           @Source = [Source]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51131, 'Booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51132, 'You are not the provider on this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION',
                              N'PARENT_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                              N'PARENT_DECLINED_MODIFICATION')
    BEGIN
        THROW 51133, 'Booking is not in a state the job can be started from.', 1;
    END

    -- The job can only be started on the day it is booked for. Checked before the
    -- working-hours gate so a provider who is open but looking at the wrong day
    -- gets the more specific error. Skipped for a Custom walk-in: the provider set
    -- that date themselves when recording the job, and one written up after the
    -- fact must still be completable rather than stuck forever.
    IF @Source <> N'Custom' AND @BookingDate <> CAST(@Now AS DATE)
    BEGIN
        THROW 51144, 'The job can only be started on the day the booking is scheduled for.', 1;
    END

    -- The job can only be started while the provider is inside their own weekly
    -- working hours. 1970-01-04 was a Sunday, so the modulo gives 0 = Sunday
    -- (matching System.DayOfWeek / the [DayOfWeek] column) independently of DATEFIRST.
    DECLARE @DayOfWeek TINYINT = CAST(DATEDIFF(DAY, '19700104', CAST(@Now AS DATE)) % 7 AS TINYINT);
    DECLARE @NowTime TIME(0) = CAST(@Now AS TIME(0));
    DECLARE @IsOpen BIT, @OpensAt TIME(0), @ClosesAt TIME(0);

    SELECT @IsOpen = [IsOpen], @OpensAt = [StartTime], @ClosesAt = [EndTime]
    FROM [Provider].[ProviderWeeklyAvailability]
    WHERE [ProviderId] = @ProviderId AND [DayOfWeek] = @DayOfWeek;

    -- A provider who has never saved their weekly hours is not gated — nor is a
    -- Custom walk-in, which is precisely the customer who turns up outside the
    -- hours the provider advertised.
    IF @Source <> N'Custom'
       AND @IsOpen IS NOT NULL AND (@IsOpen = 0 OR @NowTime < @OpensAt OR @NowTime > @ClosesAt)
    BEGIN
        THROW 51137, 'The job can only be started during your working hours.', 1;
    END

    -- A Custom walk-in goes STRAIGHT to IN_PROGRESS, skipping START_JOB and the
    -- start-OTP entirely. The OTP is issued TO the parent and readable only through
    -- the parent host's ownership-filtered route — and a walk-in has no parent
    -- ([PetParentId] is NULL), so nobody could ever read it back. That is what used
    -- to strand these bookings at CONFIRMED forever: they never reached IN_PROGRESS,
    -- so they never reached COMPLETED, so they never reached the provider's
    -- earnings at all (this is why [PrivateJobCount] always read 0). A code the
    -- provider both issues and enters would prove nothing anyway.
    DECLARE @TargetStatus NVARCHAR(48) =
        CASE WHEN @Source = N'Custom' THEN N'IN_PROGRESS' ELSE N'START_JOB' END;

    UPDATE [Booking].[Bookings]
    SET [Status] = @TargetStatus, [UpdatedAtUtc] = @Now
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, @TargetStatus, N'Provider', @ProviderId,
         CASE WHEN @Source = N'Custom'
              THEN N'Walk-in job started by the provider (no start code: the booking has no pet parent)'
              ELSE N'Job start requested; start code issued to parent' END);

    -- Where the provider was when they confirmed arrival. Guarded rather than
    -- unconditional only so an older caller that does not pass a fix still works;
    -- the API supplies one on every request.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[BookingLocationEvents]
            ([BookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@BookingId, N'ArrivalConfirmed', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    -- Everything from here is parent-facing, so a walk-in skips it wholesale:
    -- there is no code to issue and nobody to notify (the push audience is
    -- 'PetParent', and enqueuing one against a NULL recipient would be a bug).
    IF @Source <> N'Custom'
    BEGIN
        -- Issue the start-OTP for the parent (reuse-while-valid).
        UPDATE [Booking].[BookingStartOtps]
        SET [Status] = N'Expired'
        WHERE [BookingId] = @BookingId
          AND [Status] = N'Pending'
          AND [ExpiresAtUtc] <= @Now;

        IF NOT EXISTS (
            SELECT 1 FROM [Booking].[BookingStartOtps]
            WHERE [BookingId] = @BookingId
              AND [Status] = N'Pending'
              AND [ExpiresAtUtc] > @Now)
        BEGIN
            INSERT INTO [Booking].[BookingStartOtps]
                ([BookingId], [OtpCode], [ExpiresAtUtc])
            VALUES (@BookingId, @NewCode, DATEADD(MINUTE, @TtlMinutes, @Now));
        END

        -- Tell the parent to open their start code. The provider tapped Start, so
        -- only the parent is notified. The code itself is deliberately NOT in the
        -- payload — a push is readable from a locked screen, and the whole point of
        -- the code is that the parent hands it over in person.
        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = 0,
            @Audience = N'PetParent',
            @NotificationType = N'BOOKING_START_OTP_ISSUED';
    END

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [ServiceItemCode],
           [Source],
           [CustomerName],
           [CustomerMobileCountryCode],
           [CustomerMobile],
           [AnimalType],
           [PetName],
           [ServiceLocation],
           [CustomerLocation],
           [PricePerHour],
           [JobNotes],
           [PetId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO

-- VerifyBookingStartOtp: validates the provider-entered Start code and moves
-- START_JOB -> IN_PROGRESS. The 6th wrong attempt cancels the job. THROWs: 51131
-- not found, 51132 forbidden, 51138 not START_JOB, 51134 invalid/missing OTP,
-- 51135 expired, 51136 too many wrong attempts.
CREATE OR ALTER PROCEDURE [Booking].[VerifyBookingStartOtp]
    @BookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @OtpCode NVARCHAR(6),
    -- The provider's position at the moment the job starts. See
    -- [Booking].[StartBooking] for why these are defaulted to NULL.
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51131, 'Booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51132, 'You are not the provider on this booking.', 1;
    END

    IF @CurrentStatus <> N'START_JOB'
    BEGIN
        THROW 51138, 'Booking is not awaiting a start code.', 1;
    END

    DECLARE @OtpId UNIQUEIDENTIFIER, @StoredCode NVARCHAR(6), @ExpiresAt DATETIME2(7), @FailedCount INT;
    SELECT TOP (1) @OtpId = [BookingStartOtpId], @StoredCode = [OtpCode], @ExpiresAt = [ExpiresAtUtc],
           @FailedCount = [FailedAttemptCount]
    FROM [Booking].[BookingStartOtps] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId
      AND [Status] = N'Pending'
    ORDER BY [IssuedAtUtc] DESC;

    IF @OtpId IS NULL
    BEGIN
        THROW 51134, 'No active code. Ask the parent to open the booking to generate one.', 1;
    END

    IF @ExpiresAt <= @Now
    BEGIN
        UPDATE [Booking].[BookingStartOtps] SET [Status] = N'Expired' WHERE [BookingStartOtpId] = @OtpId;
        COMMIT TRANSACTION;
        THROW 51135, 'The code has expired. Ask the parent to refresh the booking.', 1;
    END

    IF @StoredCode <> @OtpCode
    BEGIN
        DECLARE @NewFailedCount INT = @FailedCount + 1;

        UPDATE [Booking].[BookingStartOtps]
        SET [FailedAttemptCount] = @NewFailedCount,
            [Status] = CASE WHEN @NewFailedCount >= 6 THEN N'Expired' ELSE [Status] END
        WHERE [BookingStartOtpId] = @OtpId;

        IF @NewFailedCount >= 6
        BEGIN
            UPDATE [Booking].[Bookings]
            SET [Status] = N'OTP_MAX_ATTEMPTS_EXCEEDED', [UpdatedAtUtc] = @Now
            WHERE [BookingId] = @BookingId;

            INSERT INTO [Booking].[BookingStatusHistory]
                ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
            VALUES
                (@BookingId, @CurrentStatus, N'OTP_MAX_ATTEMPTS_EXCEEDED', N'System', NULL,
                 N'Job cancelled after 6 incorrect start-code attempts.');

            -- The provider entered the wrong codes, so only the parent is told
            -- their job is off. Enqueued before the COMMIT below so it lands in
            -- the same transaction as the cancellation — the THROW that follows
            -- is the API's 409, not a rollback.
            EXEC [Notification].[EnqueueBookingNotification]
                @BookingId = @BookingId,
                @IsNightStay = 0,
                @Audience = N'PetParent',
                @NotificationType = N'BOOKING_OTP_ATTEMPTS_EXCEEDED';

            COMMIT TRANSACTION;
            THROW 51136, 'Too many incorrect start-code attempts; the job has been cancelled.', 1;
        END

        COMMIT TRANSACTION;
        THROW 51134, 'The start code is incorrect.', 1;
    END

    -- Valid: consume the OTP and move the job to IN_PROGRESS.
    UPDATE [Booking].[BookingStartOtps]
    SET [Status] = N'Consumed', [ConsumedAtUtc] = @Now
    WHERE [BookingStartOtpId] = @OtpId;

    UPDATE [Booking].[Bookings]
    SET [Status] = N'IN_PROGRESS', [UpdatedAtUtc] = @Now
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, N'IN_PROGRESS', N'Provider', @ProviderId, N'Job started with parent start-OTP');

    -- Where the provider was when the job actually started.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[BookingLocationEvents]
            ([BookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@BookingId, N'JobStartProceeded', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    -- "Your job has started. Thank you for the OTP!" — the provider entered the
    -- code, so the parent is the one told.
    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @BookingId,
        @IsNightStay = 0,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_IN_PROGRESS';

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [ServiceItemCode],
           [Source],
           [CustomerName],
           [CustomerMobileCountryCode],
           [CustomerMobile],
           [AnimalType],
           [PetName],
           [ServiceLocation],
           [CustomerLocation],
           [PricePerHour],
           [JobNotes],
           [PetId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO

-- The dual-OTP "End Job" leg was retired 2026-07-23: ENDING is no longer settable
-- and completion needs no OTP, so the End sprocs are dropped on re-deploy.
DROP PROCEDURE IF EXISTS [Booking].[EndBooking];
DROP PROCEDURE IF EXISTS [Booking].[CompleteBookingWithOtp];
GO

-- CompleteBooking: IN_PROGRESS -> COMPLETED with an audit row. No OTP — the
-- parent's code gates only START_JOB -> IN_PROGRESS. ENDING (retired) is
-- tolerated as a from-state so legacy rows can still be completed. THROWs: 51131
-- not found, 51132 forbidden, 51133 not IN_PROGRESS.
CREATE OR ALTER PROCEDURE [Booking].[CompleteBooking]
    @BookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @Source NVARCHAR(16);
    DECLARE @PayoutId NVARCHAR(64);
    DECLARE @BookingDate DATE;
    DECLARE @StartTime TIME(0);
    DECLARE @EndTime TIME(0);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId],
           @Source = [Source], @PayoutId = [PayoutId],
           @BookingDate = [BookingDate], @StartTime = [StartTime], @EndTime = [EndTime]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51131, 'Booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51132, 'You are not the provider on this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'IN_PROGRESS', N'ENDING')
    BEGIN
        THROW 51133, 'Booking is not in a state the job can be completed from.', 1;
    END

    -- Mint the payout reference. Custom walk-ins are deliberately left unstamped:
    -- they are arranged off-platform, carry no Pawfront commission, and can never
    -- reach PAID (see 51163 in [Booking].[MarkBookingPaid]) — so a payout for one
    -- would sit "awaiting payment" forever and skew the provider's earnings.
    IF @PayoutId IS NULL AND @Source = N'App'
    BEGIN
        DECLARE @PayoutNumber BIGINT;
        SET @PayoutNumber = NEXT VALUE FOR [Booking].[PayoutNumberSequence];
        SET @PayoutId = N'PO-' + FORMAT(@PayoutNumber, N'D6');
    END

    -- [PayoutStatus] is not touched: it defaults to 'Pending' at insert and the
    -- IN_PROGRESS from-state guarantees nothing has moved it since.
    -- Release the unused remainder of the booked window: a 6-hour day care
    -- wrapped up after 3 hours kept the other 3 hours blocked, so nobody else
    -- could book them. Every capacity / slot / agenda query reads
    -- COALESCE([ActualEndTime], [EndTime]), so stamping the real finish frees it.
    --
    -- Left NULL (booking keeps its whole window) unless the job genuinely ended
    -- early ON the service date: completed at/after [EndTime] leaves nothing to
    -- give back, and completed on a LATER date must not be compared as a bare
    -- TIME against the booking date. Clamped at [StartTime] for a job that ended
    -- before its booked start (starting is gated on the service DATE, not the
    -- time of day), which releases the slot entirely.
    --
    -- This does NOT re-price the booking: [EndTime] is what the parent agreed to
    -- and what [Booking].[BookingAmounts] and the detail read bill.
    DECLARE @ActualEndTime TIME(0) = NULL;

    IF CAST(@Now AS DATE) = @BookingDate
    BEGIN
        DECLARE @NowTime TIME(0) = CAST(@Now AS TIME(0));
        IF @NowTime < @EndTime
        BEGIN
            SET @ActualEndTime = CASE WHEN @NowTime < @StartTime THEN @StartTime ELSE @NowTime END;
        END
    END

    UPDATE [Booking].[Bookings]
    SET [Status] = N'COMPLETED',
        [UpdatedAtUtc] = @Now,
        [ActualEndTime] = @ActualEndTime,
        [PayoutId] = COALESCE([PayoutId], @PayoutId)
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, N'COMPLETED', N'Provider', @ProviderId, N'Job completed by provider');

    -- The provider completed it, so only the parent is told â€” and the copy asks
    -- for the cash, since payment is always still pending at this point.
    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @BookingId,
        @IsNightStay = 0,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_COMPLETED';

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [ServiceItemCode],
           [Source],
           [CustomerName],
           [CustomerMobileCountryCode],
           [CustomerMobile],
           [AnimalType],
           [PetName],
           [ServiceLocation],
           [CustomerLocation],
           [PricePerHour],
           [JobNotes],
           [PetId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE [Booking].[RequestBookingModification]
    @BookingId UNIQUEIDENTIFIER,
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @ProposedBookingDate DATE,
    @ProposedStartTime TIME(0),
    @ProposedEndTime TIME(0),
    @Note NVARCHAR(500) = NULL,
    @HasAcknowledgedTerms BIT = 0,
    @AcknowledgedPricePerHour DECIMAL(10, 2) = NULL,
    @AcknowledgedCancellationPolicyHours INT = NULL,
    @AcknowledgedAddressLine NVARCHAR(500) = NULL,
    @AcknowledgedCity NVARCHAR(200) = NULL,
    @AcknowledgedZipCode NVARCHAR(32) = NULL,
    @AcknowledgedLatitude DECIMAL(9, 6) = NULL,
    @AcknowledgedLongitude DECIMAL(9, 6) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @BookingDate DATE;
    DECLARE @StartTime TIME(0);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @ProviderId = [ProviderId], @PetParentId = [PetParentId],
           @BookingDate = [BookingDate], @StartTime = [StartTime]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51140, 'Booking was not found.', 1;
    END

    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51141, 'You are not a party to this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION',
                              N'PARENT_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                              N'PARENT_DECLINED_MODIFICATION')
    BEGIN
        THROW 51142, 'A modification can only be requested on a confirmed booking.', 1;
    END

    -- The modification window closes 2 hours before the service starts (all
    -- times UTC), for either party's proposal.
    DECLARE @StartsAtUtc DATETIME2(7) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @StartTime),
                CAST(@BookingDate AS DATETIME2(7)));

    IF @Now >= DATEADD(HOUR, -2, @StartsAtUtc)
    BEGIN
        THROW 51151, 'A booking can no longer be modified within 2 hours of the service start time.', 1;
    END

    IF EXISTS (SELECT 1 FROM [Booking].[BookingModifications] WHERE [BookingId] = @BookingId)
    BEGIN
        THROW 51143, 'A modification request is already awaiting a response.', 1;
    END

    -- Captured so the notification's dedupe key can be scoped to THIS proposal
    -- rather than to the booking (see the enqueue below).
    DECLARE @InsertedModification TABLE ([BookingModificationId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[BookingModifications]
        ([BookingId], [RequestedByActor], [RequestedByActorId],
         [ProposedBookingDate], [ProposedStartTime], [ProposedEndTime], [RequestNote],
         [HasAcknowledgedTerms], [AcknowledgedPricePerHour], [AcknowledgedCancellationPolicyHours],
         [AcknowledgedAddressLine], [AcknowledgedCity], [AcknowledgedZipCode],
         [AcknowledgedLatitude], [AcknowledgedLongitude])
    OUTPUT inserted.[BookingModificationId] INTO @InsertedModification
    VALUES
        (@BookingId, @Actor, @ActorId, @ProposedBookingDate, @ProposedStartTime, @ProposedEndTime, @Note,
         ISNULL(@HasAcknowledgedTerms, 0), @AcknowledgedPricePerHour, @AcknowledgedCancellationPolicyHours,
         @AcknowledgedAddressLine, @AcknowledgedCity, @AcknowledgedZipCode,
         @AcknowledgedLatitude, @AcknowledgedLongitude);

    DECLARE @NewStatus NVARCHAR(48) =
        CASE WHEN @Actor = N'Provider' THEN N'MODIFICATION_REQUEST_BY_PROVIDER'
             ELSE N'MODIFICATION_REQUEST_BY_PARENT' END;

    UPDATE [Booking].[Bookings]
    SET [Status] = @NewStatus, [UpdatedAtUtc] = @Now
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

    -- The counterparty has to review it, so they are the one notified.
    DECLARE @ReqAudience NVARCHAR(16) =
        CASE WHEN @Actor = N'Provider' THEN N'PetParent' ELSE N'Provider' END;
    DECLARE @ReqType NVARCHAR(64) =
        CASE WHEN @Actor = N'Provider'
             THEN N'BOOKING_MODIFICATION_REQUESTED_BY_PROVIDER'
             ELSE N'BOOKING_MODIFICATION_REQUESTED_BY_PARENT' END;

    -- A booking can be modified more than once over its life, and each proposal
    -- is a distinct thing to review — so the dedupe key is scoped to the staging
    -- row rather than the booking, letting a later proposal notify again while
    -- still collapsing a retry of the same one.
    DECLARE @ReqDedupe NVARCHAR(64) =
        CAST((SELECT TOP 1 [BookingModificationId] FROM @InsertedModification) AS NVARCHAR(36));

    -- The proposal as a single UTC instant; the renderer localises it into the
    -- newServiceDate + newStartTime the copy quotes.
    DECLARE @ProposedStartUtc DATETIME2(0) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @ProposedStartTime),
                CAST(@ProposedBookingDate AS DATETIME2(0)));

    -- HOW LONG THE COUNTERPARTY ACTUALLY HAS. Two deadlines can end this proposal
    -- and [Booking].[RevertExpiredModificationRequests] enforces whichever arrives
    -- FIRST, so the notification has to quote the same one:
    --   * the 24-hour review window   — this request + 24h
    --   * the 2-hour pre-service cutoff — @StartsAtUtc - 2h
    -- Telling a provider "review within 24 hours" about a booking that starts in
    -- five hours promises a window that outlives the service; the proposal in fact
    -- dies in three. Both readings are on the SAME clock and derived from the same
    -- @Now the row is stamped with, so the span between them is exact whatever that
    -- clock turns out to be — which is what keeps this honest without dragging the
    -- product-wide UTC-vs-wall-clock question into it.
    --
    -- Sent as the PAIR, not a pre-computed span: the renderer owns user-facing copy
    -- (see NotificationDuration), and the app gets the deadline instant for a live
    -- countdown, which frozen text could never give it.
    DECLARE @RequestedAtUtc DATETIME2(0) = @Now;
    DECLARE @ReviewByUtc DATETIME2(0) =
        CASE WHEN DATEADD(HOUR, 24, @Now) < DATEADD(HOUR, -2, @StartsAtUtc)
             THEN DATEADD(HOUR, 24, @Now)
             ELSE DATEADD(HOUR, -2, @StartsAtUtc) END;

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @BookingId,
        @IsNightStay = 0,
        @Audience = @ReqAudience,
        @NotificationType = @ReqType,
        @NewServiceStartUtc = @ProposedStartUtc,
        @ReviewByUtc = @ReviewByUtc,
        @RequestedAtUtc = @RequestedAtUtc,
        @DedupeSuffix = @ReqDedupe;

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [ServiceItemCode],
           [Source],
           [CustomerName],
           [CustomerMobileCountryCode],
           [CustomerMobile],
           [AnimalType],
           [PetName],
           [ServiceLocation],
           [CustomerLocation],
           [PricePerHour],
           [JobNotes],
           [PetId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE [Booking].[GetPendingBookingModification]
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [BookingModificationId], [BookingId], [RequestedByActor], [RequestedByActorId],
           [ProposedBookingDate], [ProposedStartTime], [ProposedEndTime], [RequestNote], [CreatedAtUtc],
           [HasAcknowledgedTerms], [AcknowledgedPricePerHour], [AcknowledgedCancellationPolicyHours],
           [AcknowledgedAddressLine], [AcknowledgedCity], [AcknowledgedZipCode],
           [AcknowledgedLatitude], [AcknowledgedLongitude]
    FROM [Booking].[BookingModifications] WHERE [BookingId] = @BookingId;
END;
GO

CREATE OR ALTER PROCEDURE [Booking].[RespondBookingModification]
    @BookingId UNIQUEIDENTIFIER,
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @Accept BIT,
    @Capacity INT,
    @Note NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @ServiceId UNIQUEIDENTIFIER;
    DECLARE @BookingDate DATE;
    DECLARE @StartTime TIME(0);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @ProviderId = [ProviderId],
           @PetParentId = [PetParentId], @ServiceId = [ServiceId],
           @BookingDate = [BookingDate], @StartTime = [StartTime]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51145, 'Booking was not found.', 1;
    END

    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51146, 'You are not a party to this booking.', 1;
    END

    -- An unanswered proposal â€” from either party â€” dies 2 hours before the
    -- service starts (all times UTC): reject the attempted response, whichever
    -- side is answering. Checked before the counterparty test so the rejection is
    -- the same whichever party calls. REJECT ONLY: the revert to CONFIRMED and
    -- the discard of the staging row are left to the scheduled external job, the
    -- single writer for time-driven status changes.
    IF @CurrentStatus IN (N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER')
    BEGIN
        DECLARE @StartsAtUtc DATETIME2(7) =
            DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @StartTime),
                    CAST(@BookingDate AS DATETIME2(7)));

        -- The 24-hour review window is the OTHER deadline (2026-08-04). Whichever
        -- arrives first ends the proposal, so both are rejected here â€” without
        -- this guard the 24-hour rule would only hold to the sweep's granularity
        -- and a response could land minutes after the day was up.
        IF EXISTS (
            SELECT 1 FROM [Booking].[BookingModifications]
            WHERE [BookingId] = @BookingId
              AND @Now >= DATEADD(HOUR, 24, [CreatedAtUtc]))
        BEGIN
            THROW 51152, 'The modification request timed out after 24 hours and can no longer be answered.', 1;
        END

        IF @Now >= DATEADD(HOUR, -2, @StartsAtUtc)
        BEGIN
            THROW 51152, 'The modification request expired 2 hours before the service start time and can no longer be answered.', 1;
        END
    END

    -- The responder is the counterparty: provider answers the parent's request
    -- and vice versa.
    DECLARE @ExpectedStatus NVARCHAR(48) =
        CASE WHEN @Actor = N'Provider' THEN N'MODIFICATION_REQUEST_BY_PARENT'
             ELSE N'MODIFICATION_REQUEST_BY_PROVIDER' END;

    IF @CurrentStatus <> @ExpectedStatus
    BEGIN
        THROW 51147, 'There is no modification request awaiting your response.', 1;
    END

    DECLARE @ModId UNIQUEIDENTIFIER, @PDate DATE, @PStart TIME(0), @PEnd TIME(0);
    DECLARE @HasTerms BIT, @TPrice DECIMAL(10, 2), @TPolicyHours INT,
            @TAddressLine NVARCHAR(500), @TCity NVARCHAR(200), @TZipCode NVARCHAR(32),
            @TLatitude DECIMAL(9, 6), @TLongitude DECIMAL(9, 6);
    SELECT @ModId = [BookingModificationId], @PDate = [ProposedBookingDate],
           @PStart = [ProposedStartTime], @PEnd = [ProposedEndTime],
           @HasTerms = [HasAcknowledgedTerms], @TPrice = [AcknowledgedPricePerHour],
           @TPolicyHours = [AcknowledgedCancellationPolicyHours],
           @TAddressLine = [AcknowledgedAddressLine], @TCity = [AcknowledgedCity],
           @TZipCode = [AcknowledgedZipCode], @TLatitude = [AcknowledgedLatitude],
           @TLongitude = [AcknowledgedLongitude]
    FROM [Booking].[BookingModifications] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @ModId IS NULL
    BEGIN
        THROW 51147, 'There is no modification request awaiting your response.', 1;
    END

    DECLARE @NewStatus NVARCHAR(48);

    IF @Accept = 1
    BEGIN
        -- Race-safe capacity re-check on the proposed window, excluding this booking.
        DECLARE @Concurrent INT;
        SELECT @Concurrent = COUNT(*)
        FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [BookingDate] = @PDate
          AND [BookingId] <> @BookingId
          AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
          AND [StartTime] < @PEnd
          -- Hours released by a job that finished early are available to a
          -- reschedule too — same expression [Booking].[CreateBooking] counts
          -- with, since a modification competes for the same capacity.
          AND COALESCE([ActualEndTime], [EndTime]) > @PStart;

        IF @Concurrent >= @Capacity
        BEGIN
            THROW 51148, 'No remaining capacity for the proposed time.', 1;
        END

        SET @NewStatus = CASE WHEN @Actor = N'Provider' THEN N'PROVIDER_ACCEPTED_MODIFICATION'
                              ELSE N'PARENT_ACCEPTED_MODIFICATION' END;

        -- Staging -> main: copy the proposed date/time onto the booking, plus the
        -- acknowledged terms when the requester confirmed a drift. The Application
        -- layer stages a COMPLETE term set (falling back to the booking's own
        -- frozen value for anything it couldn't resolve live), so these are
        -- applied verbatim â€” including NULLs, which are meaningful here (a NULL
        -- cancellation policy is "no restriction").
        UPDATE [Booking].[Bookings]
        SET [BookingDate] = @PDate,
            [StartTime] = @PStart,
            [EndTime] = @PEnd,
            [PricePerHour] = CASE WHEN @HasTerms = 1 THEN @TPrice ELSE [PricePerHour] END,
            [CancellationPolicyHours] = CASE WHEN @HasTerms = 1 THEN @TPolicyHours ELSE [CancellationPolicyHours] END,
            [SnapshotAddressLine] = CASE WHEN @HasTerms = 1 THEN @TAddressLine ELSE [SnapshotAddressLine] END,
            [SnapshotCity] = CASE WHEN @HasTerms = 1 THEN @TCity ELSE [SnapshotCity] END,
            [SnapshotZipCode] = CASE WHEN @HasTerms = 1 THEN @TZipCode ELSE [SnapshotZipCode] END,
            [SnapshotLatitude] = CASE WHEN @HasTerms = 1 THEN @TLatitude ELSE [SnapshotLatitude] END,
            [SnapshotLongitude] = CASE WHEN @HasTerms = 1 THEN @TLongitude ELSE [SnapshotLongitude] END,
            [Status] = @NewStatus,
            [UpdatedAtUtc] = @Now
        WHERE [BookingId] = @BookingId;
    END
    ELSE
    BEGIN
        SET @NewStatus = CASE WHEN @Actor = N'Provider' THEN N'PROVIDER_DECLINED_MODIFICATION'
                              ELSE N'PARENT_DECLINED_MODIFICATION' END;

        UPDATE [Booking].[Bookings]
        SET [Status] = @NewStatus, [UpdatedAtUtc] = @Now
        WHERE [BookingId] = @BookingId;
    END

    -- Remove the proposal from staging (consumed on accept, discarded on decline).
    DELETE FROM [Booking].[BookingModifications] WHERE [BookingModificationId] = @ModId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

    -- The responder tapped it; the REQUESTER is the one waiting to hear. That is
    -- the opposite mapping to the request enqueue above, and it is why the
    -- audience is derived from @Actor here rather than reused.
    DECLARE @RespAudience NVARCHAR(16) =
        CASE WHEN @Actor = N'Provider' THEN N'PetParent' ELSE N'Provider' END;
    DECLARE @RespType NVARCHAR(64) =
        CASE
            WHEN @Actor = N'Provider' AND @Accept = 1 THEN N'BOOKING_MODIFICATION_ACCEPTED_BY_PROVIDER'
            WHEN @Actor = N'Provider'                 THEN N'BOOKING_MODIFICATION_DECLINED_BY_PROVIDER'
            WHEN @Accept = 1                          THEN N'BOOKING_MODIFICATION_ACCEPTED_BY_PARENT'
            ELSE                                           N'BOOKING_MODIFICATION_DECLINED_BY_PARENT'
        END;

    -- On accept the booking now HOLDS the new timing, so the helper's serviceDate /
    -- startTime already read as the new window; newServiceDate/newStartTime are
    -- passed so the "confirmed: X at Y" copy is explicit either way. On decline the
    -- booking kept its original window, which is what the copy quotes.
    -- The proposal as a single UTC instant; the renderer localises it into the
    -- newServiceDate + newStartTime the copy quotes.
    DECLARE @RespStartUtc DATETIME2(0) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @PStart),
                CAST(@PDate AS DATETIME2(0)));
    DECLARE @RespDedupe NVARCHAR(64) = CAST(@ModId AS NVARCHAR(36));

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @BookingId,
        @IsNightStay = 0,
        @Audience = @RespAudience,
        @NotificationType = @RespType,
        @NewServiceStartUtc = @RespStartUtc,
        @DedupeSuffix = @RespDedupe;

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [ServiceItemCode],
           [Source],
           [CustomerName],
           [CustomerMobileCountryCode],
           [CustomerMobile],
           [AnimalType],
           [PetName],
           [ServiceLocation],
           [CustomerLocation],
           [PricePerHour],
           [JobNotes],
           [PetId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE [Booking].[AddBookingEvidence]
    @BookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000),
    -- See [Booking].[StartBooking] for why these are defaulted to NULL.
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1 FROM [Booking].[Bookings]
        WHERE [BookingId] = @BookingId AND [ProviderId] = @ProviderId)
    BEGIN
        THROW 51150, 'Booking was not found for this provider.', 1;
    END

    DECLARE @Inserted TABLE ([BookingEvidenceId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[BookingEvidence] ([BookingId], [PhotoUrl])
    OUTPUT inserted.[BookingEvidenceId] INTO @Inserted
    VALUES (@BookingId, @PhotoUrl);

    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[BookingLocationEvents]
            ([BookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@BookingId, N'EvidenceCaptured', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    SELECT [BookingEvidenceId], [BookingId], [PhotoUrl], [CreatedAtUtc]
    FROM [Booking].[BookingEvidence]
    WHERE [BookingEvidenceId] = (SELECT TOP (1) [BookingEvidenceId] FROM @Inserted);

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE [Booking].[ListBookingEvidence]
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [BookingEvidenceId], [BookingId], [PhotoUrl], [CreatedAtUtc]
    FROM [Booking].[BookingEvidence] WHERE [BookingId] = @BookingId ORDER BY [CreatedAtUtc] ASC;
END;
GO

-- Records one geolocation fix against a single-day booking, standalone — i.e. for
-- the moments that are NOT themselves a status transition, and therefore have no
-- transition sproc to ride along inside:
--   * CashNotReceived        the provider records that they were not paid. Log
--                            only: it changes no status and no payout field (see
--                            the endpoint notes) — the row IS the record.
--   * the PARENT's own fix   at a moment the PROVIDER drove (NoShowMarked,
--                            CashReceived, CashNotReceived, EvidenceCaptured).
--                            Each app reports its own position, so the parent's
--                            arrives on its own call rather than being copied off
--                            the provider's.
-- Every OTHER trigger is written inside the sproc that performs the transition
-- (StartBooking, VerifyBookingStartOtp, UpdateBookingStatus, MarkBookingPaid,
-- AddBookingEvidence, IssueBookingStartOtp), so a transition can never commit
-- while its evidence is lost to a separate failed call.
--
-- The acting party comes from the authenticated route, never the body, and is
-- checked against the booking here as well.
-- THROWs: 51360 booking not found, 51361 not a party to the booking,
-- 51362 this party cannot record this trigger.
CREATE OR ALTER PROCEDURE [Booking].[RecordBookingLocationEvent]
    @BookingId UNIQUEIDENTIFIER,
    @Trigger NVARCHAR(32),
    @CapturedByType NVARCHAR(16),
    @CapturedById UNIQUEIDENTIFIER,
    @Latitude DECIMAL(9, 6),
    @Longitude DECIMAL(9, 6),
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;

    SELECT @ProviderId = [ProviderId], @PetParentId = [PetParentId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    IF @ProviderId IS NULL
    BEGIN
        THROW 51360, 'Booking was not found.', 1;
    END

    -- A Custom walk-in has no PetParentId, so the Parent branch can never match
    -- one — which is correct: there is no second party on a private job to have
    -- a location.
    IF (@CapturedByType = N'Provider' AND @CapturedById <> @ProviderId)
       OR (@CapturedByType = N'Parent' AND (@PetParentId IS NULL OR @CapturedById <> @PetParentId))
       OR @CapturedByType NOT IN (N'Provider', N'Parent')
    BEGIN
        THROW 51361, 'You are not a party to this booking.', 1;
    END

    -- Defensive; the API validates first. Raised explicitly rather than left to
    -- CK_BookingLocationEvents_TriggerParty so a bad combination comes back as a
    -- typed error instead of a raw constraint violation.
    IF @Trigger NOT IN (N'NoShowMarked', N'CashReceived', N'CashNotReceived', N'EvidenceCaptured')
       OR (@Trigger = N'CashNotReceived' AND @CapturedByType NOT IN (N'Provider', N'Parent'))
    BEGIN
        THROW 51362, 'This trigger cannot be recorded on its own by this party.', 1;
    END

    DECLARE @Inserted TABLE ([BookingLocationEventId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[BookingLocationEvents]
        ([BookingId], [Trigger], [CapturedByType], [CapturedById],
         [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
    OUTPUT inserted.[BookingLocationEventId] INTO @Inserted
    VALUES
        (@BookingId, @Trigger, @CapturedByType, @CapturedById,
         @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);

    SELECT [BookingLocationEventId], [BookingId], [Trigger], [CapturedByType], [CapturedById],
           [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc], [RecordedAtUtc]
    FROM [Booking].[BookingLocationEvents]
    WHERE [BookingLocationEventId] = (SELECT TOP (1) [BookingLocationEventId] FROM @Inserted);
END;
GO

-- Night-stay twin of [Booking].[RecordBookingLocationEvent] — see that file for
-- which moments arrive here rather than inside a transition sproc, and why.
-- Night stays are App-only, so [PetParentId] is NOT NULL and there is no Custom
-- walk-in case to exclude from the party check.
-- THROWs: 51363 booking not found, 51364 not a party to the booking,
-- 51365 this party cannot record this trigger.
CREATE OR ALTER PROCEDURE [Booking].[RecordNightStayBookingLocationEvent]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @Trigger NVARCHAR(32),
    @CapturedByType NVARCHAR(16),
    @CapturedById UNIQUEIDENTIFIER,
    @Latitude DECIMAL(9, 6),
    @Longitude DECIMAL(9, 6),
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;

    SELECT @ProviderId = [ProviderId], @PetParentId = [PetParentId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @ProviderId IS NULL
    BEGIN
        THROW 51363, 'Night-stay booking was not found.', 1;
    END

    IF (@CapturedByType = N'Provider' AND @CapturedById <> @ProviderId)
       OR (@CapturedByType = N'Parent' AND @CapturedById <> @PetParentId)
       OR @CapturedByType NOT IN (N'Provider', N'Parent')
    BEGIN
        THROW 51364, 'You are not a party to this booking.', 1;
    END

    IF @Trigger NOT IN (N'NoShowMarked', N'CashReceived', N'CashNotReceived', N'EvidenceCaptured')
    BEGIN
        THROW 51365, 'This trigger cannot be recorded on its own by this party.', 1;
    END

    DECLARE @Inserted TABLE ([NightStayBookingLocationEventId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[NightStayBookingLocationEvents]
        ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
         [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
    OUTPUT inserted.[NightStayBookingLocationEventId] INTO @Inserted
    VALUES
        (@NightStayBookingId, @Trigger, @CapturedByType, @CapturedById,
         @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);

    SELECT [NightStayBookingLocationEventId], [NightStayBookingId], [Trigger], [CapturedByType],
           [CapturedById], [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc], [RecordedAtUtc]
    FROM [Booking].[NightStayBookingLocationEvents]
    WHERE [NightStayBookingLocationEventId] = (SELECT TOP (1) [NightStayBookingLocationEventId] FROM @Inserted);
END;
GO

-- The full location timeline for one booking, oldest first — the read the
-- admin/support panel needs when working a dispute ("was the provider actually
-- there when they said they had arrived?").
--
-- ONE procedure for both booking kinds, like [Review].[UpsertBookingReview] and
-- unlike the twinned write paths: the only difference between them is which
-- table supplies the rows, and a support screen should not have to know which
-- kind it is holding before it can ask. @BookingType is 'SingleDay' or
-- 'NightStay' — the same vocabulary [Booking].[BookingPayments] uses.
--
-- Deliberately NOT exposed on either app. These are both parties' precise
-- coordinates: handing a provider the parent's position (or the reverse) is a
-- safety problem, and the stated purpose of the capture is to confirm things to
-- Pawfront, not to the counterparty. It ships ahead of the panel for the same
-- reason [Support].[CloseTicket] did — so the panel is a single call away.
--
-- Returns an empty set for an unknown booking rather than throwing: this is a
-- read, and a support screen showing "no location was recorded" is a better
-- answer than an error.
CREATE OR ALTER PROCEDURE [Booking].[ListBookingLocationEvents]
    @BookingType NVARCHAR(16),
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    IF @BookingType = N'NightStay'
    BEGIN
        SELECT [NightStayBookingLocationEventId] AS [BookingLocationEventId],
               [NightStayBookingId] AS [BookingId],
               [Trigger],
               [CapturedByType],
               [CapturedById],
               [Latitude],
               [Longitude],
               [AccuracyMetres],
               [DeviceCapturedAtUtc],
               [RecordedAtUtc]
        FROM [Booking].[NightStayBookingLocationEvents]
        WHERE [NightStayBookingId] = @BookingId
        ORDER BY [RecordedAtUtc] ASC, [NightStayBookingLocationEventId] ASC;
    END
    ELSE
    BEGIN
        SELECT [BookingLocationEventId],
               [BookingId],
               [Trigger],
               [CapturedByType],
               [CapturedById],
               [Latitude],
               [Longitude],
               [AccuracyMetres],
               [DeviceCapturedAtUtc],
               [RecordedAtUtc]
        FROM [Booking].[BookingLocationEvents]
        WHERE [BookingId] = @BookingId
        ORDER BY [RecordedAtUtc] ASC, [BookingLocationEventId] ASC;
    END
END;
GO

CREATE OR ALTER PROCEDURE [Booking].[IssueNightStayBookingStartOtp]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @NewCode NVARCHAR(6),
    @TtlMinutes INT = 10,
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @RowPetParent UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @RowPetParent = [PetParentId]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @RowPetParent IS NULL
    BEGIN
        THROW 51250, 'Night stay booking was not found.', 1;
    END

    UPDATE [Booking].[NightStayBookingStartOtps]
    SET [Status] = N'Expired'
    WHERE [NightStayBookingId] = @NightStayBookingId
      AND [Status] = N'Pending'
      AND [ExpiresAtUtc] <= @Now;

    DECLARE @ActiveId UNIQUEIDENTIFIER;
    SELECT TOP (1) @ActiveId = [NightStayBookingStartOtpId]
    FROM [Booking].[NightStayBookingStartOtps] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId
      AND [Status] = N'Pending'
      AND [ExpiresAtUtc] > @Now
    ORDER BY [IssuedAtUtc] DESC;

    IF @ActiveId IS NULL
    BEGIN
        DECLARE @Inserted TABLE ([NightStayBookingStartOtpId] UNIQUEIDENTIFIER);
        INSERT INTO [Booking].[NightStayBookingStartOtps]
            ([NightStayBookingId], [OtpCode], [ExpiresAtUtc])
        OUTPUT inserted.[NightStayBookingStartOtpId] INTO @Inserted
        VALUES (@NightStayBookingId, @NewCode, DATEADD(MINUTE, @TtlMinutes, @Now));

        SELECT @ActiveId = [NightStayBookingStartOtpId] FROM @Inserted;
    END

    -- Mirror of Booking.IssueBookingStartOtp: record the parent's first sighting
    -- of the code, which is what separates the two nudge messages.
    UPDATE [Booking].[NightStayBookingStartOtps]
    SET [SeenAtUtc] = COALESCE([SeenAtUtc], @Now)
    WHERE [NightStayBookingStartOtpId] = @ActiveId;

    -- Where the parent was when they showed the code. NOT collapsed to the first
    -- sighting the way [SeenAtUtc] is — each showing is a fresh position claim.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[NightStayBookingLocationEvents]
            ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@NightStayBookingId, N'StartOtpShown', N'Parent', @RowPetParent,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    SELECT [NightStayBookingStartOtpId] AS [BookingStartOtpId], [NightStayBookingId] AS [BookingId],
           [OtpCode], [Status], [IssuedAtUtc], [ExpiresAtUtc]
    FROM [Booking].[NightStayBookingStartOtps]
    WHERE [NightStayBookingStartOtpId] = @ActiveId;

    COMMIT TRANSACTION;
END;
GO

-- StartNightStayBooking: provider taps "Start Job" — confirmed-equivalent ->
-- START_JOB, issuing the parent-facing start-OTP. Gated on the stay's
-- [CheckInDate] (the drop-off day) being today and on the provider's own weekly
-- working hours (UTC) — not on the drop-off TIME. Mirror of
-- [Booking].[StartBooking]. THROWs: 51251 not found, 51252 forbidden,
-- 51253 not startable, 51264 not the check-in date, 51257 outside working hours.
DROP PROCEDURE IF EXISTS [Booking].[StartNightStayBookingWithOtp];
GO
CREATE OR ALTER PROCEDURE [Booking].[StartNightStayBooking]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @NewCode NVARCHAR(6),
    @TtlMinutes INT = 10,
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @CheckInDate DATE;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId], @CheckInDate = [CheckInDate]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51251, 'Night stay booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51252, 'You are not the provider on this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION',
                              N'PARENT_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                              N'PARENT_DECLINED_MODIFICATION')
    BEGIN
        THROW 51253, 'Booking is not in a state the job can be started from.', 1;
    END

    -- The stay can only be started on its drop-off day. Checked before the
    -- working-hours gate so a provider who is open but looking at the wrong day
    -- gets the more specific error.
    IF @CheckInDate <> CAST(@Now AS DATE)
    BEGIN
        THROW 51264, 'The job can only be started on the day the booking is scheduled for.', 1;
    END

    -- The job can only be started while the provider is inside their own weekly
    -- working hours. 1970-01-04 was a Sunday, so the modulo gives 0 = Sunday
    -- (matching System.DayOfWeek / the [DayOfWeek] column) independently of DATEFIRST.
    DECLARE @DayOfWeek TINYINT = CAST(DATEDIFF(DAY, '19700104', CAST(@Now AS DATE)) % 7 AS TINYINT);
    DECLARE @NowTime TIME(0) = CAST(@Now AS TIME(0));
    DECLARE @IsOpen BIT, @OpensAt TIME(0), @ClosesAt TIME(0);

    SELECT @IsOpen = [IsOpen], @OpensAt = [StartTime], @ClosesAt = [EndTime]
    FROM [Provider].[ProviderWeeklyAvailability]
    WHERE [ProviderId] = @ProviderId AND [DayOfWeek] = @DayOfWeek;

    -- A provider who has never saved their weekly hours is not gated.
    IF @IsOpen IS NOT NULL AND (@IsOpen = 0 OR @NowTime < @OpensAt OR @NowTime > @ClosesAt)
    BEGIN
        THROW 51257, 'The job can only be started during your working hours.', 1;
    END

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'START_JOB', [UpdatedAtUtc] = @Now
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, N'START_JOB', N'Provider', @ProviderId, N'Job start requested; start code issued to parent');

    -- Where the provider was when they confirmed arrival.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[NightStayBookingLocationEvents]
            ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@NightStayBookingId, N'ArrivalConfirmed', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    UPDATE [Booking].[NightStayBookingStartOtps]
    SET [Status] = N'Expired'
    WHERE [NightStayBookingId] = @NightStayBookingId
      AND [Status] = N'Pending'
      AND [ExpiresAtUtc] <= @Now;

    IF NOT EXISTS (
        SELECT 1 FROM [Booking].[NightStayBookingStartOtps]
        WHERE [NightStayBookingId] = @NightStayBookingId
          AND [Status] = N'Pending'
          AND [ExpiresAtUtc] > @Now)
    BEGIN
        INSERT INTO [Booking].[NightStayBookingStartOtps]
            ([NightStayBookingId], [OtpCode], [ExpiresAtUtc])
        VALUES (@NightStayBookingId, @NewCode, DATEADD(MINUTE, @TtlMinutes, @Now));
    END

    -- Mirror of Booking.StartBooking: the parent is told to open their code.
    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @NightStayBookingId,
        @IsNightStay = 1,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_START_OTP_ISSUED';

    SELECT [NightStayBookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [CheckInDate],
           [CheckOutDate],
           [DropOffTime],
           [PickUpTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
GO

-- VerifyNightStayBookingStartOtp: validates the provider-entered Start code and
-- moves START_JOB -> IN_PROGRESS. Mirror of [Booking].[VerifyBookingStartOtp].
-- THROWs: 51251 not found, 51252 forbidden, 51258 not START_JOB, 51254
-- invalid/missing OTP, 51255 expired, 51256 too many wrong attempts.
CREATE OR ALTER PROCEDURE [Booking].[VerifyNightStayBookingStartOtp]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @OtpCode NVARCHAR(6),
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51251, 'Night stay booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51252, 'You are not the provider on this booking.', 1;
    END

    IF @CurrentStatus <> N'START_JOB'
    BEGIN
        THROW 51258, 'Booking is not awaiting a start code.', 1;
    END

    DECLARE @OtpId UNIQUEIDENTIFIER, @StoredCode NVARCHAR(6), @ExpiresAt DATETIME2(7), @FailedCount INT;
    SELECT TOP (1) @OtpId = [NightStayBookingStartOtpId], @StoredCode = [OtpCode], @ExpiresAt = [ExpiresAtUtc],
           @FailedCount = [FailedAttemptCount]
    FROM [Booking].[NightStayBookingStartOtps] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId
      AND [Status] = N'Pending'
    ORDER BY [IssuedAtUtc] DESC;

    IF @OtpId IS NULL
    BEGIN
        THROW 51254, 'No active code. Ask the parent to open the booking to generate one.', 1;
    END

    IF @ExpiresAt <= @Now
    BEGIN
        UPDATE [Booking].[NightStayBookingStartOtps] SET [Status] = N'Expired' WHERE [NightStayBookingStartOtpId] = @OtpId;
        COMMIT TRANSACTION;
        THROW 51255, 'The code has expired. Ask the parent to refresh the booking.', 1;
    END

    IF @StoredCode <> @OtpCode
    BEGIN
        DECLARE @NewFailedCount INT = @FailedCount + 1;

        UPDATE [Booking].[NightStayBookingStartOtps]
        SET [FailedAttemptCount] = @NewFailedCount,
            [Status] = CASE WHEN @NewFailedCount >= 6 THEN N'Expired' ELSE [Status] END
        WHERE [NightStayBookingStartOtpId] = @OtpId;

        IF @NewFailedCount >= 6
        BEGIN
            UPDATE [Booking].[NightStayBookings]
            SET [Status] = N'OTP_MAX_ATTEMPTS_EXCEEDED', [UpdatedAtUtc] = @Now
            WHERE [NightStayBookingId] = @NightStayBookingId;

            INSERT INTO [Booking].[NightStayBookingStatusHistory]
                ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
            VALUES
                (@NightStayBookingId, @CurrentStatus, N'OTP_MAX_ATTEMPTS_EXCEEDED', N'System', NULL,
                 N'Job cancelled after 6 incorrect start-code attempts.');

            -- Enqueued before the COMMIT so it shares the cancellation's
            -- transaction; the THROW below is the API's 409, not a rollback.
            EXEC [Notification].[EnqueueBookingNotification]
                @BookingId = @NightStayBookingId,
                @IsNightStay = 1,
                @Audience = N'PetParent',
                @NotificationType = N'BOOKING_OTP_ATTEMPTS_EXCEEDED';

            COMMIT TRANSACTION;
            THROW 51256, 'Too many incorrect start-code attempts; the job has been cancelled.', 1;
        END

        COMMIT TRANSACTION;
        THROW 51254, 'The start code is incorrect.', 1;
    END

    UPDATE [Booking].[NightStayBookingStartOtps]
    SET [Status] = N'Consumed', [ConsumedAtUtc] = @Now
    WHERE [NightStayBookingStartOtpId] = @OtpId;

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'IN_PROGRESS', [UpdatedAtUtc] = @Now
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, N'IN_PROGRESS', N'Provider', @ProviderId, N'Job started with parent start-OTP');

    -- Where the provider was when the stay actually started.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[NightStayBookingLocationEvents]
            ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@NightStayBookingId, N'JobStartProceeded', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @NightStayBookingId,
        @IsNightStay = 1,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_IN_PROGRESS';

    SELECT [NightStayBookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [CheckInDate],
           [CheckOutDate],
           [DropOffTime],
           [PickUpTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
GO

-- The dual-OTP "End Job" leg was retired 2026-07-23 (night-stay mirror): the End
-- sprocs are dropped on re-deploy.
DROP PROCEDURE IF EXISTS [Booking].[EndNightStayBooking];
DROP PROCEDURE IF EXISTS [Booking].[CompleteNightStayBookingWithOtp];
GO

-- CompleteNightStayBooking: IN_PROGRESS -> COMPLETED with an audit row. Mirror of
-- [Booking].[CompleteBooking] — no OTP; ENDING (retired) tolerated as a
-- from-state for legacy rows. THROWs: 51251 not found, 51252 forbidden, 51253 not
-- IN_PROGRESS.
CREATE OR ALTER PROCEDURE [Booking].[CompleteNightStayBooking]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @PayoutId NVARCHAR(64);
    DECLARE @CheckInDate DATE;
    DECLARE @CheckOutDate DATE;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId],
           @PayoutId = [PayoutId],
           @CheckInDate = [CheckInDate], @CheckOutDate = [CheckOutDate]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51251, 'Night stay booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51252, 'You are not the provider on this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'IN_PROGRESS', N'ENDING')
    BEGIN
        THROW 51253, 'Booking is not in a state the job can be completed from.', 1;
    END

    -- Same payout namespace as single-day bookings (one shared SEQUENCE), so a
    -- 'PO-...' reference identifies a payout without needing the booking kind.
    IF @PayoutId IS NULL
    BEGIN
        DECLARE @PayoutNumber BIGINT;
        SET @PayoutNumber = NEXT VALUE FOR [Booking].[PayoutNumberSequence];
        SET @PayoutId = N'PO-' + FORMAT(@PayoutNumber, N'D6');
    END

    -- Release the nights the pet did not stay: a 4-day / 3-night stay collected
    -- a day early kept that last night blocked against per-night capacity. Every
    -- per-night query reads COALESCE([ActualCheckOutDate], [CheckOutDate]).
    --
    -- Same [CheckInDate, X) meaning as [CheckOutDate] — it is the pickup DAY,
    -- not a stayed night — so completing on day D means nights CheckInDate..D-1
    -- were used. Left NULL when completion lands on/after [CheckOutDate].
    -- Floored at one night: a stay wrapped up on the check-in day still consumed
    -- that night's place. This does NOT refund the stay.
    DECLARE @Today DATE = CAST(@Now AS DATE);
    DECLARE @ActualCheckOutDate DATE = NULL;

    IF @Today < @CheckOutDate
    BEGIN
        SET @ActualCheckOutDate =
            CASE WHEN @Today <= @CheckInDate THEN DATEADD(DAY, 1, @CheckInDate) ELSE @Today END;
    END

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'COMPLETED',
        [UpdatedAtUtc] = @Now,
        [ActualCheckOutDate] = @ActualCheckOutDate,
        [PayoutId] = COALESCE([PayoutId], @PayoutId)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, N'COMPLETED', N'Provider', @ProviderId, N'Job completed by provider');

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @NightStayBookingId,
        @IsNightStay = 1,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_COMPLETED';

    SELECT [NightStayBookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [CheckInDate],
           [CheckOutDate],
           [DropOffTime],
           [PickUpTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
GO

-- MarkBookingPaid: the parent has paid the provider — flips COMPLETED -> PAID,
-- audits it, and writes the payment ledger row. Provider-only, App bookings only,
-- paid at most once. @Amount/@PawfrontFee come from the app layer's price-locked
-- computation. THROWs: 51160 not found, 51161 not the provider, 51162 not
-- COMPLETED, 51163 Custom walk-in (App only), 51164 already paid.
CREATE OR ALTER PROCEDURE [Booking].[MarkBookingPaid]
    @BookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @Amount DECIMAL(10, 2),
    @PawfrontFee DECIMAL(10, 2),
    @PaymentMethod NVARCHAR(16),
    -- The provider's position when the cash changed hands. See
    -- [Booking].[StartBooking] for why these are defaulted to NULL.
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @RowPetParent UNIQUEIDENTIFIER;
    DECLARE @Source NVARCHAR(16);
    DECLARE @PayoutId NVARCHAR(64);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId],
           @RowPetParent = [PetParentId], @Source = [Source],
           @PayoutId = [PayoutId]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51160, 'Booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51161, 'You are not the provider on this booking.', 1;
    END

    IF @Source <> N'App'
    BEGIN
        THROW 51163, 'Only app bookings can be marked paid.', 1;
    END

    IF @CurrentStatus = N'PAID'
    BEGIN
        THROW 51164, 'Booking is already marked paid.', 1;
    END

    IF @CurrentStatus <> N'COMPLETED'
    BEGIN
        THROW 51162, 'Booking must be completed before it can be marked paid.', 1;
    END

    -- The payout is normally minted at COMPLETED; stamp one here too so a booking
    -- completed before payout stamping shipped still ends up with a reference
    -- rather than a settled payout that has no id.
    IF @PayoutId IS NULL
    BEGIN
        DECLARE @PayoutNumber BIGINT;
        SET @PayoutNumber = NEXT VALUE FOR [Booking].[PayoutNumberSequence];
        SET @PayoutId = N'PO-' + FORMAT(@PayoutNumber, N'D6');
    END

    -- Cash-only today, so recording the payment settles the payout in the same
    -- step: the parent handed the provider the money directly, there is no
    -- separate transfer leg to wait on.
    UPDATE [Booking].[Bookings]
    SET [Status] = N'PAID',
        [UpdatedAtUtc] = @Now,
        [PayoutId] = COALESCE([PayoutId], @PayoutId),
        [PayoutStatus] = N'Paid'
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, N'PAID', N'Provider', @ProviderId, N'Payment received from parent');

    INSERT INTO [Booking].[BookingPayments]
        ([BookingType], [BookingId], [ProviderId], [PetParentId], [Amount], [PawfrontFee], [PaymentMethod], [PaidAtUtc])
    VALUES
        (N'SingleDay', @BookingId, @ProviderId, @RowPetParent, @Amount, @PawfrontFee, @PaymentMethod, @Now);

    -- Where the provider was when they took the money.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[BookingLocationEvents]
            ([BookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@BookingId, N'CashReceived', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    -- The provider recorded the cash, so the parent gets the receipt. The amount
    -- is the one just written to the ledger, not a re-derivation — the two must
    -- never disagree.
    DECLARE @AmountText NVARCHAR(64) =
        N'CHF ' + CONVERT(NVARCHAR(32), CAST(@Amount AS DECIMAL(12, 2)));

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @BookingId,
        @IsNightStay = 0,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_PAID',
        @Amount = @AmountText;

    -- ...and the PROVIDER gets their invoice (V-S14). This is the moment it is
    -- settled: the job is COMPLETED, the cash is recorded, the payout above just
    -- flipped to 'Paid'. Both parties are notified because both did something —
    -- the relevance rule that suppresses a notification about your own action does
    -- not apply when the action closes out the other side's money too.
    --
    -- Same amount text as the receipt, from the ledger row rather than a second
    -- derivation: a provider's invoice and a parent's receipt for one payment
    -- disagreeing about the figure would be the worst possible bug here.
    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @BookingId,
        @IsNightStay = 0,
        @Audience = N'Provider',
        @NotificationType = N'INVOICE_ISSUED',
        @Amount = @AmountText;

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [ServiceItemCode],
           [Source],
           [CustomerName],
           [CustomerMobileCountryCode],
           [CustomerMobile],
           [AnimalType],
           [PetName],
           [ServiceLocation],
           [CustomerLocation],
           [PricePerHour],
           [JobNotes],
           [PetId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
GO

-- MarkNightStayBookingPaid: night-stay mirror of MarkBookingPaid (App-only, so no
-- Custom check). THROWs: 51280 not found, 51281 not the provider, 51282 not
-- COMPLETED, 51283 already paid.
CREATE OR ALTER PROCEDURE [Booking].[MarkNightStayBookingPaid]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @Amount DECIMAL(10, 2),
    @PawfrontFee DECIMAL(10, 2),
    @PaymentMethod NVARCHAR(16),
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @RowPetParent UNIQUEIDENTIFIER;
    DECLARE @PayoutId NVARCHAR(64);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @RowProvider = [ProviderId],
           @RowPetParent = [PetParentId], @PayoutId = [PayoutId]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51280, 'Night stay booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51281, 'You are not the provider on this booking.', 1;
    END

    IF @CurrentStatus = N'PAID'
    BEGIN
        THROW 51283, 'Booking is already marked paid.', 1;
    END

    IF @CurrentStatus <> N'COMPLETED'
    BEGIN
        THROW 51282, 'Booking must be completed before it can be marked paid.', 1;
    END

    -- Backstop for stays completed before payout stamping shipped — see the
    -- single-day mirror.
    IF @PayoutId IS NULL
    BEGIN
        DECLARE @PayoutNumber BIGINT;
        SET @PayoutNumber = NEXT VALUE FOR [Booking].[PayoutNumberSequence];
        SET @PayoutId = N'PO-' + FORMAT(@PayoutNumber, N'D6');
    END

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'PAID',
        [UpdatedAtUtc] = @Now,
        [PayoutId] = COALESCE([PayoutId], @PayoutId),
        [PayoutStatus] = N'Paid'
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, N'PAID', N'Provider', @ProviderId, N'Payment received from parent');

    INSERT INTO [Booking].[BookingPayments]
        ([BookingType], [BookingId], [ProviderId], [PetParentId], [Amount], [PawfrontFee], [PaymentMethod], [PaidAtUtc])
    VALUES
        (N'NightStay', @NightStayBookingId, @ProviderId, @RowPetParent, @Amount, @PawfrontFee, @PaymentMethod, @Now);

    -- Where the provider was when they took the money.
    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[NightStayBookingLocationEvents]
            ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@NightStayBookingId, N'CashReceived', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    -- The ledger's figure, not a re-derivation — the receipt must match the row.
    DECLARE @AmountText NVARCHAR(64) =
        N'CHF ' + CONVERT(NVARCHAR(32), CAST(@Amount AS DECIMAL(12, 2)));

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @NightStayBookingId,
        @IsNightStay = 1,
        @Audience = N'PetParent',
        @NotificationType = N'BOOKING_PAID',
        @Amount = @AmountText;

    -- ...and the provider's invoice (V-S14). Mirror of Booking.MarkBookingPaid.
    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @NightStayBookingId,
        @IsNightStay = 1,
        @Audience = N'Provider',
        @NotificationType = N'INVOICE_ISSUED',
        @Amount = @AmountText;

    SELECT [NightStayBookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [CheckInDate],
           [CheckOutDate],
           [DropOffTime],
           [PickUpTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE [Booking].[RequestNightStayBookingModification]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @ProposedCheckInDate DATE,
    @ProposedCheckOutDate DATE,
    @Note NVARCHAR(500) = NULL,
    @HasAcknowledgedTerms BIT = 0,
    @AcknowledgedPricePerNight DECIMAL(10, 2) = NULL,
    @AcknowledgedCancellationPolicyHours INT = NULL,
    @AcknowledgedDropOffTime TIME(0) = NULL,
    @AcknowledgedPickUpTime TIME(0) = NULL,
    @AcknowledgedAddressLine NVARCHAR(500) = NULL,
    @AcknowledgedCity NVARCHAR(200) = NULL,
    @AcknowledgedZipCode NVARCHAR(32) = NULL,
    @AcknowledgedLatitude DECIMAL(9, 6) = NULL,
    @AcknowledgedLongitude DECIMAL(9, 6) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @CheckInDate DATE;
    DECLARE @DropOffTime TIME(0);
    -- Only used to turn the proposed check-out DATE into an instant for the
    -- notification's timezone conversion.
    DECLARE @PickUpTime TIME(0);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @ProviderId = [ProviderId], @PetParentId = [PetParentId],
           @CheckInDate = [CheckInDate], @DropOffTime = [DropOffTime],
           @PickUpTime = [PickUpTime]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51260, 'Night stay booking was not found.', 1;
    END

    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51261, 'You are not a party to this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION',
                              N'PARENT_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                              N'PARENT_DECLINED_MODIFICATION')
    BEGIN
        THROW 51262, 'A modification can only be requested on a confirmed booking.', 1;
    END

    -- The modification window closes 2 hours before drop-off on the check-in day
    -- (all times UTC), for either party's proposal.
    DECLARE @StartsAtUtc DATETIME2(7) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @DropOffTime),
                CAST(@CheckInDate AS DATETIME2(7)));

    IF @Now >= DATEADD(HOUR, -2, @StartsAtUtc)
    BEGIN
        THROW 51271, 'A booking can no longer be modified within 2 hours of the service start time.', 1;
    END

    IF EXISTS (SELECT 1 FROM [Booking].[NightStayBookingModifications] WHERE [NightStayBookingId] = @NightStayBookingId)
    BEGIN
        THROW 51263, 'A modification request is already awaiting a response.', 1;
    END

    -- Captured so the notification's dedupe key scopes to THIS proposal, letting a
    -- later proposal on the same stay notify again.
    DECLARE @InsertedModification TABLE ([NightStayBookingModificationId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[NightStayBookingModifications]
        ([NightStayBookingId], [RequestedByActor], [RequestedByActorId],
         [ProposedCheckInDate], [ProposedCheckOutDate], [RequestNote],
         [HasAcknowledgedTerms], [AcknowledgedPricePerNight], [AcknowledgedCancellationPolicyHours],
         [AcknowledgedDropOffTime], [AcknowledgedPickUpTime],
         [AcknowledgedAddressLine], [AcknowledgedCity], [AcknowledgedZipCode],
         [AcknowledgedLatitude], [AcknowledgedLongitude])
    OUTPUT inserted.[NightStayBookingModificationId] INTO @InsertedModification
    VALUES
        (@NightStayBookingId, @Actor, @ActorId, @ProposedCheckInDate, @ProposedCheckOutDate, @Note,
         ISNULL(@HasAcknowledgedTerms, 0), @AcknowledgedPricePerNight, @AcknowledgedCancellationPolicyHours,
         @AcknowledgedDropOffTime, @AcknowledgedPickUpTime,
         @AcknowledgedAddressLine, @AcknowledgedCity, @AcknowledgedZipCode,
         @AcknowledgedLatitude, @AcknowledgedLongitude);

    DECLARE @NewStatus NVARCHAR(48) =
        CASE WHEN @Actor = N'Provider' THEN N'MODIFICATION_REQUEST_BY_PROVIDER'
             ELSE N'MODIFICATION_REQUEST_BY_PARENT' END;

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = @NewStatus, [UpdatedAtUtc] = @Now
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

    -- The counterparty has to review it. Mirror of Booking.RequestBookingModification.
    DECLARE @ReqAudience NVARCHAR(16) =
        CASE WHEN @Actor = N'Provider' THEN N'PetParent' ELSE N'Provider' END;
    DECLARE @ReqType NVARCHAR(64) =
        CASE WHEN @Actor = N'Provider'
             THEN N'BOOKING_MODIFICATION_REQUESTED_BY_PROVIDER'
             ELSE N'BOOKING_MODIFICATION_REQUESTED_BY_PARENT' END;
    DECLARE @ReqDedupe NVARCHAR(64) =
        CAST((SELECT TOP 1 [NightStayBookingModificationId] FROM @InsertedModification) AS NVARCHAR(36));

    -- Both ends of the proposed stay as UTC instants, pinned to the booking's
    -- hand-over times so the renderer can localise them. A stay is proposed as a
    -- date range, so the copy's "new time" slot carries the new check-out DATE
    -- rather than a clock time — the renderer applies that for night-stay rows.
    DECLARE @ProposedStartUtc DATETIME2(0) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @DropOffTime),
                CAST(@ProposedCheckInDate AS DATETIME2(0)));
    DECLARE @ProposedCheckOutUtc DATETIME2(0) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @PickUpTime),
                CAST(@ProposedCheckOutDate AS DATETIME2(0)));

    -- How long the counterparty actually has, as the pair of readings the renderer
    -- turns into a length. Mirror of Booking.RequestBookingModification — see the
    -- note there for why a flat "24 hours" is wrong; the only difference is that a
    -- stay's service starts at drop-off on the check-in day.
    DECLARE @RequestedAtUtc DATETIME2(0) = @Now;
    DECLARE @ReviewByUtc DATETIME2(0) =
        CASE WHEN DATEADD(HOUR, 24, @Now) < DATEADD(HOUR, -2, @StartsAtUtc)
             THEN DATEADD(HOUR, 24, @Now)
             ELSE DATEADD(HOUR, -2, @StartsAtUtc) END;

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @NightStayBookingId,
        @IsNightStay = 1,
        @Audience = @ReqAudience,
        @NotificationType = @ReqType,
        @NewServiceStartUtc = @ProposedStartUtc,
        @NewCheckOutUtc = @ProposedCheckOutUtc,
        @ReviewByUtc = @ReviewByUtc,
        @RequestedAtUtc = @RequestedAtUtc,
        @DedupeSuffix = @ReqDedupe;

    SELECT [NightStayBookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [CheckInDate],
           [CheckOutDate],
           [DropOffTime],
           [PickUpTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE [Booking].[RespondNightStayBookingModification]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @Accept BIT,
    @Capacity INT,
    @Note NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @ServiceId UNIQUEIDENTIFIER;
    DECLARE @CheckInDate DATE;
    DECLARE @DropOffTime TIME(0);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @ProviderId = [ProviderId],
           @PetParentId = [PetParentId], @ServiceId = [ServiceId],
           @CheckInDate = [CheckInDate], @DropOffTime = [DropOffTime]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51265, 'Night stay booking was not found.', 1;
    END

    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51266, 'You are not a party to this booking.', 1;
    END

    -- An unanswered proposal â€” from either party â€” dies 2 hours before drop-off
    -- on the check-in day (all times UTC): reject the attempted response,
    -- whichever side is answering. Checked before the counterparty test so the
    -- rejection is the same whichever party calls. REJECT ONLY: the revert to
    -- CONFIRMED and the discard of the staging row are left to the scheduled
    -- external job, the single writer for time-driven status changes.
    IF @CurrentStatus IN (N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER')
    BEGIN
        DECLARE @StartsAtUtc DATETIME2(7) =
            DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @DropOffTime),
                    CAST(@CheckInDate AS DATETIME2(7)));

        -- Mirror of the single-day sproc: the 24-hour review window is the other
        -- deadline (2026-08-04), and whichever arrives first ends the proposal.
        IF EXISTS (
            SELECT 1 FROM [Booking].[NightStayBookingModifications]
            WHERE [NightStayBookingId] = @NightStayBookingId
              AND @Now >= DATEADD(HOUR, 24, [CreatedAtUtc]))
        BEGIN
            THROW 51272, 'The modification request timed out after 24 hours and can no longer be answered.', 1;
        END

        IF @Now >= DATEADD(HOUR, -2, @StartsAtUtc)
        BEGIN
            THROW 51272, 'The modification request expired 2 hours before the service start time and can no longer be answered.', 1;
        END
    END

    DECLARE @ExpectedStatus NVARCHAR(48) =
        CASE WHEN @Actor = N'Provider' THEN N'MODIFICATION_REQUEST_BY_PARENT'
             ELSE N'MODIFICATION_REQUEST_BY_PROVIDER' END;

    IF @CurrentStatus <> @ExpectedStatus
    BEGIN
        THROW 51267, 'There is no modification request awaiting your response.', 1;
    END

    DECLARE @ModId UNIQUEIDENTIFIER, @PIn DATE, @POut DATE;
    DECLARE @HasTerms BIT, @TPrice DECIMAL(10, 2), @TPolicyHours INT,
            @TDropOff TIME(0), @TPickUp TIME(0),
            @TAddressLine NVARCHAR(500), @TCity NVARCHAR(200), @TZipCode NVARCHAR(32),
            @TLatitude DECIMAL(9, 6), @TLongitude DECIMAL(9, 6);
    SELECT @ModId = [NightStayBookingModificationId], @PIn = [ProposedCheckInDate], @POut = [ProposedCheckOutDate],
           @HasTerms = [HasAcknowledgedTerms], @TPrice = [AcknowledgedPricePerNight],
           @TPolicyHours = [AcknowledgedCancellationPolicyHours],
           @TDropOff = [AcknowledgedDropOffTime], @TPickUp = [AcknowledgedPickUpTime],
           @TAddressLine = [AcknowledgedAddressLine], @TCity = [AcknowledgedCity],
           @TZipCode = [AcknowledgedZipCode], @TLatitude = [AcknowledgedLatitude],
           @TLongitude = [AcknowledgedLongitude]
    FROM [Booking].[NightStayBookingModifications] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @ModId IS NULL
    BEGIN
        THROW 51267, 'There is no modification request awaiting your response.', 1;
    END

    DECLARE @NewStatus NVARCHAR(48);

    IF @Accept = 1
    BEGIN
        -- Per-night capacity re-check on the proposed range, excluding this stay.
        DECLARE @FullNight DATE;
        ;WITH [Nights] AS
        (
            SELECT @PIn AS [Night]
            UNION ALL
            SELECT DATEADD(DAY, 1, [Night]) FROM [Nights] WHERE DATEADD(DAY, 1, [Night]) < @POut
        )
        SELECT TOP (1) @FullNight = n.[Night]
        FROM [Nights] n
        LEFT JOIN [Booking].[NightStayBookings] b WITH (UPDLOCK, HOLDLOCK)
            ON b.[ServiceId] = @ServiceId
           AND b.[NightStayBookingId] <> @NightStayBookingId
           AND b.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
           AND b.[CheckInDate] <= n.[Night]
           -- Nights released by an early pickup are available to a reschedule
           -- too — same expression [Booking].[CreateNightStayBooking] uses.
           AND COALESCE(b.[ActualCheckOutDate], b.[CheckOutDate]) > n.[Night]
        GROUP BY n.[Night]
        HAVING COUNT(b.[NightStayBookingId]) >= @Capacity
        OPTION (MAXRECURSION 366);

        IF @FullNight IS NOT NULL
        BEGIN
            THROW 51268, 'No remaining capacity for one or more proposed nights.', 1;
        END

        SET @NewStatus = CASE WHEN @Actor = N'Provider' THEN N'PROVIDER_ACCEPTED_MODIFICATION'
                              ELSE N'PARENT_ACCEPTED_MODIFICATION' END;

        -- Staging -> main: the proposed range, plus the acknowledged terms when the
        -- requester confirmed a drift. Drop-off / pick-up are NOT NULL on the stay,
        -- so a staged NULL falls back to the frozen value rather than failing.
        UPDATE [Booking].[NightStayBookings]
        SET [CheckInDate] = @PIn,
            [CheckOutDate] = @POut,
            [PricePerNight] = CASE WHEN @HasTerms = 1 THEN @TPrice ELSE [PricePerNight] END,
            [CancellationPolicyHours] = CASE WHEN @HasTerms = 1 THEN @TPolicyHours ELSE [CancellationPolicyHours] END,
            [DropOffTime] = CASE WHEN @HasTerms = 1 AND @TDropOff IS NOT NULL THEN @TDropOff ELSE [DropOffTime] END,
            [PickUpTime] = CASE WHEN @HasTerms = 1 AND @TPickUp IS NOT NULL THEN @TPickUp ELSE [PickUpTime] END,
            [SnapshotAddressLine] = CASE WHEN @HasTerms = 1 THEN @TAddressLine ELSE [SnapshotAddressLine] END,
            [SnapshotCity] = CASE WHEN @HasTerms = 1 THEN @TCity ELSE [SnapshotCity] END,
            [SnapshotZipCode] = CASE WHEN @HasTerms = 1 THEN @TZipCode ELSE [SnapshotZipCode] END,
            [SnapshotLatitude] = CASE WHEN @HasTerms = 1 THEN @TLatitude ELSE [SnapshotLatitude] END,
            [SnapshotLongitude] = CASE WHEN @HasTerms = 1 THEN @TLongitude ELSE [SnapshotLongitude] END,
            [Status] = @NewStatus,
            [UpdatedAtUtc] = @Now
        WHERE [NightStayBookingId] = @NightStayBookingId;
    END
    ELSE
    BEGIN
        SET @NewStatus = CASE WHEN @Actor = N'Provider' THEN N'PROVIDER_DECLINED_MODIFICATION'
                              ELSE N'PARENT_DECLINED_MODIFICATION' END;

        UPDATE [Booking].[NightStayBookings]
        SET [Status] = @NewStatus, [UpdatedAtUtc] = @Now
        WHERE [NightStayBookingId] = @NightStayBookingId;
    END

    DELETE FROM [Booking].[NightStayBookingModifications] WHERE [NightStayBookingModificationId] = @ModId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

    -- The responder tapped it; the REQUESTER is waiting to hear. Mirror of
    -- Booking.RespondBookingModification.
    DECLARE @RespAudience NVARCHAR(16) =
        CASE WHEN @Actor = N'Provider' THEN N'PetParent' ELSE N'Provider' END;
    DECLARE @RespType NVARCHAR(64) =
        CASE
            WHEN @Actor = N'Provider' AND @Accept = 1 THEN N'BOOKING_MODIFICATION_ACCEPTED_BY_PROVIDER'
            WHEN @Actor = N'Provider'                 THEN N'BOOKING_MODIFICATION_DECLINED_BY_PROVIDER'
            WHEN @Accept = 1                          THEN N'BOOKING_MODIFICATION_ACCEPTED_BY_PARENT'
            ELSE                                           N'BOOKING_MODIFICATION_DECLINED_BY_PARENT'
        END;

    -- Both ends of the proposed stay as UTC instants for the renderer to localise.
    -- The hand-over times are re-read from the row rather than recomputed from the
    -- acknowledged-terms CASEs above: on accept the UPDATE has already applied
    -- them, on decline the row is untouched, so this is correct either way without
    -- a second copy of that logic. As with the request side, the copy's "new time"
    -- slot carries the new check-out DATE — the renderer applies that for
    -- night-stay rows.
    DECLARE @EffDropOffTime TIME(0), @EffPickUpTime TIME(0);
    SELECT @EffDropOffTime = [DropOffTime], @EffPickUpTime = [PickUpTime]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    DECLARE @RespStartUtc DATETIME2(0) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @EffDropOffTime),
                CAST(@PIn AS DATETIME2(0)));
    DECLARE @RespCheckOutUtc DATETIME2(0) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @EffPickUpTime),
                CAST(@POut AS DATETIME2(0)));
    DECLARE @RespDedupe NVARCHAR(64) = CAST(@ModId AS NVARCHAR(36));

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @NightStayBookingId,
        @IsNightStay = 1,
        @Audience = @RespAudience,
        @NotificationType = @RespType,
        @NewServiceStartUtc = @RespStartUtc,
        @NewCheckOutUtc = @RespCheckOutUtc,
        @DedupeSuffix = @RespDedupe;

    SELECT [NightStayBookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [CheckInDate],
           [CheckOutDate],
           [DropOffTime],
           [PickUpTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE [Booking].[GetPendingNightStayBookingModification]
    @NightStayBookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [NightStayBookingModificationId] AS [BookingModificationId], [NightStayBookingId] AS [BookingId],
           [RequestedByActor], [RequestedByActorId],
           [ProposedCheckInDate], [ProposedCheckOutDate], [RequestNote], [CreatedAtUtc],
           [HasAcknowledgedTerms], [AcknowledgedPricePerNight], [AcknowledgedCancellationPolicyHours],
           [AcknowledgedDropOffTime], [AcknowledgedPickUpTime],
           [AcknowledgedAddressLine], [AcknowledgedCity], [AcknowledgedZipCode],
           [AcknowledgedLatitude], [AcknowledgedLongitude]
    FROM [Booking].[NightStayBookingModifications] WHERE [NightStayBookingId] = @NightStayBookingId;
END;
GO

CREATE OR ALTER PROCEDURE [Booking].[AddNightStayBookingEvidence]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000),
    @Latitude DECIMAL(9, 6) = NULL,
    @Longitude DECIMAL(9, 6) = NULL,
    @AccuracyMetres DECIMAL(9, 2) = NULL,
    @DeviceCapturedAtUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1 FROM [Booking].[NightStayBookings]
        WHERE [NightStayBookingId] = @NightStayBookingId AND [ProviderId] = @ProviderId)
    BEGIN
        THROW 51270, 'Night stay booking was not found for this provider.', 1;
    END

    DECLARE @Inserted TABLE ([NightStayBookingEvidenceId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[NightStayBookingEvidence] ([NightStayBookingId], [PhotoUrl])
    OUTPUT inserted.[NightStayBookingEvidenceId] INTO @Inserted
    VALUES (@NightStayBookingId, @PhotoUrl);

    IF @Latitude IS NOT NULL AND @Longitude IS NOT NULL
    BEGIN
        INSERT INTO [Booking].[NightStayBookingLocationEvents]
            ([NightStayBookingId], [Trigger], [CapturedByType], [CapturedById],
             [Latitude], [Longitude], [AccuracyMetres], [DeviceCapturedAtUtc])
        VALUES
            (@NightStayBookingId, N'EvidenceCaptured', N'Provider', @ProviderId,
             @Latitude, @Longitude, @AccuracyMetres, @DeviceCapturedAtUtc);
    END

    SELECT [NightStayBookingEvidenceId] AS [BookingEvidenceId], [NightStayBookingId] AS [BookingId],
           [PhotoUrl], [CreatedAtUtc]
    FROM [Booking].[NightStayBookingEvidence]
    WHERE [NightStayBookingEvidenceId] = (SELECT TOP (1) [NightStayBookingEvidenceId] FROM @Inserted);

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE [Booking].[ListNightStayBookingEvidence]
    @NightStayBookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SELECT [NightStayBookingEvidenceId] AS [BookingEvidenceId], [NightStayBookingId] AS [BookingId], [PhotoUrl], [CreatedAtUtc]
    FROM [Booking].[NightStayBookingEvidence] WHERE [NightStayBookingId] = @NightStayBookingId ORDER BY [CreatedAtUtc] ASC;
END;
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
    @IsPaid BIT = 0,
    @Price DECIMAL(18, 2) = NULL,
    @CancellationPolicy NVARCHAR(32) = NULL,
    @EventLink NVARCHAR(1000) = NULL,
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
        [BannerImageUrl], [EventType], [StartDate], [EndDate], [StartTime], [EndTime],
        [IsPaid], [Price], [CancellationPolicy], [EventLink]
    )
    OUTPUT inserted.[EventId] INTO @InsertedEventId
    VALUES
    (
        @ProviderId, @EventCategory, @IsChildFriendly, @Title, @Description,
        @BannerImageUrl, @EventType, @StartDate, @EndDate, @StartTime, @EndTime,
        @IsPaid, CASE WHEN @IsPaid = 1 THEN @Price ELSE NULL END, @CancellationPolicy, @EventLink
    );

    DECLARE @EventId UNIQUEIDENTIFIER = (SELECT TOP (1) [EventId] FROM @InsertedEventId);

    INSERT INTO [Event].[EventAmenities] ([EventId], [Amenity])
    SELECT DISTINCT @EventId, [value]
    FROM OPENJSON(@AmenitiesJson)
    WHERE [value] IS NOT NULL AND LEN(LTRIM(RTRIM([value]))) > 0;

    SELECT e.[EventId], e.[ProviderId], e.[PetParentId], e.[EventCategory], e.[IsChildFriendly], e.[Title],
           e.[Description], e.[BannerImageUrl], e.[EventType], e.[StartDate], e.[EndDate],
           e.[StartTime], e.[EndTime], e.[CreatedAtUtc], e.[UpdatedAtUtc],
           e.[ViewCount], e.[ShareCount], e.[InquiryCount], e.[IsPaid], e.[Price], e.[CancellationPolicy],
           COALESCE(org_pr.[FirstName] + N' ' + org_pr.[LastName],
                    org_pp.[FirstName] + N' ' + org_pp.[LastName]) AS [OrganizerName],
           org_pp.[ProfilePhotoUrl] AS [OrganizerImageUrl],
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed') AS [TotalBookings],
           e.[EventLink]
    FROM [Event].[Events] e
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    WHERE e.[EventId] = @EventId;

    SELECT [Amenity]
    FROM [Event].[EventAmenities]
    WHERE [EventId] = @EventId
    ORDER BY [Amenity];

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Event].[CreateEvent].';
GO


-- 3.10b Event.CreatePetParentEvent --------------------------------------------
-- Mirror of [Event].[CreateEvent] for parent-organised events. The shape of
-- the inserted row is identical; only the organiser column differs.
CREATE OR ALTER PROCEDURE [Event].[CreatePetParentEvent]
    @PetParentId UNIQUEIDENTIFIER,
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
    @IsPaid BIT = 0,
    @Price DECIMAL(18, 2) = NULL,
    @CancellationPolicy NVARCHAR(32) = NULL,
    @EventLink NVARCHAR(1000) = NULL,
    @AmenitiesJson NVARCHAR(MAX) = N'[]'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51207, 'Pet parent was not found.', 1;
    END

    DECLARE @InsertedEventId TABLE (EventId UNIQUEIDENTIFIER);

    INSERT INTO [Event].[Events]
    (
        [PetParentId], [EventCategory], [IsChildFriendly], [Title], [Description],
        [BannerImageUrl], [EventType], [StartDate], [EndDate], [StartTime], [EndTime],
        [IsPaid], [Price], [CancellationPolicy], [EventLink]
    )
    OUTPUT inserted.[EventId] INTO @InsertedEventId
    VALUES
    (
        @PetParentId, @EventCategory, @IsChildFriendly, @Title, @Description,
        @BannerImageUrl, @EventType, @StartDate, @EndDate, @StartTime, @EndTime,
        @IsPaid, CASE WHEN @IsPaid = 1 THEN @Price ELSE NULL END, @CancellationPolicy, @EventLink
    );

    DECLARE @EventId UNIQUEIDENTIFIER = (SELECT TOP (1) [EventId] FROM @InsertedEventId);

    INSERT INTO [Event].[EventAmenities] ([EventId], [Amenity])
    SELECT DISTINCT @EventId, [value]
    FROM OPENJSON(@AmenitiesJson)
    WHERE [value] IS NOT NULL AND LEN(LTRIM(RTRIM([value]))) > 0;

    SELECT e.[EventId], e.[ProviderId], e.[PetParentId], e.[EventCategory], e.[IsChildFriendly], e.[Title],
           e.[Description], e.[BannerImageUrl], e.[EventType], e.[StartDate], e.[EndDate],
           e.[StartTime], e.[EndTime], e.[CreatedAtUtc], e.[UpdatedAtUtc],
           e.[ViewCount], e.[ShareCount], e.[InquiryCount], e.[IsPaid], e.[Price], e.[CancellationPolicy],
           COALESCE(org_pr.[FirstName] + N' ' + org_pr.[LastName],
                    org_pp.[FirstName] + N' ' + org_pp.[LastName]) AS [OrganizerName],
           org_pp.[ProfilePhotoUrl] AS [OrganizerImageUrl],
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed') AS [TotalBookings],
           e.[EventLink]
    FROM [Event].[Events] e
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    WHERE e.[EventId] = @EventId;

    SELECT [Amenity]
    FROM [Event].[EventAmenities]
    WHERE [EventId] = @EventId
    ORDER BY [Amenity];

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Event].[CreatePetParentEvent].';
GO


CREATE OR ALTER PROCEDURE [Event].[GetEvent]
    @EventId UNIQUEIDENTIFIER,
    -- The caller, so a blocked pair never see each other's events. Both NULL
    -- for a legacy or unauthenticated caller, which filters nothing.
    @ViewerType NVARCHAR(16)     = NULL,
    @ViewerId   UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- A block hides each party's events from the other, both ways. Rather than
    -- repeat the predicate on all four result sets below -- and risk one being
    -- forgotten, leaking the attendee list for an event the caller must not see
    -- -- the id itself is cleared. Every result set then returns nothing and the
    -- caller maps the empty first one to 404, exactly as it does for an unknown
    -- id. A blocked event and a non-existent one are deliberately the same
    -- answer: distinguishing them would confirm the other party acted.
    IF @ViewerId IS NOT NULL AND EXISTS (
        SELECT 1
        FROM [Event].[Events] e
        INNER JOIN [Block].[BlockedParticipants] bp
            ON (bp.[BlockerType] = @ViewerType AND bp.[BlockerId] = @ViewerId
                AND bp.[BlockedId] = COALESCE(e.[ProviderId], e.[PetParentId]))
            OR (bp.[BlockedType] = @ViewerType AND bp.[BlockedId] = @ViewerId
                AND bp.[BlockerId] = COALESCE(e.[ProviderId], e.[PetParentId]))
        WHERE e.[EventId] = @EventId)
    BEGIN
        SET @EventId = NULL;
    END

    -- Result set 1: event row (zero or one). One of ProviderId / PetParentId is NULL.
    -- OrganizerName / OrganizerImageUrl are joined from whichever organiser
    -- created the event (image is null for provider organisers).
    SELECT e.[EventId],
           e.[ProviderId],
           e.[PetParentId],
           e.[EventCategory],
           e.[IsChildFriendly],
           e.[Title],
           e.[Description],
           e.[BannerImageUrl],
           e.[EventType],
           e.[StartDate],
           e.[EndDate],
           e.[StartTime],
           e.[EndTime],
           e.[CreatedAtUtc],
           e.[UpdatedAtUtc],
           e.[ViewCount],
           e.[ShareCount],
           e.[InquiryCount],
           e.[IsPaid],
           e.[Price],
           e.[CancellationPolicy],
           COALESCE(org_pr.[FirstName] + N' ' + org_pr.[LastName],
                    org_pp.[FirstName] + N' ' + org_pp.[LastName]) AS [OrganizerName],
           org_pp.[ProfilePhotoUrl] AS [OrganizerImageUrl],
           -- Total tickets booked (non-cancelled) — the "total bookings done".
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed') AS [TotalBookings],
           e.[EventLink]
    FROM [Event].[Events] e
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    WHERE e.[EventId] = @EventId;

    -- Result set 2: amenities
    SELECT [Amenity]
    FROM [Event].[EventAmenities]
    WHERE [EventId] = @EventId
    ORDER BY [Amenity];

    -- Result set 3: payment options (payout methods Cash / Digital).
    SELECT [PayoutMethod]
    FROM [Event].[EventPayoutMethods]
    WHERE [EventId] = @EventId
    ORDER BY [PayoutMethod];

    -- Result set 4: attendees — names only (+ ticket number), non-cancelled
    -- bookings. Booker contact / payment stay on the organiser-only dashboard.
    SELECT t.[AttendeeName],
           t.[TicketNumber]
    FROM [Event].[EventBookingTickets] t
    INNER JOIN [Event].[EventBookings] b ON b.[BookingId] = t.[BookingId]
    WHERE t.[EventId] = @EventId
      AND b.[Status] = N'Confirmed'
    ORDER BY b.[CreatedAtUtc] ASC, t.[TicketNumber] ASC;
END;
GO
PRINT 'Created/updated [Event].[GetEvent].';
GO


-- 3.11a Event.UpdateEvent -----------------------------------------------------
-- Full-replace edit of a provider-organised event (THROW 51216 when not found
-- / not owned). Returns GetEvent's four result sets. Cosmos physical extension
-- is reconciled by the app layer.
CREATE OR ALTER PROCEDURE [Event].[UpdateEvent]
    @EventId UNIQUEIDENTIFIER,
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
    @IsPaid BIT = 0,
    @Price DECIMAL(18, 2) = NULL,
    @CancellationPolicy NVARCHAR(32) = NULL,
    @EventLink NVARCHAR(1000) = NULL,
    @AmenitiesJson NVARCHAR(MAX) = N'[]'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1 FROM [Event].[Events]
        WHERE [EventId] = @EventId AND [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51216, 'Event was not found for this provider.', 1;
    END

    UPDATE [Event].[Events]
    SET [EventCategory]      = @EventCategory,
        [IsChildFriendly]    = @IsChildFriendly,
        [Title]              = @Title,
        [Description]        = @Description,
        [BannerImageUrl]     = @BannerImageUrl,
        [EventType]          = @EventType,
        [StartDate]          = @StartDate,
        [EndDate]            = @EndDate,
        [StartTime]          = @StartTime,
        [EndTime]            = @EndTime,
        [IsPaid]             = @IsPaid,
        [Price]              = CASE WHEN @IsPaid = 1 THEN @Price ELSE NULL END,
        [CancellationPolicy] = @CancellationPolicy,
        [EventLink] = @EventLink,
        [UpdatedAtUtc]       = SYSUTCDATETIME()
    WHERE [EventId] = @EventId;

    DELETE FROM [Event].[EventAmenities] WHERE [EventId] = @EventId;
    INSERT INTO [Event].[EventAmenities] ([EventId], [Amenity])
    SELECT DISTINCT @EventId, [value]
    FROM OPENJSON(@AmenitiesJson)
    WHERE [value] IS NOT NULL AND LEN(LTRIM(RTRIM([value]))) > 0;

    IF @IsPaid = 0
    BEGIN
        DELETE FROM [Event].[EventPayoutMethods] WHERE [EventId] = @EventId;
    END

    SELECT e.[EventId], e.[ProviderId], e.[PetParentId], e.[EventCategory], e.[IsChildFriendly], e.[Title],
           e.[Description], e.[BannerImageUrl], e.[EventType], e.[StartDate], e.[EndDate],
           e.[StartTime], e.[EndTime], e.[CreatedAtUtc], e.[UpdatedAtUtc],
           e.[ViewCount], e.[ShareCount], e.[InquiryCount], e.[IsPaid], e.[Price], e.[CancellationPolicy],
           COALESCE(org_pr.[FirstName] + N' ' + org_pr.[LastName],
                    org_pp.[FirstName] + N' ' + org_pp.[LastName]) AS [OrganizerName],
           org_pp.[ProfilePhotoUrl] AS [OrganizerImageUrl],
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed') AS [TotalBookings],
           e.[EventLink]
    FROM [Event].[Events] e
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    WHERE e.[EventId] = @EventId;

    SELECT [Amenity]
    FROM [Event].[EventAmenities]
    WHERE [EventId] = @EventId
    ORDER BY [Amenity];

    SELECT [PayoutMethod]
    FROM [Event].[EventPayoutMethods]
    WHERE [EventId] = @EventId
    ORDER BY [PayoutMethod];

    SELECT t.[AttendeeName],
           t.[TicketNumber]
    FROM [Event].[EventBookingTickets] t
    INNER JOIN [Event].[EventBookings] b ON b.[BookingId] = t.[BookingId]
    WHERE t.[EventId] = @EventId
      AND b.[Status] = N'Confirmed'
    ORDER BY b.[CreatedAtUtc] ASC, t.[TicketNumber] ASC;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Event].[UpdateEvent].';
GO


-- 3.11b Event.UpdatePetParentEvent --------------------------------------------
-- Mirror of [Event].[UpdateEvent] keyed by @PetParentId (THROW 51217).
CREATE OR ALTER PROCEDURE [Event].[UpdatePetParentEvent]
    @EventId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
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
    @IsPaid BIT = 0,
    @Price DECIMAL(18, 2) = NULL,
    @CancellationPolicy NVARCHAR(32) = NULL,
    @EventLink NVARCHAR(1000) = NULL,
    @AmenitiesJson NVARCHAR(MAX) = N'[]'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1 FROM [Event].[Events]
        WHERE [EventId] = @EventId AND [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51217, 'Event was not found for this pet parent.', 1;
    END

    UPDATE [Event].[Events]
    SET [EventCategory]      = @EventCategory,
        [IsChildFriendly]    = @IsChildFriendly,
        [Title]              = @Title,
        [Description]        = @Description,
        [BannerImageUrl]     = @BannerImageUrl,
        [EventType]          = @EventType,
        [StartDate]          = @StartDate,
        [EndDate]            = @EndDate,
        [StartTime]          = @StartTime,
        [EndTime]            = @EndTime,
        [IsPaid]             = @IsPaid,
        [Price]              = CASE WHEN @IsPaid = 1 THEN @Price ELSE NULL END,
        [CancellationPolicy] = @CancellationPolicy,
        [EventLink] = @EventLink,
        [UpdatedAtUtc]       = SYSUTCDATETIME()
    WHERE [EventId] = @EventId;

    DELETE FROM [Event].[EventAmenities] WHERE [EventId] = @EventId;
    INSERT INTO [Event].[EventAmenities] ([EventId], [Amenity])
    SELECT DISTINCT @EventId, [value]
    FROM OPENJSON(@AmenitiesJson)
    WHERE [value] IS NOT NULL AND LEN(LTRIM(RTRIM([value]))) > 0;

    IF @IsPaid = 0
    BEGIN
        DELETE FROM [Event].[EventPayoutMethods] WHERE [EventId] = @EventId;
    END

    SELECT e.[EventId], e.[ProviderId], e.[PetParentId], e.[EventCategory], e.[IsChildFriendly], e.[Title],
           e.[Description], e.[BannerImageUrl], e.[EventType], e.[StartDate], e.[EndDate],
           e.[StartTime], e.[EndTime], e.[CreatedAtUtc], e.[UpdatedAtUtc],
           e.[ViewCount], e.[ShareCount], e.[InquiryCount], e.[IsPaid], e.[Price], e.[CancellationPolicy],
           COALESCE(org_pr.[FirstName] + N' ' + org_pr.[LastName],
                    org_pp.[FirstName] + N' ' + org_pp.[LastName]) AS [OrganizerName],
           org_pp.[ProfilePhotoUrl] AS [OrganizerImageUrl],
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed') AS [TotalBookings],
           e.[EventLink]
    FROM [Event].[Events] e
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    WHERE e.[EventId] = @EventId;

    SELECT [Amenity]
    FROM [Event].[EventAmenities]
    WHERE [EventId] = @EventId
    ORDER BY [Amenity];

    SELECT [PayoutMethod]
    FROM [Event].[EventPayoutMethods]
    WHERE [EventId] = @EventId
    ORDER BY [PayoutMethod];

    SELECT t.[AttendeeName],
           t.[TicketNumber]
    FROM [Event].[EventBookingTickets] t
    INNER JOIN [Event].[EventBookings] b ON b.[BookingId] = t.[BookingId]
    WHERE t.[EventId] = @EventId
      AND b.[Status] = N'Confirmed'
    ORDER BY b.[CreatedAtUtc] ASC, t.[TicketNumber] ASC;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Event].[UpdatePetParentEvent].';
GO


-- 3.12 Event.ListEventsByProvider --------------------------------------------
CREATE OR ALTER PROCEDURE [Event].[ListEventsByProvider]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT e.[EventId], e.[ProviderId], e.[PetParentId], e.[EventCategory], e.[IsChildFriendly], e.[Title],
           e.[Description], e.[BannerImageUrl], e.[EventType], e.[StartDate], e.[EndDate],
           e.[StartTime], e.[EndTime], e.[CreatedAtUtc], e.[UpdatedAtUtc],
           e.[ViewCount], e.[ShareCount], e.[InquiryCount], e.[IsPaid], e.[Price], e.[CancellationPolicy],
           COALESCE(org_pr.[FirstName] + N' ' + org_pr.[LastName],
                    org_pp.[FirstName] + N' ' + org_pp.[LastName]) AS [OrganizerName],
           org_pp.[ProfilePhotoUrl] AS [OrganizerImageUrl],
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed') AS [TotalBookings],
           e.[EventLink]
    FROM [Event].[Events] e
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    WHERE e.[ProviderId] = @ProviderId
    ORDER BY e.[StartDate] DESC, e.[StartTime] DESC;

    SELECT a.[EventId], a.[Amenity]
    FROM [Event].[EventAmenities] a
    INNER JOIN [Event].[Events] e ON e.[EventId] = a.[EventId]
    WHERE e.[ProviderId] = @ProviderId
    ORDER BY a.[EventId], a.[Amenity];
END;
GO
PRINT 'Created/updated [Event].[ListEventsByProvider].';
GO


-- 3.12a Event.ListEventsByPetParent ------------------------------------------
CREATE OR ALTER PROCEDURE [Event].[ListEventsByPetParent]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT e.[EventId], e.[ProviderId], e.[PetParentId], e.[EventCategory], e.[IsChildFriendly], e.[Title],
           e.[Description], e.[BannerImageUrl], e.[EventType], e.[StartDate], e.[EndDate],
           e.[StartTime], e.[EndTime], e.[CreatedAtUtc], e.[UpdatedAtUtc],
           e.[ViewCount], e.[ShareCount], e.[InquiryCount], e.[IsPaid], e.[Price], e.[CancellationPolicy],
           COALESCE(org_pr.[FirstName] + N' ' + org_pr.[LastName],
                    org_pp.[FirstName] + N' ' + org_pp.[LastName]) AS [OrganizerName],
           org_pp.[ProfilePhotoUrl] AS [OrganizerImageUrl],
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed') AS [TotalBookings],
           e.[EventLink]
    FROM [Event].[Events] e
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    WHERE e.[PetParentId] = @PetParentId
    ORDER BY e.[StartDate] DESC, e.[StartTime] DESC;

    SELECT a.[EventId], a.[Amenity]
    FROM [Event].[EventAmenities] a
    INNER JOIN [Event].[Events] e ON e.[EventId] = a.[EventId]
    WHERE e.[PetParentId] = @PetParentId
    ORDER BY a.[EventId], a.[Amenity];
END;
GO
PRINT 'Created/updated [Event].[ListEventsByPetParent].';
GO


CREATE OR ALTER PROCEDURE [Event].[ListEvents]
    @EventCategory   NVARCHAR(64)  = NULL,
    @EventType       NVARCHAR(32)  = NULL,
    @StartDate       DATE          = NULL,
    @EndDate         DATE          = NULL,
    @IsChildFriendly BIT           = NULL,
    -- JSON array of amenity codes (e.g. N'["Restrooms","FreeParking"]').
    -- When supplied, only events that carry EVERY listed amenity are returned.
    @AmenitiesJson   NVARCHAR(MAX) = NULL,
    -- Optional free-text title search. When supplied, only events whose Title
    -- CONTAINS the term (case-insensitive) are returned.
    @Title           NVARCHAR(200) = NULL,
    -- The caller, so a blocked pair never see each other's events. Both NULL
    -- for a legacy or unauthenticated caller, which filters nothing.
    @ViewerType      NVARCHAR(16)     = NULL,
    @ViewerId        UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Build a case-insensitive "contains" LIKE pattern for the title search.
    -- LIKE metacharacters in the user term are escaped (ESCAPE N'\') so they
    -- match literally, and LOWER() on both sides makes the match
    -- case-insensitive regardless of the database/column collation. NULL/blank
    -- term means "no title filter".
    DECLARE @TitlePattern NVARCHAR(410) = NULL;
    IF (@Title IS NOT NULL AND LTRIM(RTRIM(@Title)) <> N'')
    BEGIN
        SET @TitlePattern = N'%' +
            REPLACE(REPLACE(REPLACE(REPLACE(
                LOWER(LTRIM(RTRIM(@Title))),
                N'\', N'\\'), N'%', N'\%'), N'_', N'\_'), N'[', N'\[') + N'%';
    END

    -- Materialise the requested amenity filter into a table variable once so we
    -- can both count it and join on it without re-parsing the JSON.
    DECLARE @Amenities TABLE ([Amenity] NVARCHAR(64) NOT NULL PRIMARY KEY);

    IF (@AmenitiesJson IS NOT NULL AND LTRIM(RTRIM(@AmenitiesJson)) <> N'')
    BEGIN
        INSERT INTO @Amenities ([Amenity])
        SELECT DISTINCT [value]
        FROM OPENJSON(@AmenitiesJson)
        WHERE [value] IS NOT NULL AND LTRIM(RTRIM([value])) <> N'';
    END

    DECLARE @AmenityCount INT = (SELECT COUNT(*) FROM @Amenities);

    -- Result set 1: event rows
    ;WITH FilteredEvents AS
    (
        SELECT e.[EventId]
        FROM [Event].[Events] e
        WHERE (@EventCategory   IS NULL OR e.[EventCategory]   = @EventCategory)
          AND (@EventType       IS NULL OR e.[EventType]       = @EventType)
          AND (@IsChildFriendly IS NULL OR e.[IsChildFriendly] = @IsChildFriendly)
          AND (@TitlePattern IS NULL OR LOWER(e.[Title]) LIKE @TitlePattern ESCAPE N'\')
          -- Date-range filter: event's [StartDate, EndDate] must overlap the
          -- caller's [@StartDate, @EndDate]. Each bound is independently optional.
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
          -- A block hides each party's events from the other, both ways. The
          -- organiser is whichever of the two id columns is set (a CHECK enforces
          -- exactly one), and GUIDs are globally unique, so matching on the id alone
          -- is safe without branching on organiser type -- the same reasoning the
          -- self-booking check uses.
          AND (@ViewerId IS NULL OR NOT EXISTS (
                SELECT 1
                FROM [Block].[BlockedParticipants] bp
                WHERE (bp.[BlockerType] = @ViewerType AND bp.[BlockerId] = @ViewerId
                       AND bp.[BlockedId] = COALESCE(e.[ProviderId], e.[PetParentId]))
                   OR (bp.[BlockedType] = @ViewerType AND bp.[BlockedId] = @ViewerId
                       AND bp.[BlockerId] = COALESCE(e.[ProviderId], e.[PetParentId]))
              ))
    )
    SELECT e.[EventId], e.[ProviderId], e.[PetParentId], e.[EventCategory], e.[IsChildFriendly],
           e.[Title], e.[Description], e.[BannerImageUrl], e.[EventType],
           e.[StartDate], e.[EndDate], e.[StartTime], e.[EndTime],
           e.[CreatedAtUtc], e.[UpdatedAtUtc],
           e.[ViewCount], e.[ShareCount], e.[InquiryCount],
           e.[IsPaid], e.[Price], e.[CancellationPolicy],
           COALESCE(org_pr.[FirstName] + N' ' + org_pr.[LastName],
                    org_pp.[FirstName] + N' ' + org_pp.[LastName]) AS [OrganizerName],
           org_pp.[ProfilePhotoUrl] AS [OrganizerImageUrl],
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed') AS [TotalBookings],
           e.[EventLink]
    FROM [Event].[Events] e
    INNER JOIN FilteredEvents f ON f.[EventId] = e.[EventId]
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    ORDER BY e.[StartDate] DESC, e.[StartTime] DESC, e.[EventId] ASC;

    -- Result set 2: (EventId, Amenity) pairs for the events above.
    SELECT a.[EventId], a.[Amenity]
    FROM [Event].[EventAmenities] a
    INNER JOIN [Event].[Events] e ON e.[EventId] = a.[EventId]
    WHERE (@EventCategory   IS NULL OR e.[EventCategory]   = @EventCategory)
      AND (@EventType       IS NULL OR e.[EventType]       = @EventType)
      AND (@IsChildFriendly IS NULL OR e.[IsChildFriendly] = @IsChildFriendly)
      AND (@TitlePattern IS NULL OR LOWER(e.[Title]) LIKE @TitlePattern ESCAPE N'\')
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
      -- A block hides each party's events from the other, both ways. The
      -- organiser is whichever of the two id columns is set (a CHECK enforces
      -- exactly one), and GUIDs are globally unique, so matching on the id alone
      -- is safe without branching on organiser type -- the same reasoning the
      -- self-booking check uses.
      AND (@ViewerId IS NULL OR NOT EXISTS (
            SELECT 1
            FROM [Block].[BlockedParticipants] bp
            WHERE (bp.[BlockerType] = @ViewerType AND bp.[BlockerId] = @ViewerId
                   AND bp.[BlockedId] = COALESCE(e.[ProviderId], e.[PetParentId]))
               OR (bp.[BlockedType] = @ViewerType AND bp.[BlockedId] = @ViewerId
                   AND bp.[BlockerId] = COALESCE(e.[ProviderId], e.[PetParentId]))
          ))
    ORDER BY a.[EventId], a.[Amenity];
END;
GO
PRINT 'Created/updated [Event].[ListEvents].';
GO


-- Trending events: ranked by engagement = ViewCount + ShareCount + total
-- (non-cancelled / Confirmed) ticket bookings, highest first. Returns the same
-- two result sets as [Event].[ListEvents] (event rows + amenities) so the
-- application reader (ReadEventRow) is shared. @Take caps the number of rows
-- (default 20, clamped to 1..100).
CREATE OR ALTER PROCEDURE [Event].[ListTrendingEvents]
    @Take INT = 20,
    -- The caller, so a blocked pair never see each other's events. Both NULL
    -- for a legacy or unauthenticated caller, which filters nothing.
    @ViewerType      NVARCHAR(16)     = NULL,
    @ViewerId        UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF (@Take IS NULL OR @Take < 1) SET @Take = 20;
    IF (@Take > 100) SET @Take = 100;

    -- Pick the top events once so both result sets share the same set.
    DECLARE @TopEvents TABLE
    (
        [EventId]       UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        [TrendingScore] INT              NOT NULL,
        [StartDate]     DATE             NOT NULL
    );

    INSERT INTO @TopEvents ([EventId], [TrendingScore], [StartDate])
    SELECT TOP (@Take)
           e.[EventId],
           e.[ViewCount] + e.[ShareCount] +
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed'),
           e.[StartDate]
    FROM [Event].[Events] e
    -- Filtered BEFORE the TOP, so an event the caller cannot see does not
    -- consume one of the @Take slots and silently shorten their list.
    -- A block hides each party's events from the other, both ways. The
    -- organiser is whichever of the two id columns is set (a CHECK enforces
    -- exactly one), and GUIDs are globally unique, so matching on the id alone
    -- is safe without branching on organiser type -- the same reasoning the
    -- self-booking check uses.
    WHERE (@ViewerId IS NULL OR NOT EXISTS (
          SELECT 1
          FROM [Block].[BlockedParticipants] bp
          WHERE (bp.[BlockerType] = @ViewerType AND bp.[BlockerId] = @ViewerId
                 AND bp.[BlockedId] = COALESCE(e.[ProviderId], e.[PetParentId]))
             OR (bp.[BlockedType] = @ViewerType AND bp.[BlockedId] = @ViewerId
                 AND bp.[BlockerId] = COALESCE(e.[ProviderId], e.[PetParentId]))
        ))
    ORDER BY e.[ViewCount] + e.[ShareCount] +
             (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
              FROM [Event].[EventBookings] eb
              WHERE eb.[EventId] = e.[EventId]
                AND eb.[Status] = N'Confirmed') DESC,
             e.[StartDate] DESC,
             e.[EventId] ASC;

    -- Result set 1: event rows (same column shape as [Event].[ListEvents]),
    -- ordered by the trending score (most engaging first).
    SELECT e.[EventId], e.[ProviderId], e.[PetParentId], e.[EventCategory], e.[IsChildFriendly],
           e.[Title], e.[Description], e.[BannerImageUrl], e.[EventType],
           e.[StartDate], e.[EndDate], e.[StartTime], e.[EndTime],
           e.[CreatedAtUtc], e.[UpdatedAtUtc],
           e.[ViewCount], e.[ShareCount], e.[InquiryCount],
           e.[IsPaid], e.[Price], e.[CancellationPolicy],
           COALESCE(org_pr.[FirstName] + N' ' + org_pr.[LastName],
                    org_pp.[FirstName] + N' ' + org_pp.[LastName]) AS [OrganizerName],
           org_pp.[ProfilePhotoUrl] AS [OrganizerImageUrl],
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed') AS [TotalBookings],
           e.[EventLink]
    FROM [Event].[Events] e
    INNER JOIN @TopEvents te ON te.[EventId] = e.[EventId]
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    ORDER BY te.[TrendingScore] DESC, te.[StartDate] DESC, e.[EventId] ASC;

    -- Result set 2: (EventId, Amenity) pairs for the events above.
    SELECT a.[EventId], a.[Amenity]
    FROM [Event].[EventAmenities] a
    INNER JOIN @TopEvents te ON te.[EventId] = a.[EventId]
    ORDER BY a.[EventId], a.[Amenity];
END;
GO
PRINT 'Created/updated [Event].[ListTrendingEvents].';
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
    WHERE b.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
      AND b.[BookingDate] BETWEEN @StartDate AND @EndDate
      AND (
          @StartTime IS NULL
          -- A booking occupies up to COALESCE([ActualEndTime], [EndTime]) —
          -- the same expression the capacity checks use — so a job that
          -- finished early does not stand in the way of closing the hours it
          -- released. There is nothing left to move or cancel.
          OR (b.[StartTime] < @EndTime AND COALESCE(b.[ActualEndTime], b.[EndTime]) > @StartTime)
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
    -- NULL for online events: they have no venue capacity, so the capacity
    -- check below is skipped and any number of bookings is accepted (each
    -- online booking is capped to one ticket by the application layer).
    @MaximumCapacity INT = NULL,
    @TotalAmount DECIMAL(18, 2),
    @AttendeeNames [Event].[EventBookingAttendeeNames] READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @TicketCount INT = (SELECT COUNT(*) FROM @AttendeeNames);

    IF @TicketCount < 1
    BEGIN
        THROW 51094, 'At least one attendee name is required.', 1;
    END

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1 FROM [Event].[Events] WHERE [EventId] = @EventId
    )
    BEGIN
        THROW 51090, 'Event was not found.', 1;
    END

    -- Race-safe capacity check. UPDLOCK + HOLDLOCK on the SUM forces
    -- concurrent CreateEventBooking calls for the same event to serialise,
    -- so two buyers can't both claim the last seat.
    DECLARE @ReservedTickets INT;
    SELECT @ReservedTickets = ISNULL(SUM([TicketCount]), 0)
    FROM [Event].[EventBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [EventId] = @EventId
      AND [Status] = N'Confirmed';

    IF @MaximumCapacity IS NOT NULL AND @ReservedTickets + @TicketCount > @MaximumCapacity
    BEGIN
        THROW 51091, 'Event is sold out or does not have enough remaining capacity.', 1;
    END

    DECLARE @InsertedBookingId TABLE ([BookingId] UNIQUEIDENTIFIER);

    INSERT INTO [Event].[EventBookings]
    (
        [EventId],
        [BookerName],
        [BookerEmail],
        [BookerMobile],
        [TicketCount],
        [PaymentMethod],
        [TotalAmount]
    )
    OUTPUT inserted.[BookingId] INTO @InsertedBookingId
    VALUES
    (
        @EventId,
        @BookerName,
        @BookerEmail,
        @BookerMobile,
        @TicketCount,
        @PaymentMethod,
        @TotalAmount
    );

    DECLARE @BookingId UNIQUEIDENTIFIER = (SELECT TOP (1) [BookingId] FROM @InsertedBookingId);

    INSERT INTO [Event].[EventBookingTickets]
        ([BookingId], [EventId], [TicketNumber], [AttendeeName])
    SELECT @BookingId, @EventId, a.[TicketNumber], a.[AttendeeName]
    FROM @AttendeeNames AS a;

    -- Tell the ORGANISER somebody bought tickets. No self-notification risk: an
    -- organiser is blocked from booking their own event (403 SelfBookingNotAllowed),
    -- so the buyer is always someone else. The buyer gets nothing here â€” buying is
    -- their own action, and they saw the confirmation on screen.
    EXEC [Notification].[EnqueueEventNotification]
        @EventId = @EventId,
        @EventBookingId = @BookingId,
        @NotificationType = N'EVENT_TICKET_SOLD',
        @BookerName = @BookerName,
        @TicketCount = @TicketCount;

    -- Result set 1: the booking row.
    SELECT [BookingId],
           [EventId],
           [BookerName],
           [BookerEmail],
           [BookerMobile],
           [TicketCount],
           [PaymentMethod],
           [PaymentStatus],
           [PaymentReference],
           [TotalAmount],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc]
    FROM [Event].[EventBookings]
    WHERE [BookingId] = @BookingId;

    -- Result set 2: the ticket rows (ordered by TicketNumber).
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


-- 3.32a Event.SaveEventPayoutMethods ------------------------------------------
-- Replaces an event's payout-method set (Cash and/or Digital). Payout methods
-- only apply to PAID events: a free event throws 51099 (API → 400
-- FreeEventNoPayout); a missing event throws 51098 (API → 404 EventNotFound).
CREATE OR ALTER PROCEDURE [Event].[SaveEventPayoutMethods]
    @EventId UNIQUEIDENTIFIER,
    @AcceptsCash BIT,
    @AcceptsDigital BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @IsPaid BIT;
    SELECT @IsPaid = [IsPaid]
    FROM [Event].[Events]
    WHERE [EventId] = @EventId;

    IF @IsPaid IS NULL
        THROW 51098, 'Event was not found.', 1;

    IF @IsPaid = 0
        THROW 51099, 'Payout methods only apply to paid events; this event is free.', 1;

    DELETE FROM [Event].[EventPayoutMethods]
    WHERE [EventId] = @EventId;

    IF @AcceptsCash = 1
        INSERT INTO [Event].[EventPayoutMethods] ([EventId], [PayoutMethod])
        VALUES (@EventId, N'Cash');

    IF @AcceptsDigital = 1
        INSERT INTO [Event].[EventPayoutMethods] ([EventId], [PayoutMethod])
        VALUES (@EventId, N'Digital');

    SELECT [PayoutMethod]
    FROM [Event].[EventPayoutMethods]
    WHERE [EventId] = @EventId
    ORDER BY [PayoutMethod];

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Event].[SaveEventPayoutMethods].';
GO

-- 3.32a Event.CountEventsByOrganizer ------------------------------------------
-- Total events created by a single organiser (provider OR pet parent). Surfaced
-- on the event-detail read as the organiser's event count.
CREATE OR ALTER PROCEDURE [Event].[CountEventsByOrganizer]
    @ProviderId UNIQUEIDENTIFIER = NULL,
    @PetParentId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT COUNT(*)
    FROM [Event].[Events]
    WHERE (@ProviderId IS NOT NULL AND [ProviderId] = @ProviderId)
       OR (@PetParentId IS NOT NULL AND [PetParentId] = @PetParentId);
END;
GO
PRINT 'Created/updated [Event].[CountEventsByOrganizer].';
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


-- 3.35 Event.ListEventBookingsByBookerEmail ------------------------------------
-- Powers the pet-parent host's "my event bookings" screen. Joins each
-- booking to its event so the mobile card can render without a follow-up
-- fetch. Booker identity is free text (see CLAUDE.md) so we match by
-- BookerEmail, which the endpoint pulls from the caller's JWT.
CREATE OR ALTER PROCEDURE [Event].[ListEventBookingsByBookerEmail]
    @BookerEmail NVARCHAR(320)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT b.[BookingId],
           b.[EventId],
           e.[Title]            AS [EventTitle],
           e.[EventCategory],
           e.[StartDate]        AS [EventStartDate],
           e.[StartTime]        AS [EventStartTime],
           e.[BannerImageUrl]   AS [EventBannerImageUrl],
           b.[BookerName],
           b.[BookerEmail],
           b.[BookerMobile],
           b.[TicketCount],
           b.[PaymentMethod],
           b.[PaymentStatus],
           b.[PaymentReference],
           b.[TotalAmount],
           b.[Status],
           b.[CreatedAtUtc],
           b.[UpdatedAtUtc],
           b.[CancelledAtUtc],
           -- EventType lets the app decide whether to hydrate the Cosmos venue
           -- location (physical events only). Appended last so existing ordinals
           -- don't shift.
           e.[EventType]        AS [EventType]
    FROM [Event].[EventBookings] AS b
    INNER JOIN [Event].[Events] AS e
        ON e.[EventId] = b.[EventId]
    WHERE b.[BookerEmail] = @BookerEmail
    ORDER BY b.[CreatedAtUtc] DESC;
END;
GO
PRINT 'Created/updated [Event].[ListEventBookingsByBookerEmail].';
GO


-- 3.35a Event.ListBookedEventIdsByBookerEmail ----------------------------------
-- Distinct events the caller currently holds tickets for (Status =
-- N'Confirmed'; cancelled bookings free the seat and are excluded). Powers
-- the IsBookable flag on event list / detail reads — an event the caller has
-- already booked is not bookable again. Booker identity is free text, so we
-- match by BookerEmail, which the endpoint pulls from the caller's JWT.
CREATE OR ALTER PROCEDURE [Event].[ListBookedEventIdsByBookerEmail]
    @BookerEmail NVARCHAR(320)
AS
BEGIN
    SET NOCOUNT ON;

    SELECT DISTINCT b.[EventId]
    FROM [Event].[EventBookings] AS b
    WHERE b.[BookerEmail] = @BookerEmail
      AND b.[Status] = N'Confirmed';
END;
GO
PRINT 'Created/updated [Event].[ListBookedEventIdsByBookerEmail].';
GO


-- 3.36 Event.CancelEventBooking ------------------------------------------------
-- Soft-cancels a booking on behalf of the booker (matched by @BookerEmail).
-- Flipping Status to N'Cancelled' releases the seat capacity automatically
-- (CreateEventBooking only SUMs Confirmed rows). THROW 51218 not found for
-- booker; 51219 already cancelled.
CREATE OR ALTER PROCEDURE [Event].[CancelEventBooking]
    @BookingId UNIQUEIDENTIFIER,
    @BookerEmail NVARCHAR(320)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @CurrentStatus NVARCHAR(32);
    DECLARE @EventId UNIQUEIDENTIFIER;
    DECLARE @BookerName NVARCHAR(200);
    DECLARE @TicketCount INT;

    SELECT @CurrentStatus = [Status],
           @EventId = [EventId],
           @BookerName = [BookerName],
           @TicketCount = [TicketCount]
    FROM [Event].[EventBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId
      AND [BookerEmail] = @BookerEmail;

    IF @CurrentStatus IS NULL
        THROW 51218, 'Event booking was not found for this booker.', 1;

    IF @CurrentStatus = N'Cancelled'
        THROW 51219, 'Event booking is already cancelled.', 1;

    UPDATE [Event].[EventBookings]
    SET [Status] = N'Cancelled',
        [CancelledAtUtc] = SYSUTCDATETIME(),
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [BookingId] = @BookingId;

    -- Tell the ORGANISER their seats are free again â€” they need to know the
    -- attendee list changed, and they didn't perform this action. The booker gets
    -- nothing: cancelling is their own tap.
    EXEC [Notification].[EnqueueEventNotification]
        @EventId = @EventId,
        @EventBookingId = @BookingId,
        @NotificationType = N'EVENT_TICKET_CANCELLED',
        @BookerName = @BookerName,
        @TicketCount = @TicketCount;

    -- Result set 1: the cancelled booking row (same shape as GetEventBooking RS1).
    SELECT [BookingId],
           [EventId],
           [BookerName],
           [BookerEmail],
           [BookerMobile],
           [TicketCount],
           [PaymentMethod],
           [PaymentStatus],
           [PaymentReference],
           [TotalAmount],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc]
    FROM [Event].[EventBookings]
    WHERE [BookingId] = @BookingId;

    -- Result set 2: tickets (zero or more, ordered by TicketNumber) so the
    -- response carries the full shape the client got on create / GET.
    SELECT [TicketId], [BookingId], [EventId], [TicketNumber], [AttendeeName], [CreatedAtUtc]
    FROM [Event].[EventBookingTickets]
    WHERE [BookingId] = @BookingId
    ORDER BY [TicketNumber];
END;
GO
PRINT 'Created/updated [Event].[CancelEventBooking].';
GO


--------------------------------------------------------------------------------
-- 3.x Device-token register / refresh (FCM)
--------------------------------------------------------------------------------
-- An FCM token is NOT stable: it rotates on reinstall, cleared app data, some
-- restores, and on Firebase's own schedule. The sign-in flow only runs at sign
-- in, so without these the rotated token is never reported and the device
-- silently stops receiving notifications.
--
-- The owner is resolved from @FirebaseUserId (the JWT), never the request body.
--
-- STALE-TOKEN RETIREMENT: with @DeviceId supplied, every OTHER active token for
-- that physical device is deactivated — including another account's — so a
-- reinstall retires the old token at once rather than waiting for FCM to report
-- UNREGISTERED, and a device that changes hands stops receiving the previous
-- account's notifications. No @DeviceId ⇒ nothing retired (we cannot tell
-- devices apart, and guessing would kill the user's other phones).

CREATE OR ALTER PROCEDURE [Provider].[SaveProviderDeviceToken]
    @FirebaseUserId NVARCHAR(128),
    @FcmToken NVARCHAR(2048),
    @DeviceId NVARCHAR(200) = NULL,
    @DevicePlatform NVARCHAR(32) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ProviderAuthIdentityId UNIQUEIDENTIFIER;
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @RetiredCount INT = 0;

    BEGIN TRANSACTION;

    SELECT @ProviderAuthIdentityId = [ProviderAuthIdentityId],
           @ProviderId = [ProviderId]
    FROM [Provider].[ProviderAuthIdentities] WITH (UPDLOCK, HOLDLOCK)
    WHERE [FirebaseUserId] = @FirebaseUserId;

    IF @ProviderAuthIdentityId IS NULL
    BEGIN
        ROLLBACK TRANSACTION;
        THROW 51004, 'Provider auth identity was not found for the supplied Firebase user.', 1;
    END

    IF @DeviceId IS NOT NULL AND LEN(LTRIM(RTRIM(@DeviceId))) > 0
    BEGIN
        UPDATE [Provider].[ProviderDeviceTokens]
        SET [IsActive] = 0, [UpdatedAtUtc] = @Now
        WHERE [DeviceId] = @DeviceId AND [FcmToken] <> @FcmToken AND [IsActive] = 1;
        SET @RetiredCount = @@ROWCOUNT;
    END

    IF EXISTS (SELECT 1 FROM [Provider].[ProviderDeviceTokens] WITH (UPDLOCK, HOLDLOCK)
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
            ([ProviderAuthIdentityId], [ProviderId], [FcmToken], [DeviceId], [DevicePlatform])
        VALUES
            (@ProviderAuthIdentityId, @ProviderId, @FcmToken, @DeviceId, @DevicePlatform);
    END

    COMMIT TRANSACTION;

    SELECT [ProviderDeviceTokenId], [ProviderId], [DeviceId], [DevicePlatform], [IsActive],
           @RetiredCount AS [RetiredTokenCount], [LastSeenAtUtc], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Provider].[ProviderDeviceTokens]
    WHERE [FcmToken] = @FcmToken;
END;
GO
PRINT 'Created/updated [Provider].[SaveProviderDeviceToken].';
GO

-- Sign-out counterpart. Scoped to the caller's own auth identity; "unknown" and
-- "not yours" are reported identically (51005) so this cannot probe whether a
-- token is registered. Kept as IsActive = 0 rather than deleted, since FcmToken
-- is UNIQUE and the same device signing back in is reactivated in place.
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

    IF NOT EXISTS (SELECT 1 FROM [Provider].[ProviderDeviceTokens]
                   WHERE [FcmToken] = @FcmToken AND [ProviderAuthIdentityId] = @ProviderAuthIdentityId)
    BEGIN
        THROW 51005, 'Device token was not found for this account.', 1;
    END

    UPDATE [Provider].[ProviderDeviceTokens]
    SET [IsActive] = 0, [UpdatedAtUtc] = @Now
    WHERE [FcmToken] = @FcmToken AND [ProviderAuthIdentityId] = @ProviderAuthIdentityId;

    SELECT [ProviderDeviceTokenId], [ProviderId], [DeviceId], [DevicePlatform], [IsActive],
           0 AS [RetiredTokenCount], [LastSeenAtUtc], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Provider].[ProviderDeviceTokens]
    WHERE [FcmToken] = @FcmToken AND [ProviderAuthIdentityId] = @ProviderAuthIdentityId;
END;
GO
PRINT 'Created/updated [Provider].[DeactivateProviderDeviceToken].';
GO

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
        SET [IsActive] = 0, [UpdatedAtUtc] = @Now
        WHERE [DeviceId] = @DeviceId AND [FcmToken] <> @FcmToken AND [IsActive] = 1;
        SET @RetiredCount = @@ROWCOUNT;
    END

    IF EXISTS (SELECT 1 FROM [Parent].[ParentDeviceTokens] WITH (UPDLOCK, HOLDLOCK)
               WHERE [FcmToken] = @FcmToken)
    BEGIN
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
            ([ParentAuthIdentityId], [PetParentId], [FcmToken], [DeviceId], [DevicePlatform])
        VALUES
            (@ParentAuthIdentityId, @PetParentId, @FcmToken, @DeviceId, @DevicePlatform);
    END

    COMMIT TRANSACTION;

    SELECT [ParentDeviceTokenId], [PetParentId], [DeviceId], [DevicePlatform], [IsActive],
           @RetiredCount AS [RetiredTokenCount], [LastSeenAtUtc], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Parent].[ParentDeviceTokens]
    WHERE [FcmToken] = @FcmToken;
END;
GO
PRINT 'Created/updated [Parent].[SaveParentDeviceToken].';
GO

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

    IF NOT EXISTS (SELECT 1 FROM [Parent].[ParentDeviceTokens]
                   WHERE [FcmToken] = @FcmToken AND [ParentAuthIdentityId] = @ParentAuthIdentityId)
    BEGIN
        THROW 51226, 'Device token was not found for this account.', 1;
    END

    UPDATE [Parent].[ParentDeviceTokens]
    SET [IsActive] = 0, [UpdatedAtUtc] = @Now
    WHERE [FcmToken] = @FcmToken AND [ParentAuthIdentityId] = @ParentAuthIdentityId;

    SELECT [ParentDeviceTokenId], [PetParentId], [DeviceId], [DevicePlatform], [IsActive],
           0 AS [RetiredTokenCount], [LastSeenAtUtc], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Parent].[ParentDeviceTokens]
    WHERE [FcmToken] = @FcmToken AND [ParentAuthIdentityId] = @ParentAuthIdentityId;
END;
GO
PRINT 'Created/updated [Parent].[DeactivateParentDeviceToken].';
GO


--------------------------------------------------------------------------------
-- 3.x Notification outbox sprocs
--------------------------------------------------------------------------------

-- Enqueues one push notification. Callable from C# AND from other sprocs (the
-- booking sweeps call it inside their own transaction, so the notification
-- commits atomically with the status flip it describes). Copy is NOT passed in —
-- the dispatcher renders it from @NotificationType + @DataJson. @DedupeKey makes
-- the call idempotent. Never THROWs: a notification must never roll back a
-- booking transaction.
CREATE OR ALTER PROCEDURE [Notification].[EnqueueNotification]
    @Audience NVARCHAR(16),
    @RecipientId UNIQUEIDENTIFIER,
    @NotificationType NVARCHAR(64),
    @EntityType NVARCHAR(32) = NULL,
    @EntityId UNIQUEIDENTIFIER = NULL,
    @DataJson NVARCHAR(MAX) = NULL,
    @ImageUrl NVARCHAR(1000) = NULL,
    @DedupeKey NVARCHAR(200) = NULL,
    -- Set by sproc-to-sproc callers (the booking transitions and the sweeps).
    -- A result set from a nested EXEC propagates all the way to the client, so
    -- without this an enqueue inside e.g. Booking.UpdateBookingStatus would append
    -- a phantom result set after the booking row and break the C# reader.
    -- Defaults to 0 so SqlNotificationPublisher, which reads the id back, is
    -- unaffected.
    @SuppressResultSet BIT = 0
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @NotificationId UNIQUEIDENTIFIER;
    DECLARE @WasDuplicate BIT = 0;

    IF @DedupeKey IS NOT NULL
    BEGIN
        -- UPDLOCK + HOLDLOCK so two concurrent enqueues of the same key
        -- serialise rather than both passing the check; the filtered UNIQUE
        -- index UX_NotificationOutbox_DedupeKey is the backstop below.
        SELECT @NotificationId = [NotificationId]
        FROM [Notification].[NotificationOutbox] WITH (UPDLOCK, HOLDLOCK)
        WHERE [DedupeKey] = @DedupeKey;

        IF @NotificationId IS NOT NULL
        BEGIN
            SET @WasDuplicate = 1;
        END
    END

    IF @NotificationId IS NULL
    BEGIN
        DECLARE @Inserted TABLE ([NotificationId] UNIQUEIDENTIFIER);

        BEGIN TRY
            INSERT INTO [Notification].[NotificationOutbox]
            (
                [Audience],
                [RecipientId],
                [NotificationType],
                [EntityType],
                [EntityId],
                [DataJson],
                [ImageUrl],
                [DedupeKey]
            )
            OUTPUT inserted.[NotificationId] INTO @Inserted
            VALUES
            (
                @Audience,
                @RecipientId,
                @NotificationType,
                @EntityType,
                @EntityId,
                @DataJson,
                @ImageUrl,
                @DedupeKey
            );

            SELECT @NotificationId = [NotificationId] FROM @Inserted;
        END TRY
        BEGIN CATCH
            -- 2601/2627 = the dedupe race the lock above almost always prevents.
            -- Anything else is a real error and must surface.
            IF ERROR_NUMBER() NOT IN (2601, 2627) OR @DedupeKey IS NULL
            BEGIN
                THROW;
            END

            SELECT @NotificationId = [NotificationId]
            FROM [Notification].[NotificationOutbox]
            WHERE [DedupeKey] = @DedupeKey;

            SET @WasDuplicate = 1;
        END CATCH
    END

    IF @SuppressResultSet = 0
    BEGIN
        SELECT @NotificationId AS [NotificationId], @WasDuplicate AS [WasDuplicate];
    END
END;
GO
PRINT 'Created/updated [Notification].[EnqueueNotification].';
GO

-- Builds the standard booking notification payload and enqueues it. THE single
-- place the booking `data` object is assembled - every booking notification in
-- the product (transition sprocs and timer sweeps alike) goes through here, so
-- the mobile contract is defined once instead of at ~30 call sites. Emits the
-- canonical id block (category/bookingId/parentId/providerId/petId/isNightStay/
-- payoutId) plus the template parameters. `serviceName` is deliberately NOT built
-- here: the grooming display names live in the C# catalog, so this emits the raw
-- ServiceType + ServiceItemCode and NotificationRenderer names it at render time.
-- Dates/times are handled the same way: this emits raw UTC INSTANTS
-- (serviceStartUtc, checkOutUtc, newServiceStartUtc, newCheckOutUtc,
-- closingAtUtc) and the renderer converts each to the recipient's timezone
-- (Switzerland for everybody today) and formats it. Never THROWs.
CREATE OR ALTER PROCEDURE [Notification].[EnqueueBookingNotification]
    @BookingId UNIQUEIDENTIFIER,
    @IsNightStay BIT,
    @Audience NVARCHAR(16),
    @NotificationType NVARCHAR(64),
    -- Extra template parameters, supplied only by the callers whose copy needs
    -- them. Explicit parameters rather than a JSON blob to merge: T-SQL has no
    -- clean object-merge, and naming them keeps the contract greppable.
    @Amount NVARCHAR(64) = NULL,
    -- The staged proposal on a modification, as UTC instants rather than display
    -- text: the renderer localises them (@NewCheckOutUtc is night-stay only).
    @NewServiceStartUtc DATETIME2(0) = NULL,
    @NewCheckOutUtc DATETIME2(0) = NULL,
    @AbsentParty NVARCHAR(32) = NULL,
    -- The modification review window, as the pair of instants the renderer needs
    -- to express it as a LENGTH ("24 hours", "3 hours 20 minutes"). Supplied by the
    -- two request-modification sprocs, which are the only callers that know which
    -- of the two deadlines bites — see the note where they compute it.
    @ReviewByUtc DATETIME2(0) = NULL,
    @RequestedAtUtc DATETIME2(0) = NULL,
    @Location NVARCHAR(500) = NULL,
    -- When the provider closes on the service date. An instant, not a bare TIME:
    -- converting a clock time to the recipient's zone needs the date it falls on.
    @ClosingAtUtc DATETIME2(0) = NULL,
    -- Idempotency. Callers pass a suffix rather than a whole key so the
    -- type + booking prefix stays consistent across every producer.
    @DedupeSuffix NVARCHAR(64) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @PetId UNIQUEIDENTIFIER;
    DECLARE @ServiceId UNIQUEIDENTIFIER;
    DECLARE @PayoutId NVARCHAR(64);
    DECLARE @ServiceItemCode NVARCHAR(64);
    DECLARE @ServiceDate DATE;
    DECLARE @StartTime TIME(0);
    DECLARE @CheckOutDate DATE;
    -- Night-stay only: the hand-back time on the check-out day, so the stay's end
    -- can be expressed as an instant like its start.
    DECLARE @PickUpTime TIME(0);
    -- Custom walk-ins carry the pet's name on the booking row rather than a PetId.
    DECLARE @RowPetName NVARCHAR(100);
    DECLARE @SnapshotAddressLine NVARCHAR(500);
    DECLARE @UnitPrice DECIMAL(10, 2);
    DECLARE @Quantity DECIMAL(10, 4);

    IF @IsNightStay = 1
    BEGIN
        SELECT @ProviderId = [ProviderId],
               @PetParentId = [PetParentId],
               @PetId = [PetId],
               @ServiceId = [ServiceId],
               @PayoutId = [PayoutId],
               @ServiceDate = [CheckInDate],
               @CheckOutDate = [CheckOutDate],
               -- A stay's "start time" is the drop-off on the check-in day: the
               -- same instant BR-01 / BR-53 / the modification cutoff all measure
               -- against, so the notification quotes what the rules use.
               @StartTime = [DropOffTime],
               @PickUpTime = [PickUpTime],
               @SnapshotAddressLine = [SnapshotAddressLine],
               @UnitPrice = [PricePerNight],
               -- The checkout day is not a stayed night.
               @Quantity = DATEDIFF(DAY, [CheckInDate], [CheckOutDate])
        FROM [Booking].[NightStayBookings]
        WHERE [NightStayBookingId] = @BookingId;
    END
    ELSE
    BEGIN
        SELECT @ProviderId = [ProviderId],
               @PetParentId = [PetParentId],
               @PetId = [PetId],
               @ServiceId = [ServiceId],
               @PayoutId = [PayoutId],
               @ServiceItemCode = [ServiceItemCode],
               @ServiceDate = [BookingDate],
               @StartTime = [StartTime],
               @RowPetName = [PetName],
               @SnapshotAddressLine = [SnapshotAddressLine],
               @UnitPrice = [PricePerHour],
               @Quantity = DATEDIFF(MINUTE, [StartTime], [EndTime]) / 60.0
        FROM [Booking].[Bookings]
        WHERE [BookingId] = @BookingId;
    END

    IF @ProviderId IS NULL
    BEGIN
        -- Unknown booking. Nothing to say, and throwing would take the caller's
        -- transaction down with it.
        RETURN;
    END

    -- A notification with no recipient is not an error either: a Custom walk-in
    -- has no PetParentId, so parent-audience notifications simply don't apply.
    DECLARE @RecipientId UNIQUEIDENTIFIER =
        CASE WHEN @Audience = N'Provider' THEN @ProviderId ELSE @PetParentId END;

    IF @RecipientId IS NULL
    BEGIN
        RETURN;
    END

    DECLARE @ProviderName NVARCHAR(200);
    DECLARE @ParentName NVARCHAR(200);
    DECLARE @PetName NVARCHAR(100);
    DECLARE @ServiceType NVARCHAR(32);

    SELECT @ProviderName = NULLIF(LTRIM(RTRIM(
        ISNULL([FirstName], N'') + N' ' + ISNULL([LastName], N''))), N'')
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;

    IF @PetParentId IS NOT NULL
    BEGIN
        SELECT @ParentName = NULLIF(LTRIM(RTRIM(
            ISNULL([FirstName], N'') + N' ' + ISNULL([LastName], N''))), N'')
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId;
    END

    IF @PetId IS NOT NULL
    BEGIN
        SELECT @PetName = [PetName] FROM [Parent].[Pets] WHERE [PetId] = @PetId;
    END

    -- Falls back to the walk-in's free-text pet name when there is no linked pet.
    SET @PetName = COALESCE(@PetName, @RowPetName);

    SELECT @ServiceType = [ServiceType]
    FROM [Provider].[ProviderServices]
    WHERE [ServiceId] = @ServiceId;

    DECLARE @EntityType NVARCHAR(32) =
        CASE WHEN @IsNightStay = 1 THEN N'NightStayBooking' ELSE N'Booking' END;

    -- Derive the amount from the price frozen onto the booking at creation, unless
    -- the caller supplied one. Same rule the booking-detail read uses: only the
    -- unit RATE is locked, so the total is always rate x quantity and an accepted
    -- modification that changes the duration still re-totals correctly.
    --
    -- Legacy rows created before price-locking have no rate. They resolve to NULL,
    -- the key drops out of the JSON, and the renderer's "the agreed amount"
    -- fallback carries the sentence — rather than quoting a wrong figure, which on
    -- a "please pay" notification would be worse than quoting none.
    IF @Amount IS NULL AND @UnitPrice IS NOT NULL AND @Quantity > 0
    BEGIN
        SET @Amount = N'CHF ' + CONVERT(NVARCHAR(32),
            CAST(ROUND(@UnitPrice * @Quantity, 2) AS DECIMAL(12, 2)));
    END

    -- The service's start as a single UTC instant: BookingDate + StartTime, or
    -- CheckInDate + DropOffTime for a stay. Same arithmetic BR-01 / BR-53 and the
    -- modification cutoff use, so what the notification says and what the rules
    -- enforce can't drift apart. DATEDIFF-of-seconds rather than a cast-and-add
    -- because TIME + DATETIME2 is not a legal addition in T-SQL.
    DECLARE @ServiceStartUtc DATETIME2(0) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @StartTime),
                CAST(@ServiceDate AS DATETIME2(0)));

    DECLARE @CheckOutUtc DATETIME2(0) =
        CASE WHEN @IsNightStay = 1 AND @PickUpTime IS NOT NULL
             THEN DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @PickUpTime),
                          CAST(@CheckOutDate AS DATETIME2(0))) END;

    DECLARE @DataJson NVARCHAR(MAX) =
    (
        SELECT
            -- --- the canonical id block ---
            N'BOOKING'                                   AS [category],
            CAST(@BookingId AS NVARCHAR(36))             AS [bookingId],
            CAST(@PetParentId AS NVARCHAR(36))           AS [parentId],
            CAST(@ProviderId AS NVARCHAR(36))            AS [providerId],
            CAST(@PetId AS NVARCHAR(36))                 AS [petId],
            CASE WHEN @IsNightStay = 1 THEN N'true' ELSE N'false' END AS [isNightStay],
            @PayoutId                                    AS [payoutId],
            -- Kept alongside isNightStay for the clients built before it existed.
            CASE WHEN @IsNightStay = 1 THEN N'NightStay' ELSE N'SingleDay' END AS [bookingType],
            -- --- template parameters ---
            @ProviderName                                AS [providerName],
            @ParentName                                  AS [parentName],
            @PetName                                     AS [petName],
            @ServiceType                                 AS [serviceType],
            @ServiceItemCode                             AS [serviceItemCode],
            -- --- times, as UTC instants; the renderer localises + formats them ---
            -- Style 126 on a DATETIME2(0) gives "2026-08-05T14:00:00" exactly.
            CONVERT(NVARCHAR(19), @ServiceStartUtc, 126)  AS [serviceStartUtc],
            -- Night-stay only; NULL columns drop out of the JSON.
            CONVERT(NVARCHAR(19), @CheckOutUtc, 126)      AS [checkOutUtc],
            -- --- caller-supplied extras ---
            @Amount                                      AS [amount],
            CONVERT(NVARCHAR(19), @NewServiceStartUtc, 126) AS [newServiceStartUtc],
            CONVERT(NVARCHAR(19), @NewCheckOutUtc, 126)   AS [newCheckOutUtc],
            @AbsentParty                                 AS [absentParty],
            CONVERT(NVARCHAR(19), @ReviewByUtc, 126)      AS [reviewByUtc],
            CONVERT(NVARCHAR(19), @RequestedAtUtc, 126)   AS [requestedAtUtc],
            COALESCE(@Location, @SnapshotAddressLine)     AS [location],
            CONVERT(NVARCHAR(19), @ClosingAtUtc, 126)     AS [closingAtUtc]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    );

    DECLARE @DedupeKey NVARCHAR(200) =
        @NotificationType + N':' + CAST(@BookingId AS NVARCHAR(36))
        + N':' + @Audience
        + CASE WHEN @DedupeSuffix IS NULL THEN N'' ELSE N':' + @DedupeSuffix END;

    EXEC [Notification].[EnqueueNotification]
        @Audience = @Audience,
        @RecipientId = @RecipientId,
        @NotificationType = @NotificationType,
        @EntityType = @EntityType,
        @EntityId = @BookingId,
        @DataJson = @DataJson,
        @DedupeKey = @DedupeKey,
        -- Callers are transition sprocs returning their own booking row; a nested
        -- result set here would corrupt that.
        @SuppressResultSet = 1;
END
GO
PRINT 'Created/updated [Notification].[EnqueueBookingNotification].';
GO

-- Builds the standard EVENT notification payload and enqueues it. The event
-- twin of EnqueueBookingNotification. Recipient is ALWAYS the organiser, so
-- there is no @Audience parameter: Event.Events carries exactly one of
-- ProviderId / PetParentId and that choice IS the audience. The BUYER is not
-- addressable - EventBookings identifies its booker only by free-text
-- BookerEmail with no FK, so there is no id to push to. Never THROWs.
CREATE OR ALTER PROCEDURE [Notification].[EnqueueEventNotification]
    @EventId UNIQUEIDENTIFIER,
    @EventBookingId UNIQUEIDENTIFIER = NULL,
    @NotificationType NVARCHAR(64),
    -- The person who bought or cancelled the tickets. Free text off the booking
    -- row, since that is all the schema records about them.
    @BookerName NVARCHAR(200) = NULL,
    @TicketCount INT = NULL,
    @DedupeSuffix NVARCHAR(64) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @EventTitle NVARCHAR(200);

    SELECT @ProviderId = [ProviderId],
           @PetParentId = [PetParentId],
           @EventTitle = [Title]
    FROM [Event].[Events]
    WHERE [EventId] = @EventId;

    IF @ProviderId IS NULL AND @PetParentId IS NULL
    BEGIN
        -- Unknown event, or an organiser-less row that should not exist.
        -- Nothing to say, and throwing would take the caller's transaction down.
        RETURN;
    END

    DECLARE @Audience NVARCHAR(16) =
        CASE WHEN @ProviderId IS NOT NULL THEN N'Provider' ELSE N'PetParent' END;
    DECLARE @RecipientId UNIQUEIDENTIFIER = COALESCE(@ProviderId, @PetParentId);

    DECLARE @DataJson NVARCHAR(MAX) =
    (
        SELECT
            -- --- the canonical id block ---
            N'EVENT'                                  AS [category],
            CAST(@EventId AS NVARCHAR(36))            AS [eventId],
            CAST(@PetParentId AS NVARCHAR(36))        AS [parentId],
            CAST(@ProviderId AS NVARCHAR(36))         AS [providerId],
            -- --- template parameters ---
            CAST(@EventBookingId AS NVARCHAR(36))     AS [eventBookingId],
            @EventTitle                               AS [eventTitle],
            -- The copy says "{parentName} booked N tickets"; for an event the
            -- booker may be a provider or a parent, so this is simply whoever
            -- bought them, by name.
            @BookerName                               AS [parentName],
            CAST(@TicketCount AS NVARCHAR(16))        AS [ticketCount]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    );

    DECLARE @DedupeKey NVARCHAR(200) =
        @NotificationType + N':' + CAST(COALESCE(@EventBookingId, @EventId) AS NVARCHAR(36))
        + CASE WHEN @DedupeSuffix IS NULL THEN N'' ELSE N':' + @DedupeSuffix END;

    EXEC [Notification].[EnqueueNotification]
        @Audience = @Audience,
        @RecipientId = @RecipientId,
        @NotificationType = @NotificationType,
        @EntityType = N'EventBooking',
        @EntityId = @EventBookingId,
        @DataJson = @DataJson,
        @DedupeKey = @DedupeKey,
        -- The callers return their own result sets; a nested one would corrupt them.
        @SuppressResultSet = 1;
END;
GO
PRINT 'Created/updated [Notification].[EnqueueEventNotification].';
GO

-- Claims a batch for dispatch and returns everything needed in ONE round-trip.
-- RS1: the claimed rows. RS2: the active FCM tokens for those recipients
-- (pre-joined, so no N+1). Claiming pushes [NextAttemptAtUtc] forward as a lease,
-- so a crashed dispatcher releases its rows automatically.
CREATE OR ALTER PROCEDURE [Notification].[ClaimPendingNotifications]
    @BatchSize INT = 100,
    @MaxAttempts INT = 5,
    @LeaseMinutes INT = 5
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @BatchSize IS NULL OR @BatchSize < 1 SET @BatchSize = 100;
    IF @BatchSize > 500 SET @BatchSize = 500;
    IF @MaxAttempts IS NULL OR @MaxAttempts < 1 SET @MaxAttempts = 5;
    IF @LeaseMinutes IS NULL OR @LeaseMinutes < 1 SET @LeaseMinutes = 5;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    -- Retire rows that exhausted their attempts while claimed — a dispatcher
    -- that crashed on the final attempt would otherwise sit in 'Sending' forever.
    UPDATE [Notification].[NotificationOutbox]
    SET [Status] = N'Failed',
        [UpdatedAtUtc] = @Now,
        [LastError] = COALESCE([LastError], N'Dispatch lease expired after the final attempt.')
    WHERE [Status] = N'Sending'
      AND [AttemptCount] >= @MaxAttempts
      AND [NextAttemptAtUtc] <= @Now;

    DECLARE @Claimed TABLE
    (
        [NotificationId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        [Audience] NVARCHAR(16) NOT NULL,
        [RecipientId] UNIQUEIDENTIFIER NOT NULL,
        [NotificationType] NVARCHAR(64) NOT NULL,
        [EntityType] NVARCHAR(32) NULL,
        [EntityId] UNIQUEIDENTIFIER NULL,
        [DataJson] NVARCHAR(MAX) NULL,
        [ImageUrl] NVARCHAR(1000) NULL,
        [AttemptCount] INT NOT NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
    );

    WITH [candidates] AS
    (
        SELECT TOP (@BatchSize) *
        FROM [Notification].[NotificationOutbox] WITH (READPAST, UPDLOCK, ROWLOCK)
        WHERE [Status] IN (N'Pending', N'Sending')
          AND [NextAttemptAtUtc] <= @Now
          AND [AttemptCount] < @MaxAttempts
        ORDER BY [CreatedAtUtc] ASC
    )
    UPDATE [candidates]
    SET [Status] = N'Sending',
        [AttemptCount] = [AttemptCount] + 1,
        [NextAttemptAtUtc] = DATEADD(MINUTE, @LeaseMinutes, @Now),
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[NotificationId], inserted.[Audience], inserted.[RecipientId],
           inserted.[NotificationType], inserted.[EntityType], inserted.[EntityId],
           inserted.[DataJson], inserted.[ImageUrl], inserted.[AttemptCount],
           inserted.[CreatedAtUtc]
    INTO @Claimed;

    SELECT [NotificationId], [Audience], [RecipientId], [NotificationType],
           [EntityType], [EntityId], [DataJson], [ImageUrl], [AttemptCount], [CreatedAtUtc]
    FROM @Claimed
    ORDER BY [CreatedAtUtc] ASC;

    SELECT DISTINCT
           N'Provider' AS [Audience],
           t.[ProviderId] AS [RecipientId],
           t.[FcmToken],
           t.[DevicePlatform]
    FROM [Provider].[ProviderDeviceTokens] t
    INNER JOIN @Claimed c
        ON c.[Audience] = N'Provider' AND c.[RecipientId] = t.[ProviderId]
    WHERE t.[IsActive] = 1 AND t.[ProviderId] IS NOT NULL

    UNION ALL

    SELECT DISTINCT
           N'PetParent' AS [Audience],
           t.[PetParentId] AS [RecipientId],
           t.[FcmToken],
           t.[DevicePlatform]
    FROM [Parent].[ParentDeviceTokens] t
    INNER JOIN @Claimed c
        ON c.[Audience] = N'PetParent' AND c.[RecipientId] = t.[PetParentId]
    WHERE t.[IsActive] = 1 AND t.[PetParentId] IS NOT NULL;
END;
GO
PRINT 'Created/updated [Notification].[ClaimPendingNotifications].';
GO

-- Records a dispatch batch's outcome and persists the copy the dispatcher
-- rendered. 'Sent'/'NoDevice' are terminal successes (both stamp [SentAtUtc] so
-- they appear in the inbox — a notification with no device still happened);
-- 'Failed' is retried with exponential backoff until the attempt ceiling.
CREATE OR ALTER PROCEDURE [Notification].[CompleteNotificationDelivery]
    @Results [Notification].[NotificationDeliveryResultList] READONLY,
    @MaxAttempts INT = 5
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @MaxAttempts IS NULL OR @MaxAttempts < 1 SET @MaxAttempts = 5;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    UPDATE o
    SET [Title] = COALESCE(r.[Title], o.[Title]),
        [Body] = COALESCE(r.[Body], o.[Body]),
        [Route] = COALESCE(r.[Route], o.[Route]),
        [DeliveredCount] = r.[DeliveredCount],
        [LastError] = r.[LastError],
        [UpdatedAtUtc] = @Now,
        [Status] =
            CASE
                WHEN r.[Status] IN (N'Sent', N'NoDevice') THEN r.[Status]
                WHEN o.[AttemptCount] >= @MaxAttempts THEN N'Failed'
                ELSE N'Pending'
            END,
        [SentAtUtc] =
            CASE
                WHEN r.[Status] IN (N'Sent', N'NoDevice') THEN COALESCE(o.[SentAtUtc], @Now)
                ELSE o.[SentAtUtc]
            END,
        [NextAttemptAtUtc] =
            CASE
                WHEN r.[Status] IN (N'Sent', N'NoDevice') THEN o.[NextAttemptAtUtc]
                WHEN o.[AttemptCount] >= @MaxAttempts THEN o.[NextAttemptAtUtc]
                -- 2, 4, 8, 16, 32 minutes, capped at 60. The exponent is clamped
                -- first: POWER(2, n) is integer arithmetic and would overflow if
                -- a caller raised @MaxAttempts.
                ELSE DATEADD(
                        MINUTE,
                        CASE
                            WHEN o.[AttemptCount] >= 6 THEN 60
                            WHEN POWER(2, o.[AttemptCount]) > 60 THEN 60
                            ELSE POWER(2, o.[AttemptCount])
                        END,
                        @Now)
            END
    FROM [Notification].[NotificationOutbox] o
    INNER JOIN @Results r ON r.[NotificationId] = o.[NotificationId];

    SELECT @@ROWCOUNT AS [UpdatedCount];
END;
GO
PRINT 'Created/updated [Notification].[CompleteNotificationDelivery].';
GO

-- Deactivates FCM tokens Firebase reported as permanently invalid (UNREGISTERED /
-- INVALID_ARGUMENT / SENDER_ID_MISMATCH), so dead tokens stop being pushed to.
-- Flipped to IsActive = 0 rather than deleted: [FcmToken] is UNIQUE, so a device
-- that re-registers is reactivated in place by the Save*AuthIdentity upsert.
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
    SET [IsActive] = 0, [UpdatedAtUtc] = @Now
    FROM [Provider].[ProviderDeviceTokens] t
    WHERE t.[IsActive] = 1
      AND EXISTS (SELECT 1 FROM @Tokens x
                  WHERE x.[Audience] = N'Provider' AND x.[FcmToken] = t.[FcmToken]);
    SET @ProviderCount = @@ROWCOUNT;

    UPDATE t
    SET [IsActive] = 0, [UpdatedAtUtc] = @Now
    FROM [Parent].[ParentDeviceTokens] t
    WHERE t.[IsActive] = 1
      AND EXISTS (SELECT 1 FROM @Tokens x
                  WHERE x.[Audience] = N'PetParent' AND x.[FcmToken] = t.[FcmToken]);
    SET @ParentCount = @@ROWCOUNT;

    SELECT @ProviderCount AS [DeactivatedProviderTokens],
           @ParentCount AS [DeactivatedParentTokens];
END;
GO
PRINT 'Created/updated [Notification].[DeactivateDeviceTokens].';
GO

-- The in-app notification inbox for one recipient, newest-first. Only RENDERED
-- rows are returned ([Title] IS NOT NULL) — a row is unrendered for at most one
-- dispatcher tick, and a blank bell entry would be worse than a late one.
-- RS1: the page. RS2: { TotalCount, UnreadCount } across the whole inbox.
CREATE OR ALTER PROCEDURE [Notification].[ListNotifications]
    @Audience NVARCHAR(16),
    @RecipientId UNIQUEIDENTIFIER,
    @Skip INT = 0,
    @Take INT = 50,
    @UnreadOnly BIT = 0
AS
BEGIN
    SET NOCOUNT ON;

    IF @Skip IS NULL OR @Skip < 0 SET @Skip = 0;
    IF @Take IS NULL OR @Take < 1 SET @Take = 50;
    IF @Take > 200 SET @Take = 200;

    SELECT [NotificationId], [NotificationType], [Title], [Body], [Route],
           [EntityType], [EntityId], [DataJson], [ImageUrl], [CreatedAtUtc], [ReadAtUtc]
    FROM [Notification].[NotificationOutbox]
    WHERE [Audience] = @Audience
      AND [RecipientId] = @RecipientId
      AND [Title] IS NOT NULL
      AND (@UnreadOnly = 0 OR [ReadAtUtc] IS NULL)
    ORDER BY [CreatedAtUtc] DESC
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;

    -- COALESCE because SUM over zero rows is NULL, not 0 — an inbox with nothing
    -- in it yet would otherwise report a null unread badge.
    SELECT COUNT(*) AS [TotalCount],
           COALESCE(SUM(CASE WHEN [ReadAtUtc] IS NULL THEN 1 ELSE 0 END), 0) AS [UnreadCount]
    FROM [Notification].[NotificationOutbox]
    WHERE [Audience] = @Audience
      AND [RecipientId] = @RecipientId
      AND [Title] IS NOT NULL;
END;
GO
PRINT 'Created/updated [Notification].[ListNotifications].';
GO

-- Marks notifications read. Pass @NotificationId for one, NULL for "mark all".
-- Always scoped by (@Audience, @RecipientId), so a caller can never mark someone
-- else's notification read — a foreign id simply matches nothing and reports 0.
CREATE OR ALTER PROCEDURE [Notification].[MarkNotificationsRead]
    @Audience NVARCHAR(16),
    @RecipientId UNIQUEIDENTIFIER,
    @NotificationId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    UPDATE [Notification].[NotificationOutbox]
    SET [ReadAtUtc] = @Now, [UpdatedAtUtc] = @Now
    WHERE [Audience] = @Audience
      AND [RecipientId] = @RecipientId
      AND [ReadAtUtc] IS NULL
      AND [Title] IS NOT NULL
      AND (@NotificationId IS NULL OR [NotificationId] = @NotificationId);

    DECLARE @Updated INT = @@ROWCOUNT;

    SELECT @Updated AS [MarkedCount],
           (SELECT COUNT(*)
            FROM [Notification].[NotificationOutbox]
            WHERE [Audience] = @Audience
              AND [RecipientId] = @RecipientId
              AND [Title] IS NOT NULL
              AND [ReadAtUtc] IS NULL) AS [UnreadCount];
END;
GO
PRINT 'Created/updated [Notification].[MarkNotificationsRead].';
GO


--------------------------------------------------------------------------------
-- Earnings / spend reporting. All four read [Booking].[BookingAmounts] (section
-- 2.9), so the provider's "earned" and the parent's "spent" on a given booking
-- are the same number by construction, and each summary describes exactly the
-- set its paginated sibling returns.
--------------------------------------------------------------------------------

-- Aggregate earnings for one provider over an optional service-date range
-- (@FromDate / @ToDate inclusive; both NULL = all time). Backs BOTH the provider
-- earnings overview and the period-filtered earnings endpoint — the two differ
-- only in the range they pass, so their numbers can never drift apart.
--
-- Rows and money come from [Booking].[BookingAmounts]; see that function for what
-- an amount means, which date buckets it, and how an unpaid booking is priced.
--
-- TWO GROUPS OF FIGURES, and they must not be confused:
--   * EARNED ([IsEarned] = COMPLETED / PAID) — the money. Gross / Fee / Received /
--     Awaiting and their counts all gate on it, so a cancelled or upcoming booking
--     can never inflate what the provider is owed.
--   * UNREALISED ([Cancelled*] / [NoShow*] / [Expired*]) — jobs that were on the
--     books and produced nothing. Added because a payouts screen has to account for
--     the gap between what was booked and what was earned; without them a provider
--     seeing a thin month cannot tell whether they were quiet or were stood up.
--     Reported ALONGSIDE the earned figures and never folded into them: no money
--     moved on any of these, so adding them to Gross would misstate what the
--     provider holds. The three are disjoint and together cover exactly the raw
--     statuses the API's 'Cancelled' status group expands to:
--       Cancelled -> PROVIDER_CANCELLED, PARENT_CANCELLED, PROVIDER_DECLINED
--       NoShow    -> PARENT_NO_SHOW, PROVIDER_NO_SHOW
--       Expired   -> EXPIRED, JOB_EXPIRED, OTP_MAX_ATTEMPTS_EXCEEDED
--     [Amount] on such a row is what the job WOULD have been worth, priced from its
--     creation-time price-lock. No fee is reported against them: a commission on
--     money that never changed hands is not owed, so there is nothing to net off.
--
-- Bookings still in flight (CREATED / CONFIRMED / IN_PROGRESS / mid-modification)
-- are in NEITHER group — they have not happened yet and have not failed. The
-- booking list's 'Upcoming' status group is how a client asks for those.
--
-- Platform figures EXCLUDE Custom walk-ins throughout, unrealised ones included:
-- those are arranged off-platform and carry no commission, so folding them in
-- would misstate what Pawfront processed. They are reported separately as
-- [PrivateJob*] so the provider still sees the work rather than wondering where it
-- went. Note [PrivateJob*] stays gated on [IsEarned], so a walk-in that was
-- cancelled appears in no figure here.
--
-- Net is not returned — it is Gross - Fee and the caller computes it, so there is
-- one subtraction in the codebase rather than one here and another in C#.
CREATE OR ALTER PROCEDURE [Booking].[GetProviderEarningsSummary]
    @ProviderId UNIQUEIDENTIFIER,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @FeePercentage DECIMAL(9, 4) = 0
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        -- Counts (earned platform bookings only).
        [CompletedBookings]       = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 THEN 1 END),
        [PaidBookings]            = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[IsPaid] = 1 THEN 1 END),
        [AwaitingPaymentBookings] = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[IsPaid] = 0 THEN 1 END),
        -- Completed bookings we could not price at all (legacy rows created before
        -- price-lock whose offering has since gone). Surfaced rather than hidden so
        -- a provider can see the totals are incomplete instead of silently low.
        [UnpricedBookings]        = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[Amount] IS NULL THEN 1 END),

        -- Money (earned platform bookings only). Gross = what the parent pays;
        -- Fee = the Pawfront commission on it. With cash the provider physically
        -- holds Gross and owes Fee, so both matter to them.
        [GrossAmount]   = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 THEN e.[Amount] END), 0),
        [PawfrontFee]   = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 THEN e.[Fee] END), 0),
        [ReceivedGross] = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[IsPaid] = 1 THEN e.[Amount] END), 0),
        [ReceivedFee]   = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[IsPaid] = 1 THEN e.[Fee] END), 0),
        [AwaitingGross] = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[IsPaid] = 0 THEN e.[Amount] END), 0),
        [AwaitingFee]   = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 0 AND e.[IsPaid] = 0 THEN e.[Fee] END), 0),

        -- Off-platform private jobs, reported alongside but never mixed in.
        [PrivateJobCount]  = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 1 THEN 1 END),
        [PrivateJobAmount] = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 AND e.[IsPrivate] = 1 THEN e.[Amount] END), 0),

        -- Unrealised: booked, then nothing. Never part of the money above.
        [CancelledJobCount]  = COUNT(CASE WHEN e.[IsPrivate] = 0 AND e.[Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED') THEN 1 END),
        [CancelledJobAmount] = ISNULL(SUM(CASE WHEN e.[IsPrivate] = 0 AND e.[Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED') THEN e.[Amount] END), 0),
        [NoShowJobCount]     = COUNT(CASE WHEN e.[IsPrivate] = 0 AND e.[Status] IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW') THEN 1 END),
        [NoShowJobAmount]    = ISNULL(SUM(CASE WHEN e.[IsPrivate] = 0 AND e.[Status] IN (N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW') THEN e.[Amount] END), 0),
        [ExpiredJobCount]    = COUNT(CASE WHEN e.[IsPrivate] = 0 AND e.[Status] IN (N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN 1 END),
        [ExpiredJobAmount]   = ISNULL(SUM(CASE WHEN e.[IsPrivate] = 0 AND e.[Status] IN (N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN e.[Amount] END), 0)
    FROM [Booking].[BookingAmounts](@ProviderId, NULL, @FeePercentage) e
    WHERE (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate);
END;
GO
PRINT 'Created/updated [Booking].[GetProviderEarningsSummary].';
GO

-- Booking-level breakdown behind a provider's earnings figure: which jobs
-- produced what — and, when asked, which produced nothing. Paginated (the API
-- caps @Take at 20), filterable by service-date range and by status, sortable by
-- date or by amount in either direction.
--
-- Returns TWO result sets:
--   1. [TotalCount] — matching rows before paging, so the client can page.
--   2. The page itself.
--
-- @Statuses is a comma-separated list of raw lifecycle statuses (the API expands
-- its friendly 'Completed' / 'Upcoming' / 'Cancelled' / 'NoShow' / 'Expired'
-- groups into raw statuses before calling, exactly as the pet-parent history
-- sprocs take them — so adding a lifecycle state means editing the C# vocabulary
-- rather than four stored procedures).
--
-- WHEN @Statuses IS OMITTED the rows are the [IsEarned] ones (COMPLETED / PAID),
-- under the same date filters as [Booking].[GetProviderEarningsSummary] — so the
-- default list still reconciles exactly with that summary's earned figures. This
-- default is deliberate rather than "return everything": it is what every existing
-- caller already relies on, and a list that silently started including cancelled
-- jobs would break that reconciliation without anybody asking it to.
--
-- WHEN @Statuses IS SUPPLIED it replaces the [IsEarned] gate entirely — the caller
-- has named the statuses they want, so second-guessing them would make it
-- impossible to ask for cancelled or no-showed jobs at all, which is the whole
-- point of the parameter.
--
-- Custom walk-ins ARE listed (they are real work the provider did) but carry
-- [IsPrivate] = 1 and are excluded from the summary's platform totals — the flag
-- is what lets the client present them apart rather than silently breaking the
-- reconciliation.
CREATE OR ALTER PROCEDURE [Booking].[ListProviderEarningsBookings]
    @ProviderId UNIQUEIDENTIFIER,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @FeePercentage DECIMAL(9, 4) = 0,
    @SortBy NVARCHAR(16) = N'Date',        -- 'Date' | 'Earnings'
    @SortDirection NVARCHAR(4) = N'Desc',  -- 'Asc'  | 'Desc'
    @Skip INT = 0,
    @Take INT = 20,
    -- NULL / empty = the earned rows only (back-compatible default, see header).
    @Statuses NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @FilterByStatus BIT =
        CASE WHEN @Statuses IS NULL OR @Statuses = N'' THEN 0 ELSE 1 END;

    SELECT [TotalCount] = COUNT(*)
    FROM [Booking].[BookingAmounts](@ProviderId, NULL, @FeePercentage) e
    WHERE ((@FilterByStatus = 0 AND e.[IsEarned] = 1)
           OR (@FilterByStatus = 1
               AND e.[Status] IN (SELECT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@Statuses, N','))))
      AND (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate);

    SELECT
        e.[BookingType],
        e.[BookingId],
        -- Raw job number; the 'PF-000123' label is formatted in C# exactly as the
        -- booking-detail read does, so the two surfaces show the same id.
        [JobNumber]       = COALESCE(b.[JobNumber], n.[JobNumber]),
        [PayoutId]        = COALESCE(b.[PayoutId], n.[PayoutId]),
        [PayoutStatus]    = COALESCE(b.[PayoutStatus], n.[PayoutStatus]),
        e.[Status],
        [ServiceCategory] = COALESCE(b.[ServiceCategory], n.[ServiceCategory]),
        [SubCategory]     = COALESCE(b.[SubCategory], n.[SubCategory]),
        [ServiceItemCode] = b.[ServiceItemCode],
        e.[ServiceDate],
        -- Single-day shape (NULL on a stay).
        [StartTime]       = b.[StartTime],
        [EndTime]         = b.[EndTime],
        -- Night-stay shape (NULL on a single-day booking).
        [CheckInDate]     = n.[CheckInDate],
        [CheckOutDate]    = n.[CheckOutDate],
        [Nights]          = CASE WHEN n.[NightStayBookingId] IS NOT NULL
                                 THEN DATEDIFF(DAY, n.[CheckInDate], n.[CheckOutDate]) END,
        -- App bookings name the parent from their profile; a Custom walk-in carries
        -- its own free-text customer instead.
        [CustomerName]    = COALESCE(pp.[FirstName] + N' ' + pp.[LastName], b.[CustomerName]),
        [PetName]         = COALESCE(pet.[PetName], b.[PetName]),
        e.[IsPaid],
        e.[IsPrivate],
        e.[Amount],
        e.[Fee],
        e.[PaidAtUtc],
        e.[PaymentMethod],
        -- Did this job produce money, or is it one of the unrealised ones? Emitted
        -- rather than left for the client to re-derive from [Status]: the moment a
        -- list can contain both, every row has to answer it, and deriving it in the
        -- app would be a second copy of a rule that already lives in the function.
        e.[IsEarned]
    FROM [Booking].[BookingAmounts](@ProviderId, NULL, @FeePercentage) e
    LEFT JOIN [Booking].[Bookings] b
        ON e.[BookingType] = N'SingleDay' AND b.[BookingId] = e.[BookingId]
    LEFT JOIN [Booking].[NightStayBookings] n
        ON e.[BookingType] = N'NightStay' AND n.[NightStayBookingId] = e.[BookingId]
    LEFT JOIN [Parent].[PetParents] pp
        ON pp.[PetParentId] = e.[PetParentId]
    LEFT JOIN [Parent].[Pets] pet
        ON pet.[PetId] = e.[PetId]
    WHERE ((@FilterByStatus = 0 AND e.[IsEarned] = 1)
           OR (@FilterByStatus = 1
               AND e.[Status] IN (SELECT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@Statuses, N','))))
      AND (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate)
    ORDER BY
        CASE WHEN @SortBy = N'Earnings' AND @SortDirection = N'Asc'  THEN e.[Amount] END ASC,
        CASE WHEN @SortBy = N'Earnings' AND @SortDirection = N'Desc' THEN e.[Amount] END DESC,
        CASE WHEN @SortBy <> N'Earnings' AND @SortDirection = N'Asc'  THEN e.[ServiceDate] END ASC,
        CASE WHEN @SortBy <> N'Earnings' AND @SortDirection = N'Desc' THEN e.[ServiceDate] END DESC,
        -- Deterministic tie-break. Without it, rows sharing a date (or an amount)
        -- can be ordered differently between two calls, which makes OFFSET paging
        -- repeat or skip rows.
        e.[BookingId]
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;
GO
PRINT 'Created/updated [Booking].[ListProviderEarningsBookings].';
GO

-- GetPetParentBookingSummary: how many bookings, and how much spent. @Statuses is
-- a comma-separated list of raw lifecycle statuses (the API expands the friendly
-- Completed / Upcoming / Cancelled groups into it). The three buckets are mutually
-- exclusive and sum to [TotalBookings].
CREATE OR ALTER PROCEDURE [Booking].[GetPetParentBookingSummary]
    @PetParentId UNIQUEIDENTIFIER,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @PetId UNIQUEIDENTIFIER = NULL,
    @Statuses NVARCHAR(MAX) = NULL,
    @FeePercentage DECIMAL(9, 4) = 0
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        [TotalBookings]           = COUNT(*),
        [SingleDayBookings]       = COUNT(CASE WHEN e.[BookingType] = N'SingleDay' THEN 1 END),
        [NightStayBookings]       = COUNT(CASE WHEN e.[BookingType] = N'NightStay' THEN 1 END),
        [CompletedBookings]       = COUNT(CASE WHEN e.[IsEarned] = 1 THEN 1 END),
        [CancelledBookings]       = COUNT(CASE WHEN e.[IsEarned] = 0 AND e.[Status] IN (
                                        N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                                        N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED',
                                        N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN 1 END),
        [UpcomingBookings]        = COUNT(CASE WHEN e.[IsEarned] = 0 AND e.[Status] NOT IN (
                                        N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                                        N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED',
                                        N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN 1 END),
        [PaidBookings]            = COUNT(CASE WHEN e.[IsPaid] = 1 THEN 1 END),
        [AwaitingPaymentBookings] = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[IsPaid] = 0 THEN 1 END),
        [UnpricedBookings]        = COUNT(CASE WHEN e.[IsEarned] = 1 AND e.[Amount] IS NULL THEN 1 END),
        [AmountSpent]    = ISNULL(SUM(CASE WHEN e.[IsEarned] = 1 THEN e.[Amount] END), 0),
        [UpcomingAmount] = ISNULL(SUM(CASE WHEN e.[IsEarned] = 0 AND e.[Status] NOT IN (
                                        N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                                        N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED',
                                        N'OTP_MAX_ATTEMPTS_EXCEEDED') THEN e.[Amount] END), 0)
    FROM [Booking].[BookingAmounts](NULL, @PetParentId, @FeePercentage) e
    WHERE (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate)
      AND (@PetId IS NULL OR e.[PetId] = @PetId)
      AND (@Statuses IS NULL OR @Statuses = N''
           OR e.[Status] IN (SELECT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@Statuses, N',')));
END;
GO
PRINT 'Created/updated [Booking].[GetPetParentBookingSummary].';
GO

-- ListPetParentBookingHistory: paginated history, single-day and night-stay merged
-- into one feed. Two result sets — [TotalCount] before paging, then the page. The
-- WHERE clause is deliberately identical to GetPetParentBookingSummary's, so the
-- summary always describes exactly this set: change one, change the other.
-- Unlike the provider earnings list this is NOT restricted to [IsEarned] rows —
-- cancelled and upcoming bookings belong in a history screen.
CREATE OR ALTER PROCEDURE [Booking].[ListPetParentBookingHistory]
    @PetParentId UNIQUEIDENTIFIER,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @PetId UNIQUEIDENTIFIER = NULL,
    @Statuses NVARCHAR(MAX) = NULL,
    @FeePercentage DECIMAL(9, 4) = 0,
    @SortBy NVARCHAR(16) = N'Date',
    @SortDirection NVARCHAR(4) = N'Desc',
    @Skip INT = 0,
    @Take INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [TotalCount] = COUNT(*)
    FROM [Booking].[BookingAmounts](NULL, @PetParentId, @FeePercentage) e
    WHERE (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate)
      AND (@PetId IS NULL OR e.[PetId] = @PetId)
      AND (@Statuses IS NULL OR @Statuses = N''
           OR e.[Status] IN (SELECT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@Statuses, N',')));

    SELECT
        e.[BookingType],
        e.[BookingId],
        [JobNumber]        = COALESCE(b.[JobNumber], n.[JobNumber]),
        e.[Status],
        [ServiceId]        = COALESCE(b.[ServiceId], n.[ServiceId]),
        [ServiceCategory]  = COALESCE(b.[ServiceCategory], n.[ServiceCategory]),
        [SubCategory]      = COALESCE(b.[SubCategory], n.[SubCategory]),
        [ServiceItemCode]  = b.[ServiceItemCode],
        e.[ServiceDate],
        [BookingDate]      = b.[BookingDate],
        [StartTime]        = b.[StartTime],
        [EndTime]          = b.[EndTime],
        [CheckInDate]      = n.[CheckInDate],
        [CheckOutDate]     = n.[CheckOutDate],
        [Nights]           = CASE WHEN n.[NightStayBookingId] IS NOT NULL
                                  THEN DATEDIFF(DAY, n.[CheckInDate], n.[CheckOutDate]) END,
        [ProviderId]       = e.[ProviderId],
        -- Personal name from SQL; the BUSINESS name lives in the Cosmos offering
        -- doc and isn't joinable here — the booking-detail read does the same.
        [ProviderName]     = pr.[FirstName] + N' ' + pr.[LastName],
        e.[PetId],
        [PetName]          = pet.[PetName],
        [PetProfilePhotoUrl] = pet.[ProfilePhotoUrl],
        e.[IsEarned],
        e.[IsPaid],
        -- Gross only. The Pawfront commission is the provider's concern — the
        -- parent pays this amount either way.
        e.[Amount],
        e.[PaidAtUtc],
        e.[PaymentMethod]
    FROM [Booking].[BookingAmounts](NULL, @PetParentId, @FeePercentage) e
    LEFT JOIN [Booking].[Bookings] b
        ON e.[BookingType] = N'SingleDay' AND b.[BookingId] = e.[BookingId]
    LEFT JOIN [Booking].[NightStayBookings] n
        ON e.[BookingType] = N'NightStay' AND n.[NightStayBookingId] = e.[BookingId]
    LEFT JOIN [Provider].[Providers] pr
        ON pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[Pets] pet
        ON pet.[PetId] = e.[PetId]
    WHERE (@FromDate IS NULL OR e.[ServiceDate] >= @FromDate)
      AND (@ToDate IS NULL OR e.[ServiceDate] <= @ToDate)
      AND (@PetId IS NULL OR e.[PetId] = @PetId)
      AND (@Statuses IS NULL OR @Statuses = N''
           OR e.[Status] IN (SELECT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@Statuses, N',')))
    ORDER BY
        CASE WHEN @SortBy = N'Amount' AND @SortDirection = N'Asc'  THEN e.[Amount] END ASC,
        CASE WHEN @SortBy = N'Amount' AND @SortDirection = N'Desc' THEN e.[Amount] END DESC,
        CASE WHEN @SortBy <> N'Amount' AND @SortDirection = N'Asc'  THEN e.[ServiceDate] END ASC,
        CASE WHEN @SortBy <> N'Amount' AND @SortDirection = N'Desc' THEN e.[ServiceDate] END DESC,
        e.[BookingId]
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;
GO
PRINT 'Created/updated [Booking].[ListPetParentBookingHistory].';
GO



--------------------------------------------------------------------------------
-- 3.90 Review procedures -------------------------------------------------------
--     Booking reviews, both directions. The eligibility gate (booking exists,
--     caller is that party, status is COMPLETED or PAID, App booking) lives in
--     [Review].[UpsertBookingReview] so it runs in the same transaction as the
--     write. Mirrors database/Pawfront.Database/StoredProcedures/*.sql — keep both
--     in sync.
--------------------------------------------------------------------------------

-- Records (or edits) one party's review of a finished booking. Handles BOTH
-- directions and BOTH booking kinds:
--   @ReviewerType = 'Parent'   -> the pet parent reviewing the provider; @ActorId
--                                 is their PetParentId. Rating + optional comment.
--   @ReviewerType = 'Provider' -> the provider rating the pet parent; @ActorId is
--                                 their ProviderId. Rating only — any comment
--                                 passed in is dropped (the provider-side endpoint
--                                 has no comment field; this only guards a direct
--                                 caller from tripping the table CHECK).
--
-- Deliberately ONE procedure rather than the single-day / night-stay twin pair used
-- elsewhere (cf. [Booking].[MarkBookingPaid] + [Booking].[MarkNightStayBookingPaid]).
-- Those twins exist because the flows genuinely differ — date range vs time window,
-- different grace windows, different codes. Here the ONLY difference is which table
-- supplies the two party ids and the status, so twinning would just be two copies of
-- the same gate to keep in step.
--
-- Submitting again replaces the rating and comment on the SAME row (the endpoint is
-- an upsert), so a corrected star or a fixed typo does not create a second review.
-- Photos are attached separately via [Review].[AddBookingReviewPhoto] — the blob
-- path is keyed by the review id, which does not exist until this runs.
--
-- Returns TWO result sets: the review row, then its photos (empty on first submit).
--
-- THROWs: 51300 booking not found, 51301 caller is not that party to the booking,
-- 51302 booking has not reached COMPLETED / PAID, 51303 Custom walk-in (no
-- pet-parent record to author or receive a review), 51304 invalid argument.
CREATE OR ALTER PROCEDURE [Review].[UpsertBookingReview]
    @BookingType NVARCHAR(16),      -- 'SingleDay' | 'NightStay'
    @BookingId UNIQUEIDENTIFIER,
    @ReviewerType NVARCHAR(16),     -- 'Parent' | 'Provider'
    @ActorId UNIQUEIDENTIFIER,
    @Rating TINYINT,
    @Comment NVARCHAR(1000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Defensive: the API validates all three before calling, so reaching these is
    -- a direct-caller error rather than something a client can provoke.
    IF @BookingType NOT IN (N'SingleDay', N'NightStay')
        OR @ReviewerType NOT IN (N'Parent', N'Provider')
        OR @Rating IS NULL OR @Rating < 1 OR @Rating > 5
    BEGIN
        THROW 51304, 'Invalid review request.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @RowPetParent UNIQUEIDENTIFIER;
    DECLARE @Status NVARCHAR(48);
    DECLARE @Found BIT = 0;
    DECLARE @ExistingId UNIQUEIDENTIFIER;

    -- A provider rates, and says nothing more.
    IF @ReviewerType = N'Provider'
    BEGIN
        SET @Comment = NULL;
    END
    ELSE IF LTRIM(RTRIM(COALESCE(@Comment, N''))) = N''
    BEGIN
        -- Store "no comment written" as NULL, not as an empty string, so a rating
        -- with no words reads back the same whether the field was omitted or blanked.
        SET @Comment = NULL;
    END

    BEGIN TRANSACTION;

    -- No UPDLOCK on the booking, unlike most write paths here: this read cannot go
    -- stale in a way that matters. The only transition out of COMPLETED is to PAID
    -- and PAID is terminal, so once a booking is reviewable it stays reviewable —
    -- a concurrent status change can never invalidate a review we are about to
    -- accept. The lock that DOES matter is the one below, on the review row.
    IF @BookingType = N'SingleDay'
    BEGIN
        SELECT @RowProvider = [ProviderId],
               @RowPetParent = [PetParentId],
               @Status = [Status],
               @Found = 1
        FROM [Booking].[Bookings]
        WHERE [BookingId] = @BookingId;
    END
    ELSE
    BEGIN
        SELECT @RowProvider = [ProviderId],
               @RowPetParent = [PetParentId],
               @Status = [Status],
               @Found = 1
        FROM [Booking].[NightStayBookings]
        WHERE [NightStayBookingId] = @BookingId;
    END

    -- Every THROW below relies on SET XACT_ABORT ON to roll the transaction back,
    -- matching [Booking].[UpdateBookingStatus] and the rest of this codebase.
    IF @Found = 0
    BEGIN
        THROW 51300, 'Booking was not found.', 1;
    END

    -- Custom walk-ins carry free-text customer details and no PetParentId, so
    -- neither direction is possible: there is nobody to author the parent's review
    -- and nobody for the provider to rate. (Night-stay is App-only, so this can
    -- only fire on a single-day booking.)
    IF @RowPetParent IS NULL
    BEGIN
        THROW 51303, 'Only app bookings can be reviewed.', 1;
    END

    IF (@ReviewerType = N'Parent' AND @RowPetParent <> @ActorId)
        OR (@ReviewerType = N'Provider' AND @RowProvider <> @ActorId)
    BEGIN
        THROW 51301, 'You are not a party to this booking.', 1;
    END

    -- PAID counts as well as COMPLETED. PAID sits downstream of COMPLETED, so
    -- gating on COMPLETED alone would close the review window the moment the
    -- provider recorded the payment — which for cash is often immediately.
    IF @Status NOT IN (N'COMPLETED', N'PAID')
    BEGIN
        THROW 51302, 'Booking must be completed before it can be reviewed.', 1;
    END

    -- UPDLOCK + HOLDLOCK over the unique key range: when no row exists yet this
    -- takes a range lock, so two devices submitting at once serialise and the
    -- second updates the first's row instead of hitting a UNIQUE violation.
    SELECT @ExistingId = [BookingReviewId]
    FROM [Review].[BookingReviews] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingType] = @BookingType
      AND [BookingId] = @BookingId
      AND [ReviewerType] = @ReviewerType;

    IF @ExistingId IS NULL
    BEGIN
        DECLARE @Inserted TABLE ([BookingReviewId] UNIQUEIDENTIFIER);

        INSERT INTO [Review].[BookingReviews]
            ([BookingType], [BookingId], [ReviewerType], [ProviderId], [PetParentId],
             [Rating], [Comment], [CreatedAtUtc], [UpdatedAtUtc])
        OUTPUT inserted.[BookingReviewId] INTO @Inserted
        VALUES
            (@BookingType, @BookingId, @ReviewerType, @RowProvider, @RowPetParent,
             @Rating, @Comment, @Now, @Now);

        SELECT @ExistingId = [BookingReviewId] FROM @Inserted;
    END
    ELSE
    BEGIN
        -- CreatedAtUtc is left alone: it is the date the review was GIVEN, which is
        -- what the list sorts on and what the app shows. An edit is not a new review.
        UPDATE [Review].[BookingReviews]
        SET [Rating] = @Rating,
            [Comment] = @Comment,
            [UpdatedAtUtc] = @Now
        WHERE [BookingReviewId] = @ExistingId;
    END

    SELECT [BookingReviewId],
           [BookingType],
           [BookingId],
           [ReviewerType],
           [ProviderId],
           [PetParentId],
           [Rating],
           [Comment],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Review].[BookingReviews]
    WHERE [BookingReviewId] = @ExistingId;

    SELECT [BookingReviewPhotoId],
           [BookingReviewId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Review].[BookingReviewPhotos]
    WHERE [BookingReviewId] = @ExistingId
    ORDER BY [CreatedAtUtc] ASC, [BookingReviewPhotoId] ASC;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Review].[UpsertBookingReview].';
GO

-- Reads one party's review of a booking, if they have written one. Used for the
-- read-back endpoint and for the "own review" section appended to the two
-- booking-detail reads, so the app can tell "not reviewed yet" (prompt) from
-- "already reviewed" (show it, allow an edit).
--
-- Takes no actor: the caller's identity is already established by the route the
-- review is read through, and @ReviewerType alone says whose review is wanted.
--
-- Returns TWO result sets: the review row (EMPTY when none exists — that is the
-- ordinary case, not an error), then its photos.
CREATE OR ALTER PROCEDURE [Review].[GetBookingReview]
    @BookingType NVARCHAR(16),      -- 'SingleDay' | 'NightStay'
    @BookingId UNIQUEIDENTIFIER,
    @ReviewerType NVARCHAR(16)      -- 'Parent' | 'Provider'
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @BookingReviewId UNIQUEIDENTIFIER;

    SELECT @BookingReviewId = [BookingReviewId]
    FROM [Review].[BookingReviews]
    WHERE [BookingType] = @BookingType
      AND [BookingId] = @BookingId
      AND [ReviewerType] = @ReviewerType;

    SELECT [BookingReviewId],
           [BookingType],
           [BookingId],
           [ReviewerType],
           [ProviderId],
           [PetParentId],
           [Rating],
           [Comment],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Review].[BookingReviews]
    WHERE [BookingReviewId] = @BookingReviewId;

    SELECT [BookingReviewPhotoId],
           [BookingReviewId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Review].[BookingReviewPhotos]
    WHERE [BookingReviewId] = @BookingReviewId
    ORDER BY [CreatedAtUtc] ASC, [BookingReviewPhotoId] ASC;
END;
GO
PRINT 'Created/updated [Review].[GetBookingReview].';
GO

-- Records one photo against a pet parent's booking review (the blob upload happens
-- in the app layer; this stores the resulting URL). Scoped to the review's own
-- author: a provider-direction review is rating-only and can never carry photos.
--
-- The per-review cap is enforced HERE rather than only in C# because the count is a
-- race: two uploads in flight would each read four existing photos and both insert.
-- UPDLOCK + HOLDLOCK on the count makes them serialise.
--
-- THROWs: 51305 review not found for this author (unknown id and "not yours" are
-- deliberately the same case, so it cannot be used to probe whether a review
-- exists), 51306 the photo cap for this review is already reached.
CREATE OR ALTER PROCEDURE [Review].[AddBookingReviewPhoto]
    @BookingReviewId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000),
    @MaxPhotos INT = 5
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Existing INT;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Review].[BookingReviews] WITH (UPDLOCK, HOLDLOCK)
        WHERE [BookingReviewId] = @BookingReviewId
          AND [PetParentId] = @PetParentId
          AND [ReviewerType] = N'Parent')
    BEGIN
        -- SET XACT_ABORT ON rolls the transaction back, as elsewhere in this codebase.
        THROW 51305, 'Review was not found for this pet parent.', 1;
    END

    SELECT @Existing = COUNT(*)
    FROM [Review].[BookingReviewPhotos] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingReviewId] = @BookingReviewId;

    IF @Existing >= @MaxPhotos
    BEGIN
        THROW 51306, 'This review already has the maximum number of photos.', 1;
    END

    DECLARE @Inserted TABLE ([BookingReviewPhotoId] UNIQUEIDENTIFIER);

    INSERT INTO [Review].[BookingReviewPhotos] ([BookingReviewId], [PhotoUrl])
    OUTPUT inserted.[BookingReviewPhotoId] INTO @Inserted
    VALUES (@BookingReviewId, @PhotoUrl);

    SELECT [BookingReviewPhotoId],
           [BookingReviewId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Review].[BookingReviewPhotos]
    WHERE [BookingReviewPhotoId] = (SELECT TOP (1) [BookingReviewPhotoId] FROM @Inserted);

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Review].[AddBookingReviewPhoto].';
GO

-- Removes one photo from a pet parent's booking review, scoped by review + author
-- so a caller can only ever delete their own. The row here is the source of truth;
-- the app layer makes a best-effort attempt at the blob afterwards, which is why the
-- deleted [PhotoUrl] is returned.
--
-- The review itself is NOT deletable — only its photos — so this never leaves a
-- rating stranded.
--
-- THROWs: 51307 photo not found for this review and author (unknown id, wrong
-- review, and "not yours" are deliberately one case).
CREATE OR ALTER PROCEDURE [Review].[DeleteBookingReviewPhoto]
    @BookingReviewId UNIQUEIDENTIFIER,
    @BookingReviewPhotoId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @PhotoUrl NVARCHAR(1000);

    BEGIN TRANSACTION;

    SELECT @PhotoUrl = p.[PhotoUrl]
    FROM [Review].[BookingReviewPhotos] p WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN [Review].[BookingReviews] r
        ON r.[BookingReviewId] = p.[BookingReviewId]
    WHERE p.[BookingReviewPhotoId] = @BookingReviewPhotoId
      AND p.[BookingReviewId] = @BookingReviewId
      AND r.[PetParentId] = @PetParentId
      AND r.[ReviewerType] = N'Parent';

    IF @PhotoUrl IS NULL
    BEGIN
        -- SET XACT_ABORT ON rolls the transaction back, as elsewhere in this codebase.
        THROW 51307, 'Review photo was not found.', 1;
    END

    DELETE FROM [Review].[BookingReviewPhotos]
    WHERE [BookingReviewPhotoId] = @BookingReviewPhotoId;

    SELECT [BookingReviewPhotoId] = @BookingReviewPhotoId,
           [BookingReviewId] = @BookingReviewId,
           [PhotoUrl] = @PhotoUrl,
           [DeletedAtUtc] = SYSUTCDATETIME();

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Review].[DeleteBookingReviewPhoto].';
GO

-- The reviews pet parents have left for a provider: the list behind the provider's
-- public profile and their own "my reviews" screen. Paginated (the API caps @Take at
-- 20), sortable by the date the review was given or by the rating score, either
-- direction.
--
-- Returns THREE result sets:
--   1. Summary over ALL the provider's reviews (not just this page) — count,
--      average, and the 1-5 histogram, so the header reads "4.6 (23)" without a
--      second call.
--   2. The page itself, ordered.
--   3. The photos belonging to that page's reviews, so the client needs no per-review
--      follow-up (an N+1 across a page of reviews is the thing to avoid here).
--
-- Only parent-authored rows are considered: a provider's ratings OF parents are the
-- other direction and belong on the customer card, not here. The filtered index
-- [IX_BookingReviews_Provider_Created] matches that predicate exactly.
--
-- The parent's name and photo are joined LIVE rather than denormalised onto the
-- review, so a parent who deletes their account correctly reads "Deleted User"
-- instead of leaving their real name frozen in every review they ever wrote.
CREATE OR ALTER PROCEDURE [Review].[ListProviderReviews]
    @ProviderId UNIQUEIDENTIFIER,
    @SortBy NVARCHAR(16) = N'Date',        -- 'Date' | 'Rating'
    @SortDirection NVARCHAR(4) = N'Desc',  -- 'Asc'  | 'Desc'
    @Skip INT = 0,
    @Take INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    -- 1. Summary. With no reviews at all, AVG yields NULL and every count is 0 —
    -- which is exactly what the profile should show for a provider nobody has
    -- reviewed yet, so there is no special-casing to do.
    -- The SUMs are COALESCEd because SUM over ZERO rows is NULL, not 0 — the counts
    -- must stay non-null integers so the reader needs no null check per bucket. The
    -- average is deliberately left nullable (see above).
    SELECT
        [ReviewCount]   = COUNT(*),
        [AverageRating] = CAST(AVG(CAST([Rating] AS DECIMAL(9, 4))) AS DECIMAL(3, 2)),
        [FiveStar]      = COALESCE(SUM(CASE WHEN [Rating] = 5 THEN 1 ELSE 0 END), 0),
        [FourStar]      = COALESCE(SUM(CASE WHEN [Rating] = 4 THEN 1 ELSE 0 END), 0),
        [ThreeStar]     = COALESCE(SUM(CASE WHEN [Rating] = 3 THEN 1 ELSE 0 END), 0),
        [TwoStar]       = COALESCE(SUM(CASE WHEN [Rating] = 2 THEN 1 ELSE 0 END), 0),
        [OneStar]       = COALESCE(SUM(CASE WHEN [Rating] = 1 THEN 1 ELSE 0 END), 0)
    FROM [Review].[BookingReviews]
    WHERE [ProviderId] = @ProviderId
      AND [ReviewerType] = N'Parent';

    -- Resolve the page's ids once, so result sets 2 and 3 describe the same rows
    -- even under a concurrent insert, and so the photo read is a join rather than a
    -- repeat of the ordering logic.
    DECLARE @Page TABLE (
        [Ordinal] INT NOT NULL PRIMARY KEY,
        [BookingReviewId] UNIQUEIDENTIFIER NOT NULL UNIQUE);

    INSERT INTO @Page ([Ordinal], [BookingReviewId])
    SELECT ordered.[Ordinal], ordered.[BookingReviewId]
    FROM (
        SELECT
            r.[BookingReviewId],
            [Ordinal] = ROW_NUMBER() OVER (ORDER BY
                -- Rating, when that is what was asked for.
                CASE WHEN @SortBy = N'Rating' AND @SortDirection = N'Asc'  THEN r.[Rating] END ASC,
                CASE WHEN @SortBy = N'Rating' AND @SortDirection = N'Desc' THEN r.[Rating] END DESC,
                -- The date given: the primary key for @SortBy = 'Date', and the
                -- secondary for a rating sort — newest first within a star band,
                -- which is what a reader scanning "all the 5s" expects.
                CASE WHEN @SortBy = N'Date' AND @SortDirection = N'Asc' THEN r.[CreatedAtUtc] END ASC,
                CASE WHEN @SortBy = N'Date' AND @SortDirection = N'Asc' THEN NULL
                     ELSE r.[CreatedAtUtc] END DESC,
                -- Deterministic tie-break. Without it, reviews sharing a timestamp
                -- (or a score) can order differently between two calls, which makes
                -- OFFSET paging repeat or skip rows.
                r.[BookingReviewId])
        FROM [Review].[BookingReviews] r
        WHERE r.[ProviderId] = @ProviderId
          AND r.[ReviewerType] = N'Parent'
    ) ordered
    WHERE ordered.[Ordinal] > @Skip
      AND ordered.[Ordinal] <= @Skip + @Take;

    -- 2. The page.
    SELECT
        r.[BookingReviewId],
        r.[BookingType],
        r.[BookingId],
        -- Raw job number; the 'PF-000123' label is formatted in C# exactly as the
        -- booking-detail read does, so the two surfaces show the same id. NULL only
        -- if the booking row has since gone (it does not: bookings are retained
        -- through both account deletes).
        [JobNumber] = COALESCE(b.[JobNumber], n.[JobNumber]),
        r.[PetParentId],
        [ParentName] = pp.[FirstName] + N' ' + pp.[LastName],
        [ParentPhotoUrl] = pp.[ProfilePhotoUrl],
        r.[Rating],
        r.[Comment],
        r.[CreatedAtUtc],
        r.[UpdatedAtUtc]
    FROM @Page pg
    INNER JOIN [Review].[BookingReviews] r
        ON r.[BookingReviewId] = pg.[BookingReviewId]
    LEFT JOIN [Booking].[Bookings] b
        ON r.[BookingType] = N'SingleDay' AND b.[BookingId] = r.[BookingId]
    LEFT JOIN [Booking].[NightStayBookings] n
        ON r.[BookingType] = N'NightStay' AND n.[NightStayBookingId] = r.[BookingId]
    LEFT JOIN [Parent].[PetParents] pp
        ON pp.[PetParentId] = r.[PetParentId]
    ORDER BY pg.[Ordinal] ASC;

    -- 3. That page's photos, grouped in C# by BookingReviewId.
    SELECT
        p.[BookingReviewPhotoId],
        p.[BookingReviewId],
        p.[PhotoUrl],
        p.[CreatedAtUtc]
    FROM @Page pg
    INNER JOIN [Review].[BookingReviewPhotos] p
        ON p.[BookingReviewId] = pg.[BookingReviewId]
    ORDER BY pg.[Ordinal] ASC, p.[CreatedAtUtc] ASC, p.[BookingReviewPhotoId] ASC;
END;
GO
PRINT 'Created/updated [Review].[ListProviderReviews].';
GO

-- Every review a pet parent has AUTHORED, keyed by the booking it belongs to.
--
-- Backs the `review` block on the parent's two "my bookings" lists
-- (GET /pet-parents/{id}/bookings and .../night-stay-bookings): one call per list
-- rather than a per-booking lookup, which is why it is scoped to the parent and
-- takes no booking ids. Both booking kinds come back together — the caller reads
-- one list at a time but the extra rows are a handful at most, and a single
-- procedure keeps the two surfaces from drifting.
--
-- [ReviewerType] = 'Parent' only: the provider's private rating OF this parent is
-- deliberately never returned on the parent host.
--
-- [BookingType] travels with [BookingId] because the two booking kinds live in
-- separate tables and share no id space, so the id alone cannot say which list
-- row a rating belongs to.
--
-- No THROW: an unknown parent, or one who has reviewed nothing, is an empty set —
-- the ordinary case, not an error.
CREATE OR ALTER PROCEDURE [Review].[ListPetParentBookingReviews]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingType],
           [BookingId],
           [Rating]
    FROM [Review].[BookingReviews]
    WHERE [PetParentId] = @PetParentId
      AND [ReviewerType] = N'Parent';
END;
GO
PRINT 'Created/updated [Review].[ListPetParentBookingReviews].';
GO

-- A pet parent's aggregate rating, as given BY providers they have booked with.
-- Feeds the [Rating] field on the provider-facing customer card
-- (GET /pet-parents/{petParentId}/details on the provider host), which was wired
-- ahead of this feature and had always returned null.
--
-- The provider direction is rating-only, so there is no comment or photo to read —
-- just the average and the count. The count matters as much as the average: "5.0"
-- off one rating and "4.6" off forty are very different claims, and the card should
-- be able to say which it is.
--
-- Always returns exactly ONE row. A parent nobody has rated yet gets a NULL average
-- and a count of 0 rather than an empty result set, so the caller needs no
-- no-rows branch.
CREATE OR ALTER PROCEDURE [Review].[GetPetParentRatingSummary]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        [RatingCount]   = COUNT(*),
        [AverageRating] = CAST(AVG(CAST([Rating] AS DECIMAL(9, 4))) AS DECIMAL(3, 2))
    FROM [Review].[BookingReviews]
    WHERE [PetParentId] = @PetParentId
      AND [ReviewerType] = N'Provider';
END;
GO
PRINT 'Created/updated [Review].[GetPetParentRatingSummary].';
GO

-- Every job between ONE provider and ONE pet parent, newest first.
--
-- Backs the chat screen's "View Jobs": the two of them are already talking, and
-- the question the screen asks is "what work have we done together?" — so unlike
-- the two "my bookings" lists this is scoped to the PAIR, and unlike the pending
-- job lists it is not filtered by status. A cancelled or completed job is part of
-- that history and belongs on the list.
--
-- Both booking kinds, merged into one feed. [BookingType] discriminates, and the
-- other kind's columns are NULL — the same shape [Booking].[BookingAmounts] and
-- the two delete refusals use, because a person thinks "our jobs", not "our two
-- kinds of jobs".
--
-- The projection is DELIBERATELY IDENTICAL, column for column and in the same
-- order, to the pending-job result sets of [Parent].[DeletePetParent] (result set
-- 3) and [Parent].[DeletePetParentPet] (result set 2). All three are read by the
-- single C# PendingJobReader, so one job card renders everywhere it appears.
-- Change the column list in one and you must change it in all of them and in that
-- reader. The last four columns are pricing inputs the caller turns into a money
-- block, since SQL cannot reach the Cosmos offering.
--
-- Custom walk-ins can never appear: they carry no PetParentId, so the pair
-- predicate excludes them without needing to say so.
--
-- Returns ONE result set — the page, with the whole-result count appended as a
-- 17th column. COUNT(*) OVER() is evaluated before OFFSET/FETCH, so it counts the
-- pair's entire history rather than the page, which is what lets the caller
-- report hasMore and a total from one round trip. The reader takes it from the
-- first row; an empty page means a total of zero.
CREATE OR ALTER PROCEDURE [Booking].[ListBookingsForParentProvider]
    @ProviderId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @Skip INT = 0,
    @Take INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    IF @Skip IS NULL OR @Skip < 0 SET @Skip = 0;
    IF @Take IS NULL OR @Take < 1 SET @Take = 20;

    WITH [Jobs] AS
    (
        SELECT b.[BookingId] AS [BookingId],
               N'SingleDay' AS [BookingType],
               N'PF-' + FORMAT(b.[JobNumber], N'D6') AS [JobId],
               b.[ProviderId] AS [ProviderId],
               NULLIF(LTRIM(RTRIM(ISNULL(pr.[FirstName], N'') + N' ' + ISNULL(pr.[LastName], N''))), N'')
                   AS [ProviderName],
               b.[ServiceCategory] AS [ServiceCategory],
               b.[SubCategory] AS [SubCategory],
               b.[Status] AS [Status],
               b.[BookingDate] AS [ServiceDate],
               b.[StartTime] AS [StartTime],
               b.[EndTime] AS [EndTime],
               pet.[PetName] AS [PetName],
               b.[ServiceId] AS [ServiceId],
               b.[ServiceItemCode] AS [ServiceItemCode],
               CAST(NULL AS DATE) AS [CheckOutDate],
               b.[PricePerHour] AS [SnapshotUnitPrice],
               b.[CreatedAtUtc] AS [CreatedAtUtc]
        FROM [Booking].[Bookings] AS b
        LEFT JOIN [Provider].[Providers] AS pr ON pr.[ProviderId] = b.[ProviderId]
        LEFT JOIN [Parent].[Pets] AS pet ON pet.[PetId] = b.[PetId]
        WHERE b.[ProviderId] = @ProviderId
          AND b.[PetParentId] = @PetParentId

        UNION ALL

        SELECT n.[NightStayBookingId],
               N'NightStay',
               N'PF-' + FORMAT(n.[JobNumber], N'D6'),
               n.[ProviderId],
               NULLIF(LTRIM(RTRIM(ISNULL(pr.[FirstName], N'') + N' ' + ISNULL(pr.[LastName], N''))), N''),
               n.[ServiceCategory],
               n.[SubCategory],
               n.[Status],
               n.[CheckInDate],
               n.[DropOffTime],
               n.[PickUpTime],
               pet.[PetName],
               n.[ServiceId],
               NULL,
               n.[CheckOutDate],
               n.[PricePerNight],
               n.[CreatedAtUtc]
        FROM [Booking].[NightStayBookings] AS n
        LEFT JOIN [Provider].[Providers] AS pr ON pr.[ProviderId] = n.[ProviderId]
        LEFT JOIN [Parent].[Pets] AS pet ON pet.[PetId] = n.[PetId]
        WHERE n.[ProviderId] = @ProviderId
          AND n.[PetParentId] = @PetParentId
    )
    SELECT [BookingId],
           [BookingType],
           [JobId],
           [ProviderId],
           [ProviderName],
           [ServiceCategory],
           [SubCategory],
           [Status],
           [ServiceDate],
           [StartTime],
           [EndTime],
           [PetName],
           [ServiceId],
           [ServiceItemCode],
           [CheckOutDate],
           [SnapshotUnitPrice],
           COUNT(*) OVER () AS [TotalCount]
    FROM [Jobs]
    -- By the date the service happens, not the date it was booked — the list is a
    -- calendar of what these two have done together. StartTime orders a day with
    -- several jobs in it; BookingId is the tie-break that stops OFFSET paging
    -- repeating or skipping a row, the same one the earnings and review lists use.
    ORDER BY [ServiceDate] DESC, [StartTime] DESC, [BookingId] DESC
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;
GO
PRINT 'Created/updated [Booking].[ListBookingsForParentProvider].';
GO



--------------------------------------------------------------------------------
-- 2.25 Chat.* — provider <-> pet-parent messaging
--------------------------------------------------------------------------------
-- SQL owns the thread index, per-side read state, live connections and blocks.
-- Message BODIES live in the Cosmos "ChatMessages" container partitioned by
-- /conversationId — the same split as [Event].[Events] + the Events document.

-- 2.25.1 Chat.Conversations ----------------------------------------------------
-- One thread per (provider, pet parent) pair. The UNIQUE below is load-bearing:
-- it makes "one thread per pair" a database fact, which is what lets
-- [Chat].[GetOrCreateConversation] be race-safe with a lock over that one key.
--
-- NO FK to [Provider].[Providers] / [Parent].[PetParents] — same posture as
-- [Booking].[BookingPayments] and [Notification].[NotificationOutbox]. An
-- anonymised account keeps its conversations: the counterparty was part of those
-- exchanges too. Names come from a LIVE join at read time, so a deleted account
-- correctly reads "Deleted Provider" / "Deleted User".
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'Conversations' AND [schema_id] = SCHEMA_ID(N'Chat'))
BEGIN
    CREATE TABLE [Chat].[Conversations]
    (
        [ConversationId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_Conversations_ConversationId] DEFAULT NEWSEQUENTIALID(),
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [PetParentId] UNIQUEIDENTIFIER NOT NULL,
        -- Monotonic per conversation; orders the thread and is the ?beforeSequence=
        -- paging cursor. A HIGH-WATER MARK, not a count — the Cosmos body is written
        -- after this advances, so a crash in between burns a number. Gaps are
        -- harmless, which is exactly why there is no [MessageCount] column to be wrong.
        [LastSequence] BIGINT NOT NULL
            CONSTRAINT [DF_Conversations_LastSequence] DEFAULT 0,
        -- Newest-message cache for the inbox card, so the list is one indexed read
        -- rather than a Cosmos query per thread. NULL until the first message: a
        -- conversation exists from the moment somebody opens it.
        [LastMessageAtUtc] DATETIME2(7) NULL,
        [LastMessagePreview] NVARCHAR(200) NULL,
        [LastMessageSenderType] NVARCHAR(16) NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Conversations_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_Conversations_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_Conversations] PRIMARY KEY CLUSTERED ([ConversationId] ASC),
        CONSTRAINT [UQ_Conversations_Pair] UNIQUE ([ProviderId], [PetParentId]),
        CONSTRAINT [CK_Conversations_LastMessageSenderType]
            CHECK ([LastMessageSenderType] IS NULL
                   OR [LastMessageSenderType] IN (N'Provider', N'PetParent'))
    );
    PRINT 'Created table [Chat].[Conversations].';
END
ELSE
BEGIN
    PRINT 'Table [Chat].[Conversations] already exists.';
END
GO

-- The two inbox reads. One index per side, because the participants live in
-- different columns.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Conversations_Provider_LastMessage'
      AND [object_id] = OBJECT_ID(N'[Chat].[Conversations]'))
    CREATE INDEX [IX_Conversations_Provider_LastMessage]
        ON [Chat].[Conversations] ([ProviderId], [LastMessageAtUtc] DESC)
        INCLUDE ([PetParentId], [LastMessagePreview], [LastMessageSenderType], [LastSequence]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_Conversations_PetParent_LastMessage'
      AND [object_id] = OBJECT_ID(N'[Chat].[Conversations]'))
    CREATE INDEX [IX_Conversations_PetParent_LastMessage]
        ON [Chat].[Conversations] ([PetParentId], [LastMessageAtUtc] DESC)
        INCLUDE ([ProviderId], [LastMessagePreview], [LastMessageSenderType], [LastSequence]);
GO


-- 2.25.2 Chat.ConversationParticipants -----------------------------------------
-- Per-side read state. A separate table rather than four more columns on
-- [Chat].[Conversations] because every read and write of this state is symmetric
-- ("advance the sender, increment the recipient"), and column pairs would force a
-- CASE WHEN @ActorType branch into every one of those statements. With a row per
-- side, [Chat].[CommitMessageAppend] updates both in a single statement.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ConversationParticipants' AND [schema_id] = SCHEMA_ID(N'Chat'))
BEGIN
    CREATE TABLE [Chat].[ConversationParticipants]
    (
        [ConversationParticipantId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_ConversationParticipants_Id] DEFAULT NEWSEQUENTIALID(),
        [ConversationId] UNIQUEIDENTIFIER NOT NULL,
        [ParticipantType] NVARCHAR(16) NOT NULL,
        [ParticipantId] UNIQUEIDENTIFIER NOT NULL,
        [LastReadSequence] BIGINT NOT NULL
            CONSTRAINT [DF_ConversationParticipants_LastReadSequence] DEFAULT 0,
        -- A real counter, not something derived from the sequence gap: sequences
        -- can have gaps, so subtracting would over-count.
        [UnreadCount] INT NOT NULL
            CONSTRAINT [DF_ConversationParticipants_UnreadCount] DEFAULT 0,
        -- Suppresses the push for this thread only. The message still arrives over
        -- the socket and still lands in the inbox.
        [IsMuted] BIT NOT NULL
            CONSTRAINT [DF_ConversationParticipants_IsMuted] DEFAULT 0,
        -- "Delete this chat", for THIS side only. Bodies are shared Cosmos
        -- documents read by both parties, so one side clearing their copy can
        -- only ever be a watermark: history at or below it stops being returned
        -- to them, and the counterparty's thread is untouched.
        [ClearedUpToSequence] BIGINT NOT NULL
            CONSTRAINT [DF_ConversationParticipants_ClearedUpToSequence] DEFAULT 0,
        -- When this side last cleared it; NULL means never. Needed as well as the
        -- watermark because a brand-new thread also satisfies
        -- ClearedUpToSequence >= LastSequence (0 >= 0) and must not be hidden —
        -- and because a cleared thread reappears the moment the counterparty
        -- writes, which is what stops a delete cutting the caller off.
        [DeletedAtUtc] DATETIME2(7) NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ConversationParticipants_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        [UpdatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ConversationParticipants_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_ConversationParticipants] PRIMARY KEY CLUSTERED ([ConversationParticipantId] ASC),
        CONSTRAINT [FK_ConversationParticipants_Conversations_ConversationId]
            FOREIGN KEY ([ConversationId]) REFERENCES [Chat].[Conversations] ([ConversationId])
            ON DELETE CASCADE,
        CONSTRAINT [UQ_ConversationParticipants_Conversation_Type]
            UNIQUE ([ConversationId], [ParticipantType]),
        CONSTRAINT [CK_ConversationParticipants_ParticipantType]
            CHECK ([ParticipantType] IN (N'Provider', N'PetParent')),
        CONSTRAINT [CK_ConversationParticipants_UnreadCount]
            CHECK ([UnreadCount] >= 0)
    );
    PRINT 'Created table [Chat].[ConversationParticipants].';
END
ELSE
BEGIN
    PRINT 'Table [Chat].[ConversationParticipants] already exists.';
END
GO

-- Per-side "delete chat" (2026-08-14) on a table that already exists. The
-- CREATE above only runs on a fresh database, so an established one needs these
-- as ALTERs. Both are additive with safe defaults — 0 and NULL together mean
-- "never cleared", which is exactly the pre-existing behaviour, so the change is
-- inert until somebody deletes a chat.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Chat].[ConversationParticipants]')
      AND [name] = N'ClearedUpToSequence')
BEGIN
    ALTER TABLE [Chat].[ConversationParticipants]
        ADD [ClearedUpToSequence] BIGINT NOT NULL
            CONSTRAINT [DF_ConversationParticipants_ClearedUpToSequence] DEFAULT 0;
    PRINT 'Added [Chat].[ConversationParticipants].[ClearedUpToSequence].';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE [object_id] = OBJECT_ID(N'[Chat].[ConversationParticipants]')
      AND [name] = N'DeletedAtUtc')
BEGIN
    ALTER TABLE [Chat].[ConversationParticipants] ADD [DeletedAtUtc] DATETIME2(7) NULL;
    PRINT 'Added [Chat].[ConversationParticipants].[DeletedAtUtc].';
END
GO

-- The unread badge, and the per-participant lookups on the send path.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ConversationParticipants_Participant'
      AND [object_id] = OBJECT_ID(N'[Chat].[ConversationParticipants]'))
    CREATE INDEX [IX_ConversationParticipants_Participant]
        ON [Chat].[ConversationParticipants] ([ParticipantType], [ParticipantId])
        INCLUDE ([ConversationId], [UnreadCount], [LastReadSequence], [IsMuted]);
GO


-- 2.25.2b Chat.ConversationMessages ---------------------------------------------
-- The per-message ledger that makes sending atomic and exactly-once. NOT a copy of
-- the message — the body still lives in Cosmos. This holds only what SQL has to be
-- the authority on: the sequence, and whether the body was ever durably written.
--
--   1. IDEMPOTENCY. The primary key on ([ConversationId], [MessageId]) makes "one
--      message per client id per thread" a database fact. A retry over the hub, or
--      over REST, or one of each, collides on it inside
--      [Chat].[ReserveMessageSequence] and is handed the ORIGINAL sequence back.
--      This used to be left entirely to the Cosmos document id, which cannot help
--      when the Cosmos write is the very thing that failed.
--
--   2. ATOMICITY. A send is reserve -> write body -> commit. Reserve assigns the
--      sequence and nothing else; every observable effect (inbox preview, unread
--      count, push) belongs to commit. A body that never lands therefore leaves
--      nothing a client can see, and [CommittedAtUtc] is how commit knows whether
--      it has already run.
--
-- The one residue of a failed send is a spent sequence number — a gap, and gaps
-- have always been free here (see [Chat].[Conversations].[LastSequence]).
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ConversationMessages' AND [schema_id] = SCHEMA_ID(N'Chat'))
BEGIN
    CREATE TABLE [Chat].[ConversationMessages]
    (
        [ConversationId] UNIQUEIDENTIFIER NOT NULL,
        -- Client-generated, and also the Cosmos document id.
        [MessageId] UNIQUEIDENTIFIER NOT NULL,
        [Sequence] BIGINT NOT NULL,
        -- Copied from the reservation so [Chat].[CommitMessageAppend] works out who
        -- to notify without being told again — the two phases cannot disagree about
        -- who sent the message.
        [SenderType] NVARCHAR(16) NOT NULL,
        [SenderId] UNIQUEIDENTIFIER NOT NULL,
        -- When the send was accepted; this is the message's CreatedAtUtc. The body
        -- is written between the two phases, so the timestamp comes from the first.
        [ReservedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ConversationMessages_ReservedAtUtc] DEFAULT SYSUTCDATETIME(),
        -- NULL until the body is durable. A row that stays NULL is a send that
        -- failed between the phases; only a retry of the same [MessageId] reads it.
        [CommittedAtUtc] DATETIME2(7) NULL,
        CONSTRAINT [PK_ConversationMessages]
            PRIMARY KEY CLUSTERED ([ConversationId] ASC, [MessageId] ASC),
        -- Two messages in a thread can never share a sequence. Belt and braces
        -- behind the UPDLOCK in the reserve procedure, and the index the
        -- "is this still the newest committed message?" check seeks on.
        CONSTRAINT [UQ_ConversationMessages_Sequence]
            UNIQUE ([ConversationId], [Sequence]),
        CONSTRAINT [FK_ConversationMessages_Conversations_ConversationId]
            FOREIGN KEY ([ConversationId]) REFERENCES [Chat].[Conversations] ([ConversationId])
            ON DELETE CASCADE,
        CONSTRAINT [CK_ConversationMessages_SenderType]
            CHECK ([SenderType] IN (N'Provider', N'PetParent'))
    );
    PRINT 'Created table [Chat].[ConversationMessages].';
END
ELSE
BEGIN
    PRINT 'Table [Chat].[ConversationMessages] already exists.';
END
GO


-- 2.25.3 Chat.ChatConnections --------------------------------------------------
-- Live SignalR connections, and which thread each has open. This exists for
-- exactly one decision: whether a message earns an FCM push. Without it the choice
-- would be to buzz someone for a message they are reading, or to leave an offline
-- recipient silent.
--
-- OPERATIONAL data, not history. [Chat].[PurgeStaleConnections] is what makes it
-- trustworthy — OnDisconnectedAsync does not run if the host crashes, and a stale
-- row does not merely waste space, it makes the recipient look present forever.
--
-- No FK on [ActiveConversationId] on purpose: it is a transient pointer, and a FK
-- would make deleting a conversation depend on nobody currently viewing it.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'ChatConnections' AND [schema_id] = SCHEMA_ID(N'Chat'))
BEGIN
    CREATE TABLE [Chat].[ChatConnections]
    (
        [ConnectionId] NVARCHAR(128) NOT NULL,
        [ParticipantType] NVARCHAR(16) NOT NULL,
        [ParticipantId] UNIQUEIDENTIFIER NOT NULL,
        -- NULL when connected but elsewhere in the app — the common case, and the
        -- one that still earns a push.
        [ActiveConversationId] UNIQUEIDENTIFIER NULL,
        [ConnectedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ChatConnections_ConnectedAtUtc] DEFAULT SYSUTCDATETIME(),
        [LastHeartbeatAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_ChatConnections_LastHeartbeatAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_ChatConnections] PRIMARY KEY CLUSTERED ([ConnectionId] ASC),
        CONSTRAINT [CK_ChatConnections_ParticipantType]
            CHECK ([ParticipantType] IN (N'Provider', N'PetParent'))
    );
    PRINT 'Created table [Chat].[ChatConnections].';
END
ELSE
BEGIN
    PRINT 'Table [Chat].[ChatConnections] already exists.';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ChatConnections_Participant'
      AND [object_id] = OBJECT_ID(N'[Chat].[ChatConnections]'))
    CREATE INDEX [IX_ChatConnections_Participant]
        ON [Chat].[ChatConnections] ([ParticipantType], [ParticipantId])
        INCLUDE ([ActiveConversationId]);
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_ChatConnections_LastHeartbeat'
      AND [object_id] = OBJECT_ID(N'[Chat].[ChatConnections]'))
    CREATE INDEX [IX_ChatConnections_LastHeartbeat]
        ON [Chat].[ChatConnections] ([LastHeartbeatAtUtc]);
GO


-- 2.25.4 Block.BlockedParticipants ---------------------------------------------
-- Who has blocked whom. Began as a chat-only remedy and is now product-wide,
-- which is why it sits in [Block] rather than [Chat] (see the transfer migration
-- in section 1). From ONE row a block stops, in BOTH directions: messages
-- ([Chat].[GetOrCreateConversation] / [Chat].[ReserveMessageSequence]), new
-- bookings ([Booking].[CreateBooking] / [Booking].[CreateNightStayBooking]),
-- sight of each other's events, and the provider's appearance in browse + all
-- five searches.
--
-- Deliberately does NOT hide existing chat history: that is part of both parties'
-- record, and removing it would also remove what a blocked user might need in
-- order to report. Nor does it hide a provider behind a booking the parent
-- already has, nor take away an event ticket already paid for.
--
-- Placing a block CANCELS the pair's unfinished jobs, except one already
-- IN_PROGRESS -- the pet is in someone's care and [Booking].[UpdateBookingStatus]
-- refuses that cancel (THROW 51149), a guard this feature does not bypass.
--
-- Reporting does NOT write here. A support ticket is a report to support, not a
-- sanction the reporter applies themselves, so the pair stay able to message, book
-- and find each other while the case is looked at -- and every row in this table is
-- one a user placed and may lift whenever they like. Unblocking restores contact
-- but does not resurrect the cancelled bookings.
IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE [name] = N'BlockedParticipants' AND [schema_id] = SCHEMA_ID(N'Block'))
BEGIN
    CREATE TABLE [Block].[BlockedParticipants]
    (
        [BlockId] UNIQUEIDENTIFIER NOT NULL
            CONSTRAINT [DF_BlockedParticipants_BlockId] DEFAULT NEWSEQUENTIALID(),
        [BlockerType] NVARCHAR(16) NOT NULL,
        [BlockerId] UNIQUEIDENTIFIER NOT NULL,
        [BlockedType] NVARCHAR(16) NOT NULL,
        [BlockedId] UNIQUEIDENTIFIER NOT NULL,
        -- Free text the blocker may supply. Never shown to the blocked party.
        [Reason] NVARCHAR(500) NULL,
        [CreatedAtUtc] DATETIME2(7) NOT NULL
            CONSTRAINT [DF_BlockedParticipants_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
        CONSTRAINT [PK_BlockedParticipants] PRIMARY KEY CLUSTERED ([BlockId] ASC),
        CONSTRAINT [UQ_BlockedParticipants_Pair]
            UNIQUE ([BlockerType], [BlockerId], [BlockedType], [BlockedId]),
        CONSTRAINT [CK_BlockedParticipants_BlockerType]
            CHECK ([BlockerType] IN (N'Provider', N'PetParent')),
        CONSTRAINT [CK_BlockedParticipants_BlockedType]
            CHECK ([BlockedType] IN (N'Provider', N'PetParent')),
        -- A blockable relationship only ever runs provider <-> parent, so a block
        -- within one side is meaningless and would silently never be consulted.
        CONSTRAINT [CK_BlockedParticipants_OppositeSides]
            CHECK ([BlockerType] <> [BlockedType])
    );
    PRINT 'Created table [Block].[BlockedParticipants].';
END
ELSE
BEGIN
    PRINT 'Table [Block].[BlockedParticipants] already exists.';
END
GO

-- Every enforcement point asks "did either of us block the other", so the reverse
-- lookup needs its own index -- the UNIQUE above only serves the blocker-first
-- direction. That mattered when chat was the only caller and matters more now
-- that the booking creates, the event reads and the discovery filter ask it too.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE [name] = N'IX_BlockedParticipants_Blocked'
      AND [object_id] = OBJECT_ID(N'[Block].[BlockedParticipants]'))
    CREATE INDEX [IX_BlockedParticipants_Blocked]
        ON [Block].[BlockedParticipants] ([BlockedType], [BlockedId])
        INCLUDE ([BlockerType], [BlockerId]);
GO


--------------------------------------------------------------------------------
-- 3.x Chat.* stored procedures + Notification.EnqueueInstantNotification
--------------------------------------------------------------------------------

-- Enqueues a notification that the CALLER intends to send itself, right now,
-- instead of leaving it for the 1-minute NotificationDispatchFunction.
--
-- This exists for chat. Every other notification in the product is a side effect
-- of a booking or a ticket, where a minute of latency costs nothing and
-- durability is everything — so those go through [Notification].[EnqueueNotification]
-- and wait for the timer. A chat message is the opposite: the latency IS the
-- feature.
--
-- The row is inserted ALREADY CLAIMED — [Status] = 'Sending' with the lease
-- pushed forward — which is the whole trick:
--   * the timer's claim predicate skips it, so the recipient is not pushed twice;
--   * if the caller dies before reporting the outcome, the lease lapses and the
--     ordinary dispatcher picks it up on its next tick and sends it after all.
-- So the outbox stops being the delivery path here and becomes the RETRY
-- BACKSTOP, with no extra machinery and no second code path to keep in step.
--
-- The caller renders copy with the C# NotificationTemplateCatalog, sends via FCM,
-- and reports through the existing [Notification].[CompleteNotificationDelivery]
-- exactly as the dispatcher does — which is what keeps chat copy and payload
-- shape identical to everything else.
--
-- Returns TWO result sets: the notification id, then the recipient's active FCM
-- tokens (same join [Notification].[ClaimPendingNotifications] uses), so the
-- caller needs no follow-up round trip to find the devices.
--
-- Never THROWs, for the same reason [Notification].[EnqueueNotification] never
-- does: a notification must not be the reason the thing that caused it fails.
CREATE OR ALTER PROCEDURE [Notification].[EnqueueInstantNotification]
    @Audience NVARCHAR(16),
    @RecipientId UNIQUEIDENTIFIER,
    @NotificationType NVARCHAR(64),
    @EntityType NVARCHAR(32) = NULL,
    @EntityId UNIQUEIDENTIFIER = NULL,
    @DataJson NVARCHAR(MAX) = NULL,
    @ImageUrl NVARCHAR(1000) = NULL,
    @DedupeKey NVARCHAR(200) = NULL,
    -- How long the caller has to render, send and report before the timer
    -- considers the row abandoned and takes it over. Generous relative to an FCM
    -- call, because a premature takeover means a duplicate push.
    @LeaseMinutes INT = 5,
    -- Set by sproc-to-sproc callers ([Chat].[CommitMessageAppend]), for the same reason
    -- [Notification].[EnqueueNotification] has it: a nested EXEC's result set
    -- propagates to the client and would corrupt the caller's own reader. Such a
    -- caller reads the id back through @NotificationId OUTPUT instead.
    @SuppressResultSet BIT = 0,
    @NotificationId UNIQUEIDENTIFIER = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    SET @NotificationId = NULL;

    IF @DedupeKey IS NOT NULL
    BEGIN
        SELECT @NotificationId = [NotificationId]
        FROM [Notification].[NotificationOutbox] WITH (UPDLOCK, HOLDLOCK)
        WHERE [DedupeKey] = @DedupeKey;
    END

    IF @NotificationId IS NULL
    BEGIN
        DECLARE @Inserted TABLE ([NotificationId] UNIQUEIDENTIFIER);

        BEGIN TRY
            INSERT INTO [Notification].[NotificationOutbox]
            (
                [Audience],
                [RecipientId],
                [NotificationType],
                [EntityType],
                [EntityId],
                [DataJson],
                [ImageUrl],
                [DedupeKey],
                [Status],
                [AttemptCount],
                [NextAttemptAtUtc],
                [CreatedAtUtc],
                [UpdatedAtUtc]
            )
            OUTPUT inserted.[NotificationId] INTO @Inserted
            VALUES
            (
                @Audience,
                @RecipientId,
                @NotificationType,
                @EntityType,
                @EntityId,
                @DataJson,
                @ImageUrl,
                @DedupeKey,
                -- Claimed on insert. AttemptCount starts at 1 because this row's
                -- first attempt is the one the caller is about to make.
                N'Sending',
                1,
                DATEADD(MINUTE, @LeaseMinutes, @Now),
                @Now,
                @Now
            );

            SELECT @NotificationId = [NotificationId] FROM @Inserted;
        END TRY
        BEGIN CATCH
            -- 2601/2627 = the dedupe race the lock above almost always prevents.
            IF ERROR_NUMBER() NOT IN (2601, 2627) OR @DedupeKey IS NULL
            BEGIN
                THROW;
            END

            SELECT @NotificationId = [NotificationId]
            FROM [Notification].[NotificationOutbox]
            WHERE [DedupeKey] = @DedupeKey;
        END CATCH
    END

    IF @SuppressResultSet = 1
    BEGIN
        RETURN;
    END

    SELECT @NotificationId AS [NotificationId];

    IF @Audience = N'Provider'
    BEGIN
        SELECT [FcmToken], [DevicePlatform]
        FROM [Provider].[ProviderDeviceTokens]
        WHERE [ProviderId] = @RecipientId
          AND [IsActive] = 1;
    END
    ELSE
    BEGIN
        SELECT [FcmToken], [DevicePlatform]
        FROM [Parent].[ParentDeviceTokens]
        WHERE [PetParentId] = @RecipientId
          AND [IsActive] = 1;
    END
END;
GO
PRINT 'Created/updated [Notification].[EnqueueInstantNotification].';
GO

-- Opens the one thread between a provider and a pet parent, creating it if this
-- is the first contact.
--
-- Chat is OPEN — any parent may message any provider with no booking between them
-- — so this procedure is where "may these two talk at all" is decided, and the
-- only gates are: both accounts still exist, and neither has blocked the other.
--
-- An INACTIVE provider is deliberately still reachable. Inactive means "not
-- taking bookings": they are hidden from discovery and [Booking].[CreateBooking]
-- refuses them, but their profile is still viewable by deep link and answering a
-- question before switching back on is exactly what chat is for. Only IsDeleted —
-- which is permanent — closes the door.
--
-- Race safety comes from the UNIQUE ([ProviderId], [PetParentId]) on
-- [Chat].[Conversations]: the lookup below takes UPDLOCK + HOLDLOCK over that key
-- range, so two devices opening the same thread at once serialise and the second
-- finds the first's row instead of hitting a UNIQUE violation. Same shape as
-- [Review].[UpsertBookingReview].
--
-- Returns TWO result sets: the conversation row, then both participant rows.
--
-- THROWs: 51320 provider not found, 51321 provider account deleted,
-- 51322 pet parent not found or deleted, 51323 blocked in one direction or the
-- other, 51324 the actor is not one of the two named parties.
CREATE OR ALTER PROCEDURE [Chat].[GetOrCreateConversation]
    @ProviderId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @ActorType NVARCHAR(16),        -- 'Provider' | 'PetParent'
    @ActorId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Defensive. The API derives the actor from the JWT and never from the body,
    -- so a mismatch here means a direct caller, not something a client can reach.
    IF @ActorType NOT IN (N'Provider', N'PetParent')
        OR (@ActorType = N'Provider' AND @ActorId <> @ProviderId)
        OR (@ActorType = N'PetParent' AND @ActorId <> @PetParentId)
    BEGIN
        THROW 51324, 'The acting participant is not a party to this conversation.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ConversationId UNIQUEIDENTIFIER;
    DECLARE @ProviderIsDeleted BIT;
    DECLARE @ParentIsDeleted BIT;

    BEGIN TRANSACTION;

    SELECT @ProviderIsDeleted = [IsDeleted]
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;

    IF @ProviderIsDeleted IS NULL
    BEGIN
        THROW 51320, 'Provider was not found.', 1;
    END

    IF @ProviderIsDeleted = 1
    BEGIN
        THROW 51321, 'This provider account has been deleted.', 1;
    END

    SELECT @ParentIsDeleted = [IsDeleted]
    FROM [Parent].[PetParents]
    WHERE [PetParentId] = @PetParentId;

    -- Deleted and absent are one case on this side. A deleted parent has had
    -- their Firebase identity severed, so they could never read the thread; the
    -- provider gains nothing from being told the difference.
    IF @ParentIsDeleted IS NULL OR @ParentIsDeleted = 1
    BEGIN
        THROW 51322, 'Pet parent was not found.', 1;
    END

    -- Either direction blocks. The blocked party is not told which way round it
    -- is — that would confirm the other person acted, which is the thing a block
    -- is meant to end.
    IF EXISTS (
        SELECT 1
        FROM [Block].[BlockedParticipants]
        WHERE ([BlockerType] = N'Provider' AND [BlockerId] = @ProviderId
               AND [BlockedType] = N'PetParent' AND [BlockedId] = @PetParentId)
           OR ([BlockerType] = N'PetParent' AND [BlockerId] = @PetParentId
               AND [BlockedType] = N'Provider' AND [BlockedId] = @ProviderId))
    BEGIN
        THROW 51323, 'This conversation is not available.', 1;
    END

    -- UPDLOCK + HOLDLOCK over the unique pair: when no row exists this takes a
    -- range lock, so a concurrent open of the same thread waits here rather than
    -- racing to INSERT.
    SELECT @ConversationId = [ConversationId]
    FROM [Chat].[Conversations] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId
      AND [PetParentId] = @PetParentId;

    IF @ConversationId IS NULL
    BEGIN
        DECLARE @Inserted TABLE ([ConversationId] UNIQUEIDENTIFIER);

        INSERT INTO [Chat].[Conversations]
            ([ProviderId], [PetParentId], [CreatedAtUtc], [UpdatedAtUtc])
        OUTPUT inserted.[ConversationId] INTO @Inserted
        VALUES
            (@ProviderId, @PetParentId, @Now, @Now);

        SELECT @ConversationId = [ConversationId] FROM @Inserted;

        -- Both sides, always, in the same statement — a conversation with one
        -- participant row is not a state any read path knows how to handle.
        INSERT INTO [Chat].[ConversationParticipants]
            ([ConversationId], [ParticipantType], [ParticipantId], [CreatedAtUtc], [UpdatedAtUtc])
        VALUES
            (@ConversationId, N'Provider', @ProviderId, @Now, @Now),
            (@ConversationId, N'PetParent', @PetParentId, @Now, @Now);
    END
    ELSE
    BEGIN
        -- Re-opening a thread the caller had DELETED puts it back on their inbox.
        -- They have deliberately navigated into it, so leaving it hidden would
        -- mean opening a conversation you then cannot find.
        --
        -- [ClearedUpToSequence] is deliberately NOT reset: they deleted that
        -- history and re-entering the room is not a request to have it back. The
        -- thread resumes empty and fills from here — which is exactly what every
        -- messaging app does.
        --
        -- Conditional, so the ordinary open (the overwhelmingly common case)
        -- writes nothing.
        UPDATE [Chat].[ConversationParticipants]
        SET [DeletedAtUtc] = NULL,
            [UpdatedAtUtc] = @Now
        WHERE [ConversationId] = @ConversationId
          AND [ParticipantType] = @ActorType
          AND [ParticipantId] = @ActorId
          AND [DeletedAtUtc] IS NOT NULL;
    END

    SELECT [ConversationId],
           [ProviderId],
           [PetParentId],
           [LastSequence],
           [LastMessageAtUtc],
           [LastMessagePreview],
           [LastMessageSenderType],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Chat].[Conversations]
    WHERE [ConversationId] = @ConversationId;

    SELECT [ConversationParticipantId],
           [ConversationId],
           [ParticipantType],
           [ParticipantId],
           [LastReadSequence],
           [UnreadCount],
           [IsMuted]
    FROM [Chat].[ConversationParticipants]
    WHERE [ConversationId] = @ConversationId
    ORDER BY [ParticipantType] ASC;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Chat].[GetOrCreateConversation].';
GO

-- [Chat].[AppendMessage] is RETIRED. It did the whole send in one call, which
-- meant its transaction committed BEFORE the message body was written to Cosmos:
-- a failure after it left the thread advanced, the recipient pushed, and the
-- sender told the send had failed. It is replaced by the reserve/commit pair
-- below. Dropped rather than left in place so a stray caller cannot keep
-- producing that outcome.
IF OBJECT_ID(N'[Chat].[AppendMessage]', N'P') IS NOT NULL
BEGIN
    DROP PROCEDURE [Chat].[AppendMessage];
    PRINT 'Dropped retired [Chat].[AppendMessage].';
END
GO


-- Phase 1 of sending a message: authorise the sender and assign the sequence.
--
-- It deliberately does NOTHING a client could observe. The inbox preview, the
-- recipient's unread count and their push all belong to phase 2
-- ([Chat].[CommitMessageAppend]), which runs only after the body is durable in
-- Cosmos. That split is the whole point: a send that fails at the Cosmos write
-- leaves no trace except a spent sequence number, instead of the old behaviour
-- where the thread advanced, the recipient was pushed, and the sender was told
-- the send had failed.
--
-- IDEMPOTENT ON @MessageId. The client mints it, and ([ConversationId],
-- [MessageId]) is the primary key of [Chat].[ConversationMessages] — so a retry,
-- whether it arrives over the hub or over REST, finds the existing reservation
-- and is handed the ORIGINAL sequence back with [IsReplay] = 1. A duplicate can
-- therefore never take a second sequence, and never produce a second message.
-- This does not depend on Cosmos, which matters because the case that needs
-- protecting most is the one where the Cosmos write is what failed.
--
-- The transaction commits before returning, so NO lock is held across the Cosmos
-- write that follows. Holding the conversation row — the row every send in the
-- thread serialises on — across an external network call would be the worst
-- possible place to put one.
--
-- Returns ONE result set: the sequence, the timestamp to stamp on the body, and
-- whether this is a replay of an already-reserved or already-committed message.
--
-- THROWs: 51323 blocked, 51325 conversation not found, 51326 sender is not a
-- party to it.
CREATE OR ALTER PROCEDURE [Chat].[ReserveMessageSequence]
    @ConversationId UNIQUEIDENTIFIER,
    @SenderType NVARCHAR(16),           -- 'Provider' | 'PetParent'
    @SenderId UNIQUEIDENTIFIER,
    -- The client-generated message id, which is also the Cosmos document id.
    @MessageId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @SenderType NOT IN (N'Provider', N'PetParent')
    BEGIN
        THROW 51326, 'You are not a party to this conversation.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @Sequence BIGINT = NULL;
    DECLARE @ReservedAtUtc DATETIME2(7) = NULL;
    DECLARE @CommittedAtUtc DATETIME2(7) = NULL;
    DECLARE @IsReplay BIT = 0;

    BEGIN TRANSACTION;

    -- UPDLOCK on the conversation row is what serialises concurrent sends and
    -- makes the sequence strictly increasing: two messages at once queue here
    -- rather than both reading the same LastSequence.
    SELECT @ProviderId = [ProviderId],
           @PetParentId = [PetParentId]
    FROM [Chat].[Conversations] WITH (UPDLOCK, ROWLOCK)
    WHERE [ConversationId] = @ConversationId;

    IF @ProviderId IS NULL
    BEGIN
        THROW 51325, 'Conversation was not found.', 1;
    END

    IF (@SenderType = N'Provider' AND @SenderId <> @ProviderId)
        OR (@SenderType = N'PetParent' AND @SenderId <> @PetParentId)
    BEGIN
        THROW 51326, 'You are not a party to this conversation.', 1;
    END

    -- Re-checked on every message, not just at conversation creation: a block
    -- raised mid-thread has to take effect immediately, and the conversation row
    -- already exists by then.
    IF EXISTS (
        SELECT 1
        FROM [Block].[BlockedParticipants]
        WHERE ([BlockerType] = N'Provider' AND [BlockerId] = @ProviderId
               AND [BlockedType] = N'PetParent' AND [BlockedId] = @PetParentId)
           OR ([BlockerType] = N'PetParent' AND [BlockerId] = @PetParentId
               AND [BlockedType] = N'Provider' AND [BlockedId] = @ProviderId))
    BEGIN
        THROW 51323, 'This conversation is not available.', 1;
    END

    -- The idempotency check. HOLDLOCK as well as UPDLOCK so the absence of a row
    -- is held too — without it two concurrent sends of the same @MessageId could
    -- both find nothing and both try to insert, and the loser would fail on the
    -- primary key instead of being told it is a replay.
    SELECT @Sequence = [Sequence],
           @ReservedAtUtc = [ReservedAtUtc],
           @CommittedAtUtc = [CommittedAtUtc],
           @IsReplay = 1
    FROM [Chat].[ConversationMessages] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ConversationId] = @ConversationId
      AND [MessageId] = @MessageId;

    IF @IsReplay = 0
    BEGIN
        SELECT @Sequence = [LastSequence] + 1
        FROM [Chat].[Conversations]
        WHERE [ConversationId] = @ConversationId;

        SET @ReservedAtUtc = @Now;

        -- LastSequence moves here, in phase 1, and is never rolled back if the
        -- body fails to land. It is a high-water mark, not a count — see the note
        -- on the column for why a gap costs nothing.
        UPDATE [Chat].[Conversations]
        SET [LastSequence] = @Sequence,
            [UpdatedAtUtc] = @Now
        WHERE [ConversationId] = @ConversationId;

        INSERT INTO [Chat].[ConversationMessages]
            ([ConversationId], [MessageId], [Sequence], [SenderType], [SenderId], [ReservedAtUtc])
        VALUES
            (@ConversationId, @MessageId, @Sequence, @SenderType, @SenderId, @ReservedAtUtc);
    END

    COMMIT TRANSACTION;

    -- Every flag is CAST to BIT explicitly. COALESCE(bit, 0) returns INT, because
    -- INT outranks BIT in data type precedence — which is exactly how the old
    -- [Chat].[AppendMessage] came to hand its caller an INT where a BIT was
    -- expected and crash every single send after the transaction had committed.
    SELECT @ConversationId AS [ConversationId],
           @MessageId AS [MessageId],
           @Sequence AS [Sequence],
           @ReservedAtUtc AS [CreatedAtUtc],
           CAST(@IsReplay AS BIT) AS [IsReplay],
           CAST(CASE WHEN @CommittedAtUtc IS NULL THEN 0 ELSE 1 END AS BIT) AS [IsCommitted];
END;
GO
PRINT 'Created/updated [Chat].[ReserveMessageSequence].';
GO

-- Phase 2 of sending a message, run only once the body is durable in Cosmos.
--
-- Everything a client can observe happens here and nowhere else: the inbox cache,
-- both sides' read state, and the recipient's push. That is what makes a send
-- atomic in the only sense that matters to a user — either the message exists and
-- is delivered, or nothing about the thread changed. Phase 1
-- ([Chat].[ReserveMessageSequence]) deliberately leaves no observable trace, so
-- there is nothing to undo when the body fails to land.
--
-- WHY THE PUSH DECISION LIVES HERE. It needs the recipient's presence, their mute
-- flag, and their device tokens — three reads against tables this transaction is
-- already in. Deciding it in C# would mean extra round trips and a window in
-- which presence could change between the decision and the write. Same reasoning
-- that put the notification enqueue inside the booking transition sprocs and the
-- pending-jobs check inside [Parent].[DeletePetParent].
--
-- IDEMPOTENT. A second commit of the same message is a no-op: it moves no counter
-- and queues no second push. That is what lets the caller retry safely after a
-- failure between the Cosmos write and this call — and it is load-bearing, since
-- such a retry is the only thing that repairs a message whose body landed but
-- whose delivery did not.
--
-- OUT-OF-ORDER COMMITS ARE EXPECTED. Two sends race, the later one's body lands
-- first, and it commits first. The unread count must still count both, but the
-- inbox preview must show the NEWER message — so the counter update is
-- unconditional while the cache update is guarded on this still being the highest
-- committed sequence.
--
-- Returns TWO result sets:
--   1. the recipient, the message's sequence/timestamp, and the notification id if
--      one was queued (NULL if not)
--   2. the recipient's active FCM tokens (empty unless a notification was queued)
--
-- THROWs: 51325 conversation not found, 51328 no reservation for this message.
CREATE OR ALTER PROCEDURE [Chat].[CommitMessageAppend]
    @ConversationId UNIQUEIDENTIFIER,
    -- The message reserved by phase 1. The sender is NOT a parameter: it is read
    -- back from the reservation, so the two phases cannot disagree about who sent
    -- this.
    @MessageId UNIQUEIDENTIFIER,
    -- What the inbox card shows. The caller truncates and, for an attachment,
    -- substitutes a label ("Photo") rather than sending a blob URL.
    @Preview NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;

    DECLARE @Sequence BIGINT = NULL;
    DECLARE @SenderType NVARCHAR(16) = NULL;
    DECLARE @ReservedAtUtc DATETIME2(7) = NULL;
    DECLARE @AlreadyCommitted BIT = 0;

    DECLARE @RecipientType NVARCHAR(16);
    DECLARE @RecipientId UNIQUEIDENTIFIER;
    DECLARE @RecipientUnread INT;
    DECLARE @RecipientIsMuted BIT;
    DECLARE @RecipientIsViewing BIT = 0;
    DECLARE @NotificationId UNIQUEIDENTIFIER = NULL;
    -- Returned to the caller so it can render the copy itself. The C#
    -- NotificationTemplateCatalog is the only place wording lives, and the
    -- renderer needs these parameters; without handing them back, the chat host
    -- would have to re-read the outbox row it just wrote.
    DECLARE @DataJson NVARCHAR(MAX) = NULL;

    BEGIN TRANSACTION;

    SELECT @ProviderId = [ProviderId],
           @PetParentId = [PetParentId]
    FROM [Chat].[Conversations] WITH (UPDLOCK, ROWLOCK)
    WHERE [ConversationId] = @ConversationId;

    IF @ProviderId IS NULL
    BEGIN
        THROW 51325, 'Conversation was not found.', 1;
    END

    SELECT @Sequence = [Sequence],
           @SenderType = [SenderType],
           @ReservedAtUtc = [ReservedAtUtc],
           @AlreadyCommitted = CASE WHEN [CommittedAtUtc] IS NULL THEN 0 ELSE 1 END
    FROM [Chat].[ConversationMessages] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ConversationId] = @ConversationId
      AND [MessageId] = @MessageId;

    IF @Sequence IS NULL
    BEGIN
        -- Phase 2 without phase 1. Not reachable through the service, which always
        -- reserves first; surfacing it beats silently inventing a sequence.
        THROW 51328, 'No reservation exists for this message.', 1;
    END

    SET @RecipientType = CASE WHEN @SenderType = N'Provider'
                              THEN N'PetParent' ELSE N'Provider' END;
    SET @RecipientId = CASE WHEN @RecipientType = N'Provider'
                            THEN @ProviderId ELSE @PetParentId END;

    IF @AlreadyCommitted = 0
    BEGIN
        UPDATE [Chat].[ConversationMessages]
        SET [CommittedAtUtc] = @Now
        WHERE [ConversationId] = @ConversationId
          AND [MessageId] = @MessageId;

        -- The inbox cache, but only if nothing newer has already committed — see
        -- the out-of-order note above. Seeks [UQ_ConversationMessages_Sequence].
        IF NOT EXISTS (
            SELECT 1
            FROM [Chat].[ConversationMessages]
            WHERE [ConversationId] = @ConversationId
              AND [CommittedAtUtc] IS NOT NULL
              AND [Sequence] > @Sequence)
        BEGIN
            UPDATE [Chat].[Conversations]
            SET [LastMessageAtUtc] = @ReservedAtUtc,
                [LastMessagePreview] = @Preview,
                [LastMessageSenderType] = @SenderType,
                [UpdatedAtUtc] = @Now
            WHERE [ConversationId] = @ConversationId;
        END

        -- Both sides in one statement, which is the reason participants are rows
        -- rather than column pairs on the conversation. The sender's read pointer
        -- advances because you have obviously read what you just sent — but only
        -- forwards, so an out-of-order commit cannot drag it back.
        UPDATE [Chat].[ConversationParticipants]
        SET [LastReadSequence] = CASE WHEN [ParticipantType] = @SenderType
                                           AND @Sequence > [LastReadSequence]
                                      THEN @Sequence ELSE [LastReadSequence] END,
            [UnreadCount] = CASE WHEN [ParticipantType] = @SenderType
                                 THEN 0 ELSE [UnreadCount] + 1 END,
            [UpdatedAtUtc] = @Now
        WHERE [ConversationId] = @ConversationId;
    END

    SELECT @RecipientUnread = [UnreadCount],
           @RecipientIsMuted = [IsMuted]
    FROM [Chat].[ConversationParticipants]
    WHERE [ConversationId] = @ConversationId
      AND [ParticipantType] = @RecipientType;

    -- Presence, at THREAD level rather than merely "are they connected". Someone
    -- connected but on another screen still deserves a push — they are not
    -- looking at this. Only an open thread suppresses it.
    IF EXISTS (
        SELECT 1
        FROM [Chat].[ChatConnections]
        WHERE [ParticipantType] = @RecipientType
          AND [ParticipantId] = @RecipientId
          AND [ActiveConversationId] = @ConversationId)
    BEGIN
        SET @RecipientIsViewing = 1;
    END

    -- @AlreadyCommitted guards the enqueue as well as the counters: a replay must
    -- not buzz the recipient a second time for one message.
    IF @AlreadyCommitted = 0 AND @RecipientIsViewing = 0 AND COALESCE(@RecipientIsMuted, 0) = 0
    BEGIN
        -- Copy is NOT built here. The dispatcher's C# NotificationTemplateCatalog
        -- owns every user-facing string in the product, and chat is not an
        -- exception to that — this only supplies the type and its parameters.
        DECLARE @SenderName NVARCHAR(200) =
            CASE WHEN @SenderType = N'Provider'
                 THEN (SELECT LTRIM(RTRIM(COALESCE([FirstName], N'') + N' ' + COALESCE([LastName], N'')))
                       FROM [Provider].[Providers] WHERE [ProviderId] = @ProviderId)
                 ELSE (SELECT LTRIM(RTRIM(COALESCE([FirstName], N'') + N' ' + COALESCE([LastName], N'')))
                       FROM [Parent].[PetParents] WHERE [PetParentId] = @PetParentId)
            END;

        -- Joined live rather than denormalised onto the message, so a deleted
        -- account reads "Deleted Provider" / "Deleted User" — the same invariant
        -- the review and booking reads hold. NULLIF sends nothing at all when the
        -- name is blank, letting the renderer's "Someone" fallback take over.
        SET @DataJson =
        (
            SELECT
                -- --- the canonical id block ---
                N'MESSAGING'                                 AS [category],
                CAST(@ConversationId AS NVARCHAR(36))        AS [conversationId],
                CAST(@PetParentId AS NVARCHAR(36))           AS [parentId],
                CAST(@ProviderId AS NVARCHAR(36))            AS [providerId],
                -- --- template parameters ---
                NULLIF(@SenderName, N'')                     AS [senderName]
            FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
        );

        DECLARE @DedupeKey NVARCHAR(200) =
            N'MESSAGE_RECEIVED:' + CAST(@MessageId AS NVARCHAR(36));

        EXEC [Notification].[EnqueueInstantNotification]
            @Audience = @RecipientType,
            @RecipientId = @RecipientId,
            @NotificationType = N'MESSAGE_RECEIVED',
            @EntityType = N'Conversation',
            @EntityId = @ConversationId,
            @DataJson = @DataJson,
            -- Keyed on the message, not the conversation: every message is a
            -- distinct event, but a retried send of the SAME message must not
            -- buzz twice. Prefixed and cast explicitly, matching the DedupeKey
            -- shape [Notification].[EnqueueBookingNotification] builds.
            @DedupeKey = @DedupeKey,
            @SuppressResultSet = 1,
            @NotificationId = @NotificationId OUTPUT;
    END

    -- Result set 1 — who to deliver to, and whether a push was queued.
    --
    -- EVERY BIT COLUMN IS CAST EXPLICITLY. COALESCE(bit, 0) returns INT, because
    -- INT outranks BIT in data type precedence, and the C# reader calls
    -- GetBoolean on this ordinal. The predecessor of this procedure got that
    -- wrong on [RecipientIsMuted] and threw InvalidCastException on every send,
    -- AFTER its transaction had committed — the message was delivered and the
    -- sender was told it had failed. Do not drop the CASTs.
    SELECT @RecipientType AS [RecipientType],
           @RecipientId AS [RecipientId],
           COALESCE(@RecipientUnread, 0) AS [RecipientUnreadCount],
           CAST(@RecipientIsViewing AS BIT) AS [RecipientIsViewing],
           CAST(COALESCE(@RecipientIsMuted, 0) AS BIT) AS [RecipientIsMuted],
           @NotificationId AS [NotificationId],
           @DataJson AS [DataJson],
           @Sequence AS [Sequence],
           @ReservedAtUtc AS [CreatedAtUtc];

    -- Result set 2 — the devices to push to. Empty when no notification was
    -- queued, so the caller can branch on row count alone.
    IF @NotificationId IS NULL
    BEGIN
        SELECT CAST(NULL AS NVARCHAR(2048)) AS [FcmToken],
               CAST(NULL AS NVARCHAR(32)) AS [DevicePlatform]
        WHERE 1 = 0;
    END
    ELSE IF @RecipientType = N'Provider'
    BEGIN
        SELECT [FcmToken], [DevicePlatform]
        FROM [Provider].[ProviderDeviceTokens]
        WHERE [ProviderId] = @RecipientId
          AND [IsActive] = 1;
    END
    ELSE
    BEGIN
        SELECT [FcmToken], [DevicePlatform]
        FROM [Parent].[ParentDeviceTokens]
        WHERE [PetParentId] = @RecipientId
          AND [IsActive] = 1;
    END

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Chat].[CommitMessageAppend].';
GO

-- Undoes phase 1 when the Cosmos write between the two phases fails.
--
-- Phase 1 deliberately changes nothing observable, so this is not a compensating
-- transaction in the usual sense — there is no preview to restore, no unread
-- count to decrement and no push to recall. All it does is free the
-- ([ConversationId], [MessageId]) key so a retry gets a clean reservation rather
-- than inheriting a sequence from a send that never happened.
--
-- BEST EFFORT, and the caller treats a failure here as a log line. If the row is
-- left behind, a retry of the same @MessageId simply finds it uncommitted, reuses
-- its sequence and completes the send — which is the correct outcome anyway. The
-- only cost of never calling this is an orphaned row and a gap in the sequence,
-- and gaps are already free (see [Chat].[Conversations].[LastSequence]).
--
-- [LastSequence] is NOT rewound. Rewinding it would race with any send that
-- reserved after this one and hand two messages the same number.
--
-- A COMMITTED reservation is never deleted: that row describes a real message
-- whose body is in Cosmos, and removing it would strand the body and let a
-- replay of the same id take a second sequence.
--
-- Never THROWs. An unknown message id is simply nothing to release.
CREATE OR ALTER PROCEDURE [Chat].[ReleaseMessageReservation]
    @ConversationId UNIQUEIDENTIFIER,
    @MessageId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DELETE FROM [Chat].[ConversationMessages]
    WHERE [ConversationId] = @ConversationId
      AND [MessageId] = @MessageId
      AND [CommittedAtUtc] IS NULL;

    SELECT CAST(CASE WHEN @@ROWCOUNT > 0 THEN 1 ELSE 0 END AS BIT) AS [WasReleased];
END;
GO
PRINT 'Created/updated [Chat].[ReleaseMessageReservation].';
GO


-- The authorisation point read: "does this conversation exist, and is the caller
-- one of its two participants?"
--
-- Every path that touches a thread goes through this first — the history read,
-- the hub's JoinConversation (so a client cannot subscribe to a group by
-- guessing its name), mark-read, and attachment upload. It returns NOTHING for a
-- conversation the caller is not part of, deliberately conflating "no such
-- thread" with "not yours" so the caller answers 404 for both and an id cannot be
-- probed for existence. Same posture as [Provider].[DeactivateProviderDeviceToken]
-- and the review-photo reads.
--
-- Returns TWO result sets: the conversation with the caller's own participant
-- state, then the counterparty's name joined LIVE from
-- [Provider].[Providers] / [Parent].[PetParents] — never denormalised, so a
-- deleted account reads "Deleted Provider" / "Deleted User" rather than keeping
-- its real name frozen in the thread.
--
-- Never THROWs; an empty result is the answer.
CREATE OR ALTER PROCEDURE [Chat].[GetConversationForParticipant]
    @ConversationId UNIQUEIDENTIFIER,
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT c.[ConversationId],
           c.[ProviderId],
           c.[PetParentId],
           c.[LastSequence],
           c.[LastMessageAtUtc],
           c.[LastMessagePreview],
           c.[LastMessageSenderType],
           c.[CreatedAtUtc],
           c.[UpdatedAtUtc],
           p.[LastReadSequence],
           p.[UnreadCount],
           CAST(p.[IsMuted] AS BIT) AS [IsMuted],
           -- The caller's "delete chat" watermark, appended LAST so the existing
           -- ordinals stay put. The history read needs it: message bodies are
           -- shared with the counterparty, so hiding a cleared thread's messages
           -- can only be done by filtering below this sequence on the way out.
           -- 0 on a thread that was never cleared, which filters nothing.
           p.[ClearedUpToSequence],
           p.[DeletedAtUtc]
    FROM [Chat].[Conversations] c
    INNER JOIN [Chat].[ConversationParticipants] p
        ON p.[ConversationId] = c.[ConversationId]
       AND p.[ParticipantType] = @ParticipantType
       AND p.[ParticipantId] = @ParticipantId
    WHERE c.[ConversationId] = @ConversationId;

    -- The other side, for the thread header. Scoped by the same participant join
    -- as above so a non-participant gets nothing here either.
    SELECT CASE WHEN @ParticipantType = N'Provider' THEN N'PetParent' ELSE N'Provider' END
               AS [CounterpartyType],
           CASE WHEN @ParticipantType = N'Provider' THEN c.[PetParentId] ELSE c.[ProviderId] END
               AS [CounterpartyId],
           CASE WHEN @ParticipantType = N'Provider'
                THEN LTRIM(RTRIM(COALESCE(pp.[FirstName], N'') + N' ' + COALESCE(pp.[LastName], N'')))
                ELSE LTRIM(RTRIM(COALESCE(pr.[FirstName], N'') + N' ' + COALESCE(pr.[LastName], N'')))
           END AS [CounterpartyName],
           -- Only the pet parent has a photo on their SQL row. A provider's image
           -- lives in their Cosmos offering document, so the caller resolves that
           -- one separately — the same split GetBookingDetail's providerPhotoUrl
           -- has to live with.
           CASE WHEN @ParticipantType = N'Provider' THEN pp.[ProfilePhotoUrl] END
               AS [CounterpartyPhotoUrl],
           CASE WHEN @ParticipantType = N'Provider' THEN pp.[IsDeleted] ELSE pr.[IsDeleted] END
               AS [CounterpartyIsDeleted],
           -- The provider's Cosmos partition key, so the caller can point-read the
           -- offering document their image lives in. Appended LAST so the existing
           -- ordinals the reader uses stay put. See Chat.ListConversations for the
           -- full note.
           CASE WHEN @ParticipantType = N'PetParent' THEN reg.[ServiceCategory] END
               AS [CounterpartyServiceCategory],
           -- A block closes the thread; the flag lets the app disable the
           -- composer rather than let a send fail. TRUE in EITHER direction.
           -- Appended LAST, like the category above, so the reader's existing
           -- ordinals stay put.
           CAST(CASE WHEN EXISTS (
                    SELECT 1
                    FROM [Block].[BlockedParticipants] bp
                    WHERE (bp.[BlockerType] = N'Provider' AND bp.[BlockerId] = c.[ProviderId]
                           AND bp.[BlockedType] = N'PetParent' AND bp.[BlockedId] = c.[PetParentId])
                       OR (bp.[BlockerType] = N'PetParent' AND bp.[BlockerId] = c.[PetParentId]
                           AND bp.[BlockedType] = N'Provider' AND bp.[BlockedId] = c.[ProviderId])
                ) THEN 1 ELSE 0 END AS BIT) AS [IsBlocked],
           -- Only the caller's OWN block offers an Unblock button; one placed
           -- against them is never named. See [Block].[ListMyBlockedCounterparties].
           CAST(CASE WHEN EXISTS (
                    SELECT 1
                    FROM [Block].[BlockedParticipants] bp
                    WHERE bp.[BlockerType] = @ParticipantType
                      AND bp.[BlockerId] = @ParticipantId
                      AND bp.[BlockedId] = CASE WHEN @ParticipantType = N'Provider'
                                                THEN c.[PetParentId] ELSE c.[ProviderId] END
                ) THEN 1 ELSE 0 END AS BIT) AS [BlockedByMe]
    FROM [Chat].[Conversations] c
    INNER JOIN [Chat].[ConversationParticipants] p
        ON p.[ConversationId] = c.[ConversationId]
       AND p.[ParticipantType] = @ParticipantType
       AND p.[ParticipantId] = @ParticipantId
    LEFT JOIN [Provider].[Providers] pr ON pr.[ProviderId] = c.[ProviderId]
    LEFT JOIN [Parent].[PetParents] pp ON pp.[PetParentId] = c.[PetParentId]
    LEFT JOIN [Provider].[ProviderServiceRegistrations] reg ON reg.[ProviderId] = c.[ProviderId]
    WHERE c.[ConversationId] = @ConversationId;
END;
GO
PRINT 'Created/updated [Chat].[GetConversationForParticipant].';
GO

-- The caller's chat inbox: their threads, most recently active first, with the
-- counterparty's name and their own unread count on each card. Optionally
-- filtered by @Search — the inbox search bar.
--
-- One indexed read, not a fan-out. That is what the denormalised last-message
-- columns on [Chat].[Conversations] are for — without them this screen would need
-- a Cosmos query per thread just to show a preview line. Search is applied to
-- that SAME read rather than to a second store, which is why it costs nothing
-- extra and pages identically.
--
-- Threads that exist but have never been used sort last (LastMessageAtUtc NULL),
-- rather than being hidden: a conversation is created the moment someone opens
-- it, and a parent who opened a provider's thread and hesitated should still find
-- it where they left it.
--
-- Names are joined LIVE. A review, a booking and a chat all read a
-- counterparty's name this way for the same reason: an anonymised account must
-- read "Deleted Provider" / "Deleted User" everywhere, and a denormalised copy
-- would keep the real name. Searching therefore matches whatever the caller can
-- actually SEE — including "Deleted User".
--
-- Returns ONE result set. Paged by the caller; @Take is capped there.
CREATE OR ALTER PROCEDURE [Chat].[ListConversations]
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER,
    @Skip INT = 0,
    @Take INT = 20,
    -- Free text from the inbox search bar. NULL or blank returns the unfiltered
    -- inbox, so the same procedure serves both and there is no second code path
    -- for the two to drift apart in.
    @Search NVARCHAR(200) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF @Skip IS NULL OR @Skip < 0 SET @Skip = 0;
    IF @Take IS NULL OR @Take < 1 SET @Take = 20;

    -- A "contains" match, with the LIKE metacharacters in the user's own text
    -- neutralised — otherwise typing '%' would match every thread and '_' would
    -- match any character, which is a surprising search box at best. '[' is
    -- escaped FIRST: the other two replacements introduce brackets of their own,
    -- so doing it later would re-escape them. Same treatment the event-title
    -- search gives its term.
    --
    -- Case-insensitivity comes from the database collation, as it does for the
    -- event search — no LOWER() on the column, which would make the predicate
    -- non-sargable for no benefit here.
    DECLARE @Pattern NVARCHAR(220) = NULL;

    IF @Search IS NOT NULL AND LTRIM(RTRIM(@Search)) <> N''
    BEGIN
        SET @Pattern = N'%' + REPLACE(REPLACE(REPLACE(
            LTRIM(RTRIM(@Search)), N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]') + N'%';
    END

    -- The counterparty name is a CASE expression over a LEFT JOIN, and a computed
    -- alias cannot be referenced from WHERE — hence the derived table, which lets
    -- the search predicate read the name exactly as the card will show it rather
    -- than repeating the expression.
    SELECT t.[ConversationId],
           t.[ProviderId],
           t.[PetParentId],
           t.[LastSequence],
           t.[LastMessageAtUtc],
           t.[LastMessagePreview],
           t.[LastMessageSenderType],
           t.[CreatedAtUtc],
           t.[LastReadSequence],
           t.[UnreadCount],
           t.[IsMuted],
           t.[CounterpartyType],
           t.[CounterpartyId],
           t.[CounterpartyName],
           t.[CounterpartyPhotoUrl],
           t.[CounterpartyServiceCategory],
           t.[IsBlocked],
           t.[BlockedByMe]
    FROM (
        SELECT c.[ConversationId],
               c.[ProviderId],
               c.[PetParentId],
               c.[LastSequence],
               c.[LastMessageAtUtc],
               c.[LastMessagePreview],
               c.[LastMessageSenderType],
               c.[CreatedAtUtc],
               p.[LastReadSequence],
               p.[UnreadCount],
               CAST(p.[IsMuted] AS BIT) AS [IsMuted],
               CASE WHEN @ParticipantType = N'Provider' THEN N'PetParent' ELSE N'Provider' END
                   AS [CounterpartyType],
               CASE WHEN @ParticipantType = N'Provider' THEN c.[PetParentId] ELSE c.[ProviderId] END
                   AS [CounterpartyId],
               CASE WHEN @ParticipantType = N'Provider'
                    THEN LTRIM(RTRIM(COALESCE(pp.[FirstName], N'') + N' ' + COALESCE(pp.[LastName], N'')))
                    ELSE LTRIM(RTRIM(COALESCE(pr.[FirstName], N'') + N' ' + COALESCE(pr.[LastName], N'')))
               END AS [CounterpartyName],
               -- Parent photos only; a provider's image is in Cosmos. The caller
               -- batch-resolves those for the page it is returning.
               CASE WHEN @ParticipantType = N'Provider' THEN pp.[ProfilePhotoUrl] END
                   AS [CounterpartyPhotoUrl],
               -- ...and this is what lets it. A provider's offering document is
               -- partitioned by ServiceCategory, so a point read needs the category as
               -- well as the id, and a chat thread — unlike a booking — carries no
               -- service to get it from. Handing it over here keeps the resolution to
               -- one Cosmos point read per provider instead of a SQL round trip first.
               -- NULL when the counterparty is a pet parent (their photo is already
               -- above), and when the provider has registered no service yet, which is
               -- legitimate: chat is open to a provider mid-onboarding.
               CASE WHEN @ParticipantType = N'PetParent' THEN reg.[ServiceCategory] END
                   AS [CounterpartyServiceCategory],
               -- A block closes the thread. The flag is what lets the app disable
               -- the composer up front instead of letting a send fail, and it is
               -- TRUE in EITHER direction, because either direction closes it.
               CAST(CASE WHEN EXISTS (
                        SELECT 1
                        FROM [Block].[BlockedParticipants] bp
                        WHERE (bp.[BlockerType] = N'Provider' AND bp.[BlockerId] = c.[ProviderId]
                               AND bp.[BlockedType] = N'PetParent' AND bp.[BlockedId] = c.[PetParentId])
                           OR (bp.[BlockerType] = N'PetParent' AND bp.[BlockerId] = c.[PetParentId]
                               AND bp.[BlockedType] = N'Provider' AND bp.[BlockedId] = c.[ProviderId])
                    ) THEN 1 ELSE 0 END AS BIT) AS [IsBlocked],
               -- Only the caller's OWN block offers an Unblock button. One placed
               -- against them is never named -- naming it would confirm the other
               -- party acted, which is the thing a block must not do. The pair
               -- reads as: true/true offer Unblock, true/false show a neutral
               -- "not available".
               CAST(CASE WHEN EXISTS (
                        SELECT 1
                        FROM [Block].[BlockedParticipants] bp
                        WHERE bp.[BlockerType] = @ParticipantType
                          AND bp.[BlockerId] = @ParticipantId
                          AND bp.[BlockedId] = CASE WHEN @ParticipantType = N'Provider'
                                                    THEN c.[PetParentId] ELSE c.[ProviderId] END
                    ) THEN 1 ELSE 0 END AS BIT) AS [BlockedByMe]
        FROM [Chat].[ConversationParticipants] p
        INNER JOIN [Chat].[Conversations] c
            ON c.[ConversationId] = p.[ConversationId]
        LEFT JOIN [Provider].[Providers] pr ON pr.[ProviderId] = c.[ProviderId]
        LEFT JOIN [Parent].[PetParents] pp ON pp.[PetParentId] = c.[PetParentId]
        -- UNIQUE on ProviderId, so this cannot fan the page out.
        LEFT JOIN [Provider].[ProviderServiceRegistrations] reg ON reg.[ProviderId] = c.[ProviderId]
        WHERE p.[ParticipantType] = @ParticipantType
          AND p.[ParticipantId] = @ParticipantId
          -- Threads THIS side has cleared ("delete chat"), while nothing has been
          -- said since. The counterparty's copy is unaffected — their row has its
          -- own watermark — and the moment they write again LastSequence passes
          -- the watermark and the thread reappears here, which is what stops a
          -- delete from quietly cutting the caller off. See the column comments
          -- on [Chat].[ConversationParticipants] for why both halves are needed.
          AND (p.[DeletedAtUtc] IS NULL OR c.[LastSequence] > p.[ClearedUpToSequence])
    ) AS t
    WHERE @Pattern IS NULL
       OR t.[CounterpartyName] LIKE @Pattern
       -- The preview is the thread's newest message, already denormalised onto
       -- the conversation for the card — so matching it costs nothing and covers
       -- "whatever we were just talking about". It is NOT full-history search:
       -- bodies live in Cosmos partitioned by conversation, so searching them all
       -- would be a cross-partition scan per keystroke.
       OR t.[LastMessagePreview] LIKE @Pattern
    -- Newest activity first; never-used threads fall to the bottom. The
    -- ConversationId tie-break is what stops OFFSET paging repeating or skipping
    -- a row when two threads share a timestamp — the same reason the review and
    -- earnings lists carry one.
    ORDER BY t.[LastMessageAtUtc] DESC, t.[ConversationId] DESC
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;
GO
PRINT 'Created/updated [Chat].[ListConversations].';
GO

-- Advances the caller's read pointer on one thread and clears their unread count.
--
-- @UpToSequence is what the client claims to have seen. It is clamped to the
-- conversation's own [LastSequence] and never moves backwards, so a stale client
-- reporting an old number cannot un-read a thread, and a client that has somehow
-- got ahead cannot mark unsent messages as read.
--
-- The unread count is recomputed rather than zeroed outright: marking up to
-- sequence 40 on a thread that is already at 45 should leave five unread, not
-- none. It only reaches zero when the caller has caught up completely.
--
-- Scoped by participant, so a caller can only ever mark their own side read —
-- there is no way to clear somebody else's badge.
--
-- Returns ONE result set with the resulting state. Empty when the conversation
-- does not exist or is not the caller's: the same non-disclosure posture as
-- [Chat].[GetConversationForParticipant]. Never THROWs.
CREATE OR ALTER PROCEDURE [Chat].[MarkConversationRead]
    @ConversationId UNIQUEIDENTIFIER,
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER,
    @UpToSequence BIGINT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @LastSequence BIGINT;

    BEGIN TRANSACTION;

    SELECT @LastSequence = [LastSequence]
    FROM [Chat].[Conversations]
    WHERE [ConversationId] = @ConversationId;

    IF @LastSequence IS NULL
    BEGIN
        COMMIT TRANSACTION;
        RETURN;
    END

    IF @UpToSequence IS NULL OR @UpToSequence > @LastSequence
    BEGIN
        SET @UpToSequence = @LastSequence;
    END

    UPDATE [Chat].[ConversationParticipants]
    SET [LastReadSequence] = CASE WHEN @UpToSequence > [LastReadSequence]
                                  THEN @UpToSequence ELSE [LastReadSequence] END,
        -- What is left after catching up to here. CASE rather than arithmetic on
        -- the old count because unread is a counter and sequences can have gaps —
        -- subtracting sequence numbers would over-count.
        [UnreadCount] = CASE WHEN @UpToSequence >= @LastSequence THEN 0
                             ELSE [UnreadCount] END,
        [UpdatedAtUtc] = @Now
    WHERE [ConversationId] = @ConversationId
      AND [ParticipantType] = @ParticipantType
      AND [ParticipantId] = @ParticipantId;

    SELECT [ConversationId],
           [ParticipantType],
           [ParticipantId],
           [LastReadSequence],
           [UnreadCount],
           [IsMuted]
    FROM [Chat].[ConversationParticipants]
    WHERE [ConversationId] = @ConversationId
      AND [ParticipantType] = @ParticipantType
      AND [ParticipantId] = @ParticipantId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Chat].[MarkConversationRead].';
GO

-- The app's chat badge: how many unread messages the caller has, and across how
-- many threads.
--
-- Both numbers, because they answer different questions — the tab badge wants
-- the message total, while "3 conversations need you" is the more useful line in
-- a notification summary. Computing them separately client-side would need the
-- whole conversation list.
--
-- Served entirely from [IX_ConversationParticipants_Participant], which INCLUDEs
-- [UnreadCount], so it never touches [Chat].[Conversations].
--
-- Returns ONE result set, always exactly one row (zeros when the caller has no
-- conversations). Never THROWs.
CREATE OR ALTER PROCEDURE [Chat].[GetUnreadSummary]
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- A muted thread still counts toward the badge. Muting silences the push, it
    -- does not mark the messages read — the user still has them waiting.
    SELECT COALESCE(SUM([UnreadCount]), 0) AS [UnreadMessageCount],
           COALESCE(SUM(CASE WHEN [UnreadCount] > 0 THEN 1 ELSE 0 END), 0)
               AS [UnreadConversationCount]
    FROM [Chat].[ConversationParticipants]
    WHERE [ParticipantType] = @ParticipantType
      AND [ParticipantId] = @ParticipantId;
END;
GO
PRINT 'Created/updated [Chat].[GetUnreadSummary].';
GO

-- Rewrites a thread's inbox preview after its newest message has been retracted.
--
-- WHY THIS EXISTS. [Chat].[Conversations] carries a denormalised copy of the last
-- message — preview, timestamp, sender — so the inbox renders in one indexed read
-- instead of a Cosmos query per thread. A soft delete clears the message's content
-- in Cosmos and never touches SQL, so without this the retracted text goes on
-- being shown on the conversation card until something else is sent. That was a
-- reported bug: the message was gone from the thread and still legible in the
-- inbox.
--
-- The message itself is deliberately NOT removed (see the store's SoftDeleteAsync):
-- it keeps its place and its sequence, so it is still the thread's last activity
-- and the card keeps its own LastMessageAtUtc. Only the preview line changes, to
-- whatever placeholder the caller composes — user-facing copy lives in C#
-- (ChatLimits.DeletedPreviewLabel), as it does for the "Photo" label.
--
-- @Sequence IS A GUARD. The update applies only while that sequence is still the
-- thread's last, which makes two things right at once:
--   * a message that landed between the delete and this call has already moved the
--     preview on, and must not be dragged back to a retraction notice;
--   * deleting an OLDER message no-ops, which is correct — it was never on the card.
-- Reading and writing in one statement means no lock is held across a round trip
-- and no separate existence check can go stale underneath it.
--
-- Idempotent: a repeated delete writes the same placeholder over itself. Returns
-- nothing; the caller does not branch on the outcome.
CREATE OR ALTER PROCEDURE [Chat].[RefreshDeletedMessagePreview]
    @ConversationId UNIQUEIDENTIFIER,
    @Sequence BIGINT,
    @Preview NVARCHAR(200)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE [Chat].[Conversations]
    SET [LastMessagePreview] = @Preview,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [ConversationId] = @ConversationId
      AND [LastSequence] = @Sequence;
END;
GO
PRINT 'Created/updated [Chat].[RefreshDeletedMessagePreview].';
GO

-- "Delete this chat" — for the CALLER only.
--
-- The counterparty's thread is deliberately untouched: they keep every message,
-- their unread count, and their place in the conversation. That is the stated
-- requirement, and it is also the only thing the storage allows — a message body
-- is ONE Cosmos document read by both parties, so one side clearing their copy
-- can only ever be a watermark, never a delete.
--
-- What it does, all on the caller's own [Chat].[ConversationParticipants] row:
--   * [ClearedUpToSequence] = the thread's last sequence — history at or below it
--     stops being returned to this side.
--   * [DeletedAtUtc] = now — hides the thread from this side's inbox, until the
--     counterparty writes again (see the column note for why both are needed).
--   * unread zeroed and the read pointer advanced — a thread you have cleared
--     cannot still be owing you attention.
--
-- The conversation row, the messages, and the other side's state are ALL left
-- alone, so this is reversible in every way that matters: one new message and the
-- thread is back on the caller's inbox, carrying on from there.
--
-- LEGAL HOLD: refused outright while an open support ticket names this
-- conversation. A chat incident is reported precisely because of what was said,
-- so allowing either side to clear their copy while support is reading it would
-- defeat the report. It binds BOTH parties — the accused is the one with a motive
-- to erase — and lifts when the ticket closes.
--
-- Returns ONE result set — the caller's updated participant state — or NOTHING
-- when the conversation is unknown OR not theirs. Those two are deliberately the
-- same answer, as everywhere else in this schema, so a conversation id cannot be
-- probed for existence.
--
-- THROWs: 51352 the conversation is under legal hold. Checked only AFTER the
-- participant check, so a stranger still gets the empty result and cannot use
-- this to discover that a thread exists and is under investigation.
CREATE OR ALTER PROCEDURE [Chat].[DeleteConversationForParticipant]
    @ConversationId UNIQUEIDENTIFIER,
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @LastSequence BIGINT;

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK on the conversation row, for the reason the watermark
    -- exists at all: it has to be the thread's last sequence AS OF this instant.
    -- A message committing between the read and the write would otherwise be
    -- swept under a watermark taken before it arrived — cleared from the caller's
    -- history without ever having been seen. [Chat].[CommitMessageAppend] takes
    -- the same row first, so the two serialise in the same lock order.
    SELECT @LastSequence = c.[LastSequence]
    FROM [Chat].[Conversations] AS c WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN [Chat].[ConversationParticipants] AS p
        ON p.[ConversationId] = c.[ConversationId]
       AND p.[ParticipantType] = @ParticipantType
       AND p.[ParticipantId] = @ParticipantId
    WHERE c.[ConversationId] = @ConversationId;

    IF @LastSequence IS NULL
    BEGIN
        -- Unknown, or not the caller's. Empty result set is the answer.
        COMMIT TRANSACTION;
        RETURN;
    END

    -- Served by [UX_Tickets_OpenConversation], filtered to exactly this predicate,
    -- so the ordinary unreported thread pays one seek that finds nothing.
    IF EXISTS (
        SELECT 1
        FROM [Support].[Tickets]
        WHERE [ConversationId] = @ConversationId
          AND [Status] <> N'CLOSED')
    BEGIN
        THROW 51352, 'This conversation cannot be cleared while a support ticket is open on it.', 1;
    END

    UPDATE [Chat].[ConversationParticipants]
    SET [ClearedUpToSequence] = @LastSequence,
        -- Never move a read pointer backwards — the same invariant
        -- [Chat].[MarkConversationRead] holds. In practice clearing always
        -- advances it, but a concurrent mark-read must not be undone.
        [LastReadSequence] = CASE WHEN [LastReadSequence] > @LastSequence
                                  THEN [LastReadSequence]
                                  ELSE @LastSequence END,
        [UnreadCount] = 0,
        [DeletedAtUtc] = @Now,
        [UpdatedAtUtc] = @Now
    WHERE [ConversationId] = @ConversationId
      AND [ParticipantType] = @ParticipantType
      AND [ParticipantId] = @ParticipantId;

    SELECT [ConversationId],
           [ParticipantType],
           [ParticipantId],
           [LastReadSequence],
           [UnreadCount],
           -- CAST because BIT loses to INT in data type precedence the moment it
           -- meets one in an expression, and SqlDataReader.GetBoolean does not
           -- coerce — the exact trap that took down [Chat].[AppendMessage].
           CAST([IsMuted] AS BIT) AS [IsMuted],
           [ClearedUpToSequence],
           [DeletedAtUtc]
    FROM [Chat].[ConversationParticipants]
    WHERE [ConversationId] = @ConversationId
      AND [ParticipantType] = @ParticipantType
      AND [ParticipantId] = @ParticipantId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Chat].[DeleteConversationForParticipant].';
GO

-- Records a live SignalR connection, or refreshes its heartbeat.
--
-- Called from OnConnectedAsync and again on every client heartbeat, so it is an
-- upsert rather than an insert: a heartbeat for a connection the stale sweep
-- already purged (a long GC pause, a slow tick) re-registers it instead of
-- failing, which is the behaviour that keeps a live user reachable.
--
-- Presence is what decides whether a message earns a push, so a missing row costs
-- the user an unwanted buzz rather than a lost message — the safe direction to
-- fail in.
--
-- Never THROWs. Returns nothing.
CREATE OR ALTER PROCEDURE [Chat].[SaveChatConnection]
    @ConnectionId NVARCHAR(128),
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    UPDATE [Chat].[ChatConnections]
    SET [LastHeartbeatAtUtc] = @Now,
        -- Re-asserted on every heartbeat: a connection id is unique per socket,
        -- so if it somehow resolves to a different participant the newer claim is
        -- the true one.
        [ParticipantType] = @ParticipantType,
        [ParticipantId] = @ParticipantId
    WHERE [ConnectionId] = @ConnectionId;

    IF @@ROWCOUNT = 0
    BEGIN
        BEGIN TRY
            INSERT INTO [Chat].[ChatConnections]
                ([ConnectionId], [ParticipantType], [ParticipantId],
                 [ConnectedAtUtc], [LastHeartbeatAtUtc])
            VALUES
                (@ConnectionId, @ParticipantType, @ParticipantId, @Now, @Now);
        END TRY
        BEGIN CATCH
            -- 2601/2627: a concurrent connect for the same id won the race. Its
            -- row is as good as the one we were about to write, so let it stand.
            IF ERROR_NUMBER() NOT IN (2601, 2627)
            BEGIN
                THROW;
            END
        END CATCH
    END
END;
GO
PRINT 'Created/updated [Chat].[SaveChatConnection].';
GO

-- Removes a connection on disconnect.
--
-- Best-effort by design: OnDisconnectedAsync does not run if the host crashes or
-- the socket dies unnoticed, which is exactly why
-- [Chat].[PurgeStaleConnections] exists. This is the tidy path, not the
-- guaranteed one.
--
-- Never THROWs. Returns nothing.
CREATE OR ALTER PROCEDURE [Chat].[DeleteChatConnection]
    @ConnectionId NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM [Chat].[ChatConnections]
    WHERE [ConnectionId] = @ConnectionId;
END;
GO
PRINT 'Created/updated [Chat].[DeleteChatConnection].';
GO

-- Records which thread a connection currently has open — or NULL when the client
-- has navigated away.
--
-- This one column is the whole of presence-gated push: [Chat].[CommitMessageAppend]
-- sends an FCM push only when the recipient has no connection whose
-- [ActiveConversationId] matches. Without it the choice would be to buzz someone
-- for a message they are reading, or to leave an offline recipient silent.
--
-- It is set from JoinConversation / LeaveConversation on the hub, and cleared
-- implicitly when the connection row is deleted.
--
-- Scoped by participant as well as connection id: a connection may only declare
-- itself viewing on behalf of the participant it authenticated as, so a client
-- cannot suppress somebody else's push by claiming their id.
--
-- The caller is expected to have already authorised the participant against the
-- conversation (via [Chat].[GetConversationForParticipant]); this does not
-- re-check membership, because being "on" a thread you are not part of has no
-- effect — the presence match in AppendMessage is scoped to the recipient.
--
-- Never THROWs. Returns nothing.
CREATE OR ALTER PROCEDURE [Chat].[SetActiveConversation]
    @ConnectionId NVARCHAR(128),
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER,
    @ConversationId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE [Chat].[ChatConnections]
    SET [ActiveConversationId] = @ConversationId,
        -- Opening a thread is proof of life, so it counts as a heartbeat and
        -- keeps the stale sweep off an actively used connection.
        [LastHeartbeatAtUtc] = SYSUTCDATETIME()
    WHERE [ConnectionId] = @ConnectionId
      AND [ParticipantType] = @ParticipantType
      AND [ParticipantId] = @ParticipantId;
END;
GO
PRINT 'Created/updated [Chat].[SetActiveConversation].';
GO

-- Deletes connection rows whose heartbeat has gone quiet.
--
-- Run on a timer by ChatPresenceSweepFunction, and it is what makes presence
-- trustworthy: OnDisconnectedAsync is best-effort — a crashed host, a killed
-- process or a silently dropped socket never fires it — so without this sweep
-- dead rows would accumulate and permanently suppress a user's pushes. That is
-- the failure mode worth engineering against: a stale row does not merely waste
-- space, it makes the recipient look present forever.
--
-- @StaleMinutes must stay comfortably longer than the client heartbeat interval,
-- or live connections get purged out from under themselves. A purged-but-live
-- connection self-heals on its next heartbeat ([Chat].[SaveChatConnection] is an
-- upsert), but it earns the user a spurious push in the meantime.
--
-- Returns ONE row: how many were removed, for the function's log line.
-- Never THROWs.
CREATE OR ALTER PROCEDURE [Chat].[PurgeStaleConnections]
    @StaleMinutes INT = 3
AS
BEGIN
    SET NOCOUNT ON;

    IF @StaleMinutes IS NULL OR @StaleMinutes < 1
    BEGIN
        SET @StaleMinutes = 3;
    END

    DECLARE @Cutoff DATETIME2(7) = DATEADD(MINUTE, -@StaleMinutes, SYSUTCDATETIME());

    DELETE FROM [Chat].[ChatConnections]
    WHERE [LastHeartbeatAtUtc] < @Cutoff;

    SELECT @@ROWCOUNT AS [PurgedCount];
END;
GO
PRINT 'Created/updated [Chat].[PurgeStaleConnections].';
GO

-- The three chat-scoped block procedures are RETIRED. The table they read moved
-- to [Block] in section 1, so leaving them would leave three procedures that
-- compile and then fail at runtime against a table that is no longer there.
-- Dropped rather than left behind, so a stray caller fails loudly at deploy time
-- instead of quietly at 3am.
IF OBJECT_ID(N'[Chat].[BlockChatParticipant]', N'P') IS NOT NULL
BEGIN
    DROP PROCEDURE [Chat].[BlockChatParticipant];
    PRINT 'Dropped retired [Chat].[BlockChatParticipant].';
END
GO

IF OBJECT_ID(N'[Chat].[UnblockChatParticipant]', N'P') IS NOT NULL
BEGIN
    DROP PROCEDURE [Chat].[UnblockChatParticipant];
    PRINT 'Dropped retired [Chat].[UnblockChatParticipant].';
END
GO

IF OBJECT_ID(N'[Chat].[ListBlockedParticipants]', N'P') IS NOT NULL
BEGIN
    DROP PROCEDURE [Chat].[ListBlockedParticipants];
    PRINT 'Dropped retired [Chat].[ListBlockedParticipants].';
END
GO

-- Blocks the counterparty, in the caller's direction.
--
-- Idempotent: blocking somebody already blocked returns the existing row rather
-- than failing or duplicating, so a double-tap is harmless. [WasAlreadyBlocked]
-- says which happened.
--
-- A block severs the pair across the product from this one row -- messages,
-- new bookings, sight of each other's events, and the provider's place in browse
-- and all five searches. See [Block].[BlockedParticipants] for the full policy and
-- for what a block deliberately does NOT do.
--
-- Returns TWO result sets:
--   1. the block row, plus [WasAlreadyBlocked].
--   2. the pair's UNFINISHED jobs, which the caller then cancels one by one
--      through the ordinary status transition -- so each gets its party check,
--      its audit row, its freed capacity and its counterparty notification for
--      free, exactly as [IBulkBookingCancellationService] does.
--
-- WHY THE JOBS ARE RETURNED RATHER THAN CANCELLED HERE: cancelling in T-SQL would
-- mean duplicating the whole status engine (from-state rules, audit, capacity,
-- the notification enqueue) inside this procedure, and the two copies would drift.
-- The list is captured inside the transaction that writes the block, so a booking
-- cannot be created between the block landing and the list being taken --
-- [Booking].[CreateBooking] reads this table under HOLDLOCK and therefore
-- serialises against the INSERT below.
--
-- WHAT IS DELIBERATELY NOT IN THAT LIST:
--   * IN_PROGRESS / ENDING -- the pet is physically in someone's care and
--     [Booking].[UpdateBookingStatus] refuses the cancel (THROW 51149). That guard
--     is not bypassed; such a job runs to completion carrying the blocked flag.
--   * COMPLETED / PAID and every terminal status -- nothing to cancel. (PAID is
--     named explicitly: the engine's own terminal list omits it, since PAID is
--     reachable only from COMPLETED, which is terminal.)
--   * a CREATED booking that has ALREADY expired under BR-17 (24h unanswered) or
--     BR-53 (under 2h to the service). The engine rejects every transition on
--     those, cancel included (THROW 51129 / 51153), so listing them would produce
--     a guaranteed per-item failure for a booking that is already dead and merely
--     waiting for the sweep to label it EXPIRED.
--
-- THROWs: 51327 the two parties are on the same side (a blockable relationship
-- only ever runs provider <-> parent, so such a block could never be consulted).
CREATE OR ALTER PROCEDURE [Block].[BlockParticipant]
    @BlockerType NVARCHAR(16),
    @BlockerId UNIQUEIDENTIFIER,
    @BlockedType NVARCHAR(16),
    @BlockedId UNIQUEIDENTIFIER,
    @Reason NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @BlockerType NOT IN (N'Provider', N'PetParent')
        OR @BlockedType NOT IN (N'Provider', N'PetParent')
        OR @BlockerType = @BlockedType
    BEGIN
        THROW 51327, 'A block must run between a provider and a pet parent.', 1;
    END

    IF LTRIM(RTRIM(COALESCE(@Reason, N''))) = N''
    BEGIN
        SET @Reason = NULL;
    END

    -- Which side is which. Everything below is expressed in terms of the pair, not
    -- of who did the blocking, so one query serves both directions.
    DECLARE @ProviderId UNIQUEIDENTIFIER =
        CASE WHEN @BlockerType = N'Provider' THEN @BlockerId ELSE @BlockedId END;
    DECLARE @PetParentId UNIQUEIDENTIFIER =
        CASE WHEN @BlockerType = N'PetParent' THEN @BlockerId ELSE @BlockedId END;

    DECLARE @BlockId UNIQUEIDENTIFIER;
    DECLARE @WasAlreadyBlocked BIT = 0;
    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK over the unique 4-tuple: with no row yet this takes a
    -- range lock, so two concurrent blocks serialise instead of one hitting a
    -- UNIQUE violation -- and a concurrent booking create, which reads this same
    -- range under HOLDLOCK, serialises behind it too.
    SELECT @BlockId = [BlockId]
    FROM [Block].[BlockedParticipants] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BlockerType] = @BlockerType
      AND [BlockerId] = @BlockerId
      AND [BlockedType] = @BlockedType
      AND [BlockedId] = @BlockedId;

    IF @BlockId IS NULL
    BEGIN
        DECLARE @Inserted TABLE ([BlockId] UNIQUEIDENTIFIER);

        INSERT INTO [Block].[BlockedParticipants]
            ([BlockerType], [BlockerId], [BlockedType], [BlockedId], [Reason], [CreatedAtUtc])
        OUTPUT inserted.[BlockId] INTO @Inserted
        VALUES
            (@BlockerType, @BlockerId, @BlockedType, @BlockedId, @Reason, @Now);

        SELECT @BlockId = [BlockId] FROM @Inserted;
    END
    ELSE
    BEGIN
        SET @WasAlreadyBlocked = 1;
    END

    -- Result set 1: the block row.
    SELECT [BlockId],
           [BlockerType],
           [BlockerId],
           [BlockedType],
           [BlockedId],
           [Reason],
           [CreatedAtUtc],
           -- CAST is not decoration: COALESCE/CASE over a BIT and an INT literal
           -- yields INT by data-type precedence, and SqlDataReader.GetBoolean does
           -- not coerce -- it throws, after this transaction has committed.
           CAST(@WasAlreadyBlocked AS BIT) AS [WasAlreadyBlocked]
    FROM [Block].[BlockedParticipants]
    WHERE [BlockId] = @BlockId;

    -- Result set 2: the unfinished jobs for the pair, soonest first, both kinds.
    -- Empty on a repeat block -- the first one already cancelled them.
    SELECT [BookingType],
           [BookingId],
           [JobNumber],
           [Status],
           [ServiceDate]
    FROM (
        SELECT N'SingleDay' AS [BookingType],
               b.[BookingId] AS [BookingId],
               b.[JobNumber] AS [JobNumber],
               b.[Status] AS [Status],
               b.[BookingDate] AS [ServiceDate]
        FROM [Booking].[Bookings] b
        WHERE b.[ProviderId] = @ProviderId
          AND b.[PetParentId] = @PetParentId
          AND b.[Status] NOT IN (N'COMPLETED', N'PAID', N'IN_PROGRESS', N'ENDING',
                                 N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                 N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED',
                                 N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
          -- Already dead under BR-17 / BR-53; the engine would reject the cancel.
          AND NOT (
                b.[Status] = N'CREATED'
                AND (
                    @Now >= DATEADD(HOUR, 24, b.[CreatedAtUtc])
                    OR DATEADD(SECOND,
                               DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), b.[StartTime]),
                               CAST(b.[BookingDate] AS DATETIME2(7))) < DATEADD(HOUR, 2, @Now)
                )
          )

        UNION ALL

        SELECT N'NightStay' AS [BookingType],
               n.[NightStayBookingId] AS [BookingId],
               n.[JobNumber] AS [JobNumber],
               n.[Status] AS [Status],
               n.[CheckInDate] AS [ServiceDate]
        FROM [Booking].[NightStayBookings] n
        WHERE n.[ProviderId] = @ProviderId
          AND n.[PetParentId] = @PetParentId
          AND n.[Status] NOT IN (N'COMPLETED', N'PAID', N'IN_PROGRESS', N'ENDING',
                                 N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                                 N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED',
                                 N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
          AND NOT (
                n.[Status] = N'CREATED'
                AND (
                    @Now >= DATEADD(HOUR, 24, n.[CreatedAtUtc])
                    OR DATEADD(SECOND,
                               DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                               CAST(n.[CheckInDate] AS DATETIME2(7))) < DATEADD(HOUR, 2, @Now)
                )
          )
    ) AS [Jobs]
    ORDER BY [ServiceDate] ASC, [BookingId] ASC;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Block].[BlockParticipant].';
GO

-- Lifts a block the caller placed.
--
-- Scoped to the caller as BLOCKER, so a user can only ever undo their own block --
-- there is no way to remove one placed against you.
--
-- A block is ALWAYS the blocker's to lift, with no exceptions. A support ticket
-- does not freeze one: reporting somebody does not block them, so the two are
-- independent remedies -- the user decides who they will deal with, support
-- decides what to do about the report.
--
-- UNBLOCKING RESTORES CONTACT, NOT BOOKINGS. The pair can message, book, see each
-- other's events and find each other in search again from the moment the row goes.
-- The jobs the block cancelled stay cancelled: they were cancelled through the
-- ordinary transition, with an audit row and their capacity released back to the
-- provider's calendar, and that capacity may since have been sold to somebody
-- else. Re-booking is a new booking.
--
-- Returns ONE result set describing what was lifted. Empty when the id is unknown
-- OR belongs to somebody else's block: deliberately the same case, so a block id
-- cannot be probed for existence (the posture
-- [Provider].[DeactivateProviderDeviceToken] and the review-photo delete take).
-- The caller maps an empty result to 404.
CREATE OR ALTER PROCEDURE [Block].[UnblockParticipant]
    @BlockId UNIQUEIDENTIFIER,
    @BlockerType NVARCHAR(16),
    @BlockerId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DELETE FROM [Block].[BlockedParticipants]
    OUTPUT deleted.[BlockId],
           deleted.[BlockerType],
           deleted.[BlockerId],
           deleted.[BlockedType],
           deleted.[BlockedId],
           deleted.[Reason],
           deleted.[CreatedAtUtc]
    WHERE [BlockId] = @BlockId
      AND [BlockerType] = @BlockerType
      AND [BlockerId] = @BlockerId;
END;
GO
PRINT 'Created/updated [Block].[UnblockParticipant].';
GO

-- Everyone the caller has blocked, newest first -- the "blocked list" screen.
--
-- Lists only blocks the caller PLACED. Blocks placed against them are
-- deliberately not returned: telling someone they have been blocked confirms the
-- other party acted, which is the thing a block is meant to end. A blocked user
-- gets the same neutral "not available" wherever they run into it.
--
-- Names joined LIVE, so a blocked account that has since been deleted reads
-- "Deleted Provider" / "Deleted User" rather than keeping its real name -- the
-- same invariant every other counterparty read here holds.
--
-- [BlockedServiceCategory] is NOT for display. It is the Cosmos partition key the
-- caller needs in order to fetch a blocked PROVIDER's BUSINESS name and image
-- from their offering document: [Provider].[Providers] holds only the person's
-- own name, and a parent who blocked "Happy Paws Hotel" must see that, not just
-- the owner's. One point read per distinct provider on the page, best-effort --
-- a provider still mid-onboarding has no offering yet, and the list must still
-- render. Null for a blocked pet parent, who has no business.
--
-- Paged with [TotalCount] via COUNT(*) OVER(), so one round trip serves the page
-- and its total. @Take is clamped to 20, the figure every other list here uses.
--
-- Returns ONE result set. Never THROWs.
CREATE OR ALTER PROCEDURE [Block].[ListBlockedParticipants]
    @BlockerType NVARCHAR(16),
    @BlockerId UNIQUEIDENTIFIER,
    @Skip INT = 0,
    @Take INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    IF @Skip IS NULL OR @Skip < 0 SET @Skip = 0;
    IF @Take IS NULL OR @Take < 1 SET @Take = 20;
    IF @Take > 20 SET @Take = 20;

    SELECT b.[BlockId],
           b.[BlockerType],
           b.[BlockerId],
           b.[BlockedType],
           b.[BlockedId],
           b.[Reason],
           b.[CreatedAtUtc],
           CASE WHEN b.[BlockedType] = N'Provider'
                THEN LTRIM(RTRIM(COALESCE(pr.[FirstName], N'') + N' ' + COALESCE(pr.[LastName], N'')))
                ELSE LTRIM(RTRIM(COALESCE(pp.[FirstName], N'') + N' ' + COALESCE(pp.[LastName], N'')))
           END AS [BlockedName],
           -- Parent photos only; a provider's image lives in their Cosmos
           -- offering document and is resolved by the caller alongside the
           -- business name, using the category below.
           CASE WHEN b.[BlockedType] = N'PetParent' THEN pp.[ProfilePhotoUrl] END
               AS [BlockedPhotoUrl],
           -- UNIQUE on ProviderId, so this join cannot fan the page out.
           CASE WHEN b.[BlockedType] = N'Provider' THEN reg.[ServiceCategory] END
               AS [BlockedServiceCategory],
           COUNT(*) OVER() AS [TotalCount]
    FROM [Block].[BlockedParticipants] b
    LEFT JOIN [Provider].[Providers] pr
        ON b.[BlockedType] = N'Provider' AND pr.[ProviderId] = b.[BlockedId]
    LEFT JOIN [Parent].[PetParents] pp
        ON b.[BlockedType] = N'PetParent' AND pp.[PetParentId] = b.[BlockedId]
    LEFT JOIN [Provider].[ProviderServiceRegistrations] reg
        ON b.[BlockedType] = N'Provider' AND reg.[ProviderId] = b.[BlockedId]
    WHERE b.[BlockerType] = @BlockerType
      AND b.[BlockerId] = @BlockerId
    ORDER BY b.[CreatedAtUtc] DESC, b.[BlockId] DESC
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;
GO
PRINT 'Created/updated [Block].[ListBlockedParticipants].';
GO

-- Every counterparty the caller is blocked from, in EITHER direction.
--
-- This is the single read behind all the per-request block state: the isBlocked
-- flag on booking cards and booking details, the flag on conversation reads, and
-- the filter that removes a blocked provider from browse and the five searches.
-- It is scoped to the ACTOR rather than taking a list of counterparty ids so a
-- page of twenty bookings costs ONE round trip instead of twenty -- the same
-- reasoning behind [Review].[ListPetParentBookingReviews] and
-- [Support].[ListMyOpenTicketSubjects].
--
-- BOTH DIRECTIONS, because every rule this feeds is symmetric: a booking is
-- refused, a thread is closed and an event is hidden whichever party placed the
-- block. That is also why the two directions collapse to ONE row per
-- counterparty -- a mutual block is still one severed relationship, and two rows
-- would make every caller de-duplicate.
--
-- [BlockedByMe] is what the apps branch on for the ACTION: only my own block
-- offers an Unblock button, so [BlockId] is returned ONLY for my own. Handing
-- back the id of a block placed against me would both be useless -- I cannot lift
-- it -- and leak that the other party acted, which is the one thing a block must
-- not confirm. A caller seeing isBlocked with BlockedByMe false shows a neutral
-- "not available".
--
-- Returns ONE result set, one row per blocked counterparty. Never THROWs.
CREATE OR ALTER PROCEDURE [Block].[ListMyBlockedCounterparties]
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [CounterpartyType],
           [CounterpartyId],
           -- CAST: MAX over a CASE yielding INT literals is INT, and
           -- SqlDataReader.GetBoolean does not coerce -- it throws.
           CAST(MAX([IsMine]) AS BIT) AS [BlockedByMe],
           MAX([MyBlockId]) AS [BlockId],
           MIN([CreatedAtUtc]) AS [BlockedAtUtc]
    FROM (
        -- Blocks I placed.
        SELECT b.[BlockedType] AS [CounterpartyType],
               b.[BlockedId] AS [CounterpartyId],
               1 AS [IsMine],
               b.[BlockId] AS [MyBlockId],
               b.[CreatedAtUtc] AS [CreatedAtUtc]
        FROM [Block].[BlockedParticipants] b
        WHERE b.[BlockerType] = @ParticipantType
          AND b.[BlockerId] = @ParticipantId

        UNION ALL

        -- Blocks placed against me. The id is deliberately NOT carried through.
        SELECT b.[BlockerType] AS [CounterpartyType],
               b.[BlockerId] AS [CounterpartyId],
               0 AS [IsMine],
               NULL AS [MyBlockId],
               b.[CreatedAtUtc] AS [CreatedAtUtc]
        FROM [Block].[BlockedParticipants] b
        WHERE b.[BlockedType] = @ParticipantType
          AND b.[BlockedId] = @ParticipantId
    ) AS [Blocks]
    GROUP BY [CounterpartyType], [CounterpartyId];
END;
GO
PRINT 'Created/updated [Block].[ListMyBlockedCounterparties].';
GO

PRINT '--- Pawfront deployment complete ---';
GO

-- Raises a support ticket against the counterparty.
--
-- Reporting somebody does NOT block them. Nothing here writes to
-- [Block].[BlockedParticipants]: the pair stay able to message, book and find each
-- other while support looks at the case, and blocking remains what it always was
-- — the users' own remedy, placed by hand through the chat host and lifted the
-- same way. A ticket is a report to support, not a sanction applied on the
-- reporter's say-so.
--
-- Handles ALL FOUR kinds, because the only thing that differs between them is
-- where the party ids come from:
--   @TicketType = 'BookingIncident' -> @BookingType + @BookingId name the job.
--                                      Both party ids and the pet come off the
--                                      booking row.
--   @TicketType = 'ChatIncident'    -> @ConversationId names the thread. Both
--                                      party ids come off the conversation row.
--   @TicketType = 'EventIncident'   -> @EventId names the event. There is NO
--                                      counterparty: only the reporter's own
--                                      party column is set (see below).
--   @TicketType = 'AppIssue'        -> no subject at all. Reporter only.
--
-- The reporter passes only their OWN id (@ActorId) and which side they are on.
-- For the two counterparty kinds the OTHER party is DERIVED from the subject and
-- never accepted from the caller, which is what stops a report being filed
-- against somebody who was never party to the booking or thread.
--
-- An event report records no counterparty on purpose. The organiser is one join
-- away through @EventId, and a parent-organised event reported by another parent
-- could not be stored in the one-Provider / one-PetParent shape anyway. An event
-- is public: anyone signed in can see one, so anyone signed in can report one —
-- there is no attendance check.
--
-- ONE open ticket per SUBJECT, not per person:
--   booking      -> per (BookingType, BookingId), either direction
--   conversation -> per ConversationId, either direction
--   event        -> per (EventId, REPORTER). An event has many attendees and each
--                   of them reporting it is a separate account; what is refused
--                   is the same person reporting it twice.
--   app issue    -> no rule. Each bug report is a different bug.
-- The check and the insert share a transaction so the read cannot go stale
-- between them, with [UX_Tickets_OpenBooking] / [UX_Tickets_OpenConversation] /
-- [UX_Tickets_OpenEventReporter] as the race-safe backstop underneath.
--
-- Returns TWO result sets:
--   1. [Outcome] = 'Created' | 'TicketAlreadyOpen', followed by the ticket row.
--      On 'TicketAlreadyOpen' the row is the ticket ALREADY open on this booking
--      or conversation — nothing was written — so the caller can answer 409
--      naming it rather than leaving the reporter at a dead end. Discriminated
--      rather than THROWn for the same reason
--      [Provider].[SetProviderActiveStatus] and [Provider].[CreateClosures] are:
--      the conflict carries data the app needs.
--   2. the ticket's photos — always empty here (photos are a second call, keyed
--      by the ticket id this mints), present so the read paths share one shape.
--
-- THROWs: 51340 booking not found, 51341 caller is not a party to the subject,
-- 51342 conversation not found, 51343 invalid request (defensive), 51348 Custom
-- walk-in (no pet parent to report or be reported), 51355 event not found.
CREATE OR ALTER PROCEDURE [Support].[CreateTicket]
    @TicketType NVARCHAR(24),
    @RaisedByType NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @BookingType NVARCHAR(16) = NULL,
    @BookingId UNIQUEIDENTIFIER = NULL,
    @ConversationId UNIQUEIDENTIFIER = NULL,
    @Category NVARCHAR(100) = NULL,
    @Reason NVARCHAR(500) = NULL,
    @EventId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Defensive: the API validates all of this first, so reaching these is a
    -- direct-caller error rather than something a client can provoke.
    IF @TicketType NOT IN (N'BookingIncident', N'ChatIncident', N'EventIncident', N'AppIssue')
        OR @RaisedByType NOT IN (N'Provider', N'PetParent')
        OR @ActorId IS NULL
        OR (@TicketType = N'BookingIncident'
            AND (@BookingId IS NULL OR @BookingType NOT IN (N'SingleDay', N'NightStay')))
        OR (@TicketType = N'ChatIncident' AND @ConversationId IS NULL)
        OR (@TicketType = N'EventIncident' AND @EventId IS NULL)
    BEGIN
        THROW 51343, 'Invalid support ticket request.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @PetId UNIQUEIDENTIFIER;
    DECLARE @Found BIT = 0;
    DECLARE @TicketId UNIQUEIDENTIFIER;
    DECLARE @ExistingTicketId UNIQUEIDENTIFIER;
    DECLARE @Outcome NVARCHAR(24);

    -- Blank and absent are the same thing for both classifiers, so a client sending
    -- an empty picker value stores NULL rather than an empty string that every read
    -- would then have to treat as "not set".
    IF LTRIM(RTRIM(COALESCE(@Category, N''))) = N''
    BEGIN
        SET @Category = NULL;
    END

    IF LTRIM(RTRIM(COALESCE(@Reason, N''))) = N''
    BEGIN
        SET @Reason = NULL;
    END

    BEGIN TRANSACTION;

    IF @TicketType = N'BookingIncident'
    BEGIN
        -- No UPDLOCK on the booking: nothing about its status affects whether it
        -- can be reported, so this read cannot go stale in a way that matters.
        -- Any booking either party is on is reportable, at any point in its life —
        -- an incident is a statement about what happened, not a transition.
        IF @BookingType = N'SingleDay'
        BEGIN
            SELECT @ProviderId = [ProviderId],
                   @PetParentId = [PetParentId],
                   @PetId = [PetId],
                   @Found = 1
            FROM [Booking].[Bookings]
            WHERE [BookingId] = @BookingId;
        END
        ELSE
        BEGIN
            SELECT @ProviderId = [ProviderId],
                   @PetParentId = [PetParentId],
                   @PetId = [PetId],
                   @Found = 1
            FROM [Booking].[NightStayBookings]
            WHERE [NightStayBookingId] = @BookingId;
        END

        IF @Found = 0
        BEGIN
            THROW 51340, 'Booking was not found.', 1;
        END

        -- A Custom walk-in carries free-text customer details and no PetParentId,
        -- so there is no second party to report or to be reported.
        -- (Night-stay is App-only, so this can only fire on a single-day booking.)
        IF @PetParentId IS NULL
        BEGIN
            THROW 51348, 'Only app bookings can be reported.', 1;
        END
    END
    ELSE IF @TicketType = N'ChatIncident'
    BEGIN
        SELECT @ProviderId = [ProviderId],
               @PetParentId = [PetParentId],
               @Found = 1
        FROM [Chat].[Conversations]
        WHERE [ConversationId] = @ConversationId;

        IF @Found = 0
        BEGIN
            THROW 51342, 'Conversation was not found.', 1;
        END
    END
    ELSE
    BEGIN
        -- 'EventIncident' and 'AppIssue': the reporter is the only party, so
        -- their own column is set from @RaisedByType and the other stays NULL.
        -- CK_Tickets_SubjectMatchesType enforces exactly that shape.
        IF @RaisedByType = N'Provider'
        BEGIN
            SET @ProviderId = @ActorId;
        END
        ELSE
        BEGIN
            SET @PetParentId = @ActorId;
        END

        IF @TicketType = N'EventIncident'
        BEGIN
            -- Existence only. An event is public, so there is no attendance or
            -- ownership test to apply — anyone who can see one can report it.
            IF NOT EXISTS (SELECT 1 FROM [Event].[Events] WHERE [EventId] = @EventId)
            BEGIN
                THROW 51355, 'Event was not found.', 1;
            END
        END
    END

    -- The caller must be the side they claim to be, on the subject they named.
    -- Trivially satisfied for the two reporter-only kinds, where the column was
    -- just set FROM @ActorId — deliberately left to fall through rather than
    -- branched around, so there is one place this rule is stated.
    IF (@RaisedByType = N'Provider' AND @ProviderId <> @ActorId)
        OR (@RaisedByType = N'PetParent' AND @PetParentId <> @ActorId)
    BEGIN
        THROW 51341, 'You are not a party to this.', 1;
    END

    -- Scoped to the SUBJECT, not to the pair: this is what lets a parent report
    -- every booking they have with one provider, while still refusing a second
    -- report of the SAME job.
    --
    -- UPDLOCK + HOLDLOCK over the matching filtered-unique range. With no open
    -- ticket yet this takes a range lock, so two reports filed at the same
    -- instant serialise: the second finds the first rather than both inserting
    -- and one failing on the unique index. Either party's open ticket is found,
    -- since one incident is one case however many people report it.
    IF @TicketType = N'BookingIncident'
    BEGIN
        SELECT @ExistingTicketId = [TicketId]
        FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
        WHERE [BookingId] = @BookingId
          AND [BookingType] = @BookingType
          AND [Status] <> N'CLOSED';
    END
    ELSE IF @TicketType = N'ChatIncident'
    BEGIN
        SELECT @ExistingTicketId = [TicketId]
        FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ConversationId] = @ConversationId
          AND [Status] <> N'CLOSED';
    END
    ELSE IF @TicketType = N'EventIncident'
    BEGIN
        -- Scoped to the REPORTER as well as the event, unlike the two above: a
        -- second attendee reporting the same event is a second account of it and
        -- gets its own ticket. Matches [UX_Tickets_OpenEventReporter], which is
        -- the backstop if two of this reporter's requests land together.
        SELECT @ExistingTicketId = [TicketId]
        FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
        WHERE [EventId] = @EventId
          AND [Status] <> N'CLOSED'
          AND ((@RaisedByType = N'Provider'  AND [ProviderId]  = @ActorId)
            OR (@RaisedByType = N'PetParent' AND [PetParentId] = @ActorId));
    END
    -- 'AppIssue' has no subject, so nothing to collide with: every report is a
    -- new ticket. @ExistingTicketId stays NULL and the insert below always runs.

    IF @ExistingTicketId IS NOT NULL
    BEGIN
        -- One open ticket per booking / per conversation (either direction), or
        -- per event per reporter.
        -- Nothing is written; the caller reports the conflict naming this ticket.
        SET @Outcome = N'TicketAlreadyOpen';
        SET @TicketId = @ExistingTicketId;
    END
    ELSE
    BEGIN
        SET @Outcome = N'Created';

        DECLARE @Inserted TABLE ([TicketId] UNIQUEIDENTIFIER);

        -- The ticket row is the ONLY thing written. Nothing touches
        -- [Block].[BlockedParticipants]: a report is not a block, and the reported
        -- party keeps every ability they had — messaging, booking, discovery —
        -- until support decides otherwise off-platform.
        INSERT INTO [Support].[Tickets]
            ([TicketType], [ProviderId], [PetParentId], [RaisedByType],
             [BookingType], [BookingId], [PetId], [ConversationId], [EventId],
             [Category], [Reason], [Status], [CreatedAtUtc], [UpdatedAtUtc])
        OUTPUT inserted.[TicketId] INTO @Inserted
        VALUES
            (@TicketType, @ProviderId, @PetParentId, @RaisedByType,
             @BookingType, @BookingId, @PetId, @ConversationId, @EventId,
             @Category, @Reason, N'OPENED', @Now, @Now);

        SELECT @TicketId = [TicketId] FROM @Inserted;
    END

    SELECT @Outcome AS [Outcome],
           t.[TicketId],
           t.[TicketNumber],
           t.[TicketType],
           t.[ProviderId],
           t.[PetParentId],
           t.[RaisedByType],
           t.[BookingType],
           t.[BookingId],
           t.[PetId],
           t.[ConversationId],
           t.[Status],
           t.[CreatedAtUtc],
           t.[UpdatedAtUtc],
           t.[ClosedAtUtc],
           -- The reporter's two classifiers and the event subject, appended LAST
           -- here and in the five other procedures that project this row, so
           -- the existing reader ordinals did not shift when they were added.
           t.[Category],
           t.[Reason],
           t.[EventId]
    FROM [Support].[Tickets] AS t
    WHERE t.[TicketId] = @TicketId;

    SELECT [TicketPhotoId],
           [TicketId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Support].[TicketPhotos]
    WHERE [TicketId] = @TicketId
    ORDER BY [CreatedAtUtc] ASC, [TicketPhotoId] ASC;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Support].[CreateTicket].';
GO

-- One ticket, scoped to a party to it.
--
-- Returns TWO result sets — the ticket row, then its photos (oldest-first) —
-- or NOTHING when the ticket is unknown OR the caller is not a party. Those two
-- are deliberately the same answer, as everywhere else in this codebase, so a
-- ticket id cannot be probed for existence. Never THROWs.
--
-- The narrative — the reporter's comment and the clarification thread — is NOT
-- here. It lives in the Cosmos "SupportTickets" document, which the caller point
-- reads by [TicketId]; this is the row that says whether they may.
CREATE OR ALTER PROCEDURE [Support].[GetTicket]
    @TicketId UNIQUEIDENTIFIER,
    @ActorType NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (
        SELECT 1
        FROM [Support].[Tickets]
        WHERE [TicketId] = @TicketId
          AND ((@ActorType = N'Provider'  AND [ProviderId]  = @ActorId)
            OR (@ActorType = N'PetParent' AND [PetParentId] = @ActorId)))
    BEGIN
        RETURN;
    END

    SELECT [TicketId],
           [TicketNumber],
           [TicketType],
           [ProviderId],
           [PetParentId],
           [RaisedByType],
           [BookingType],
           [BookingId],
           [PetId],
           [ConversationId],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [ClosedAtUtc],
           -- Appended LAST, here and in every other procedure projecting this row,
           -- so adding them shifted no existing reader ordinal.
           [Category],
           [Reason],
           [EventId]
    FROM [Support].[Tickets]
    WHERE [TicketId] = @TicketId;

    SELECT [TicketPhotoId],
           [TicketId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Support].[TicketPhotos]
    WHERE [TicketId] = @TicketId
    ORDER BY [CreatedAtUtc] ASC, [TicketPhotoId] ASC;
END;
GO
PRINT 'Created/updated [Support].[GetTicket].';
GO

-- One party's tickets — the "my tickets" list on either app.
--
-- Scoped by the CALLER, not by direction: a ticket the counterparty raised
-- against you is as much yours as one you raised, and both need answering. The
-- row carries [RaisedByType] so the card can say which way round it is.
--
-- ALL FOUR ticket types come back in one list, discriminated by [TicketType]. A
-- user thinks "my tickets", not "my four kinds of tickets" — the same reasoning
-- that merges the two booking kinds in the parent's history feed. An 'AppIssue'
-- or 'EventIncident' has only a reporter, so the caller matches on whichever of
-- the two party columns is theirs and the other is NULL; the predicate below
-- needs no change for that.
--
-- @Statuses is a plain CSV, expanded in C#. Keeping the vocabulary there rather
-- than teaching this procedure about groups like "open" means adding a status is
-- an edit to one C# file instead of to every reader.
--
-- Returns ONE result set — the page, with the whole-population [TotalCount]
-- appended as a window so one round trip serves both the rows and the total.
CREATE OR ALTER PROCEDURE [Support].[ListTickets]
    @ActorType NVARCHAR(16),                    -- 'Provider' | 'PetParent'
    @ActorId UNIQUEIDENTIFIER,
    @Statuses NVARCHAR(MAX) = NULL,
    @SortBy NVARCHAR(16) = N'UpdatedAt',        -- 'CreatedAt' | 'UpdatedAt'
    @SortDirection NVARCHAR(8) = N'Desc',       -- 'Asc' | 'Desc'
    @Skip INT = 0,
    @Take INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    IF @Skip IS NULL OR @Skip < 0 SET @Skip = 0;
    IF @Take IS NULL OR @Take <= 0 SET @Take = 20;
    IF @Take > 20 SET @Take = 20;

    SELECT t.[TicketId],
           t.[TicketNumber],
           t.[TicketType],
           t.[ProviderId],
           t.[PetParentId],
           t.[RaisedByType],
           t.[BookingType],
           t.[BookingId],
           t.[PetId],
           t.[ConversationId],
           t.[Status],
           t.[CreatedAtUtc],
           t.[UpdatedAtUtc],
           t.[ClosedAtUtc],
           -- Appended after [ClosedAtUtc] to keep the ticket block contiguous and
           -- identical to the other five procedures — which pushes [TotalCount]
           -- from ordinal 14 to 17. Its reader in SqlSupportTicketStore reads it
           -- positionally, so the two must move together.
           t.[Category],
           t.[Reason],
           t.[EventId],
           COUNT(*) OVER () AS [TotalCount]
    FROM [Support].[Tickets] AS t
    WHERE ((@ActorType = N'Provider'  AND t.[ProviderId]  = @ActorId)
        OR (@ActorType = N'PetParent' AND t.[PetParentId] = @ActorId))
      AND (@Statuses IS NULL OR @Statuses = N''
           OR t.[Status] IN (SELECT LTRIM(RTRIM([value])) FROM STRING_SPLIT(@Statuses, N',')))
    ORDER BY
        CASE WHEN @SortBy = N'CreatedAt' AND @SortDirection = N'Asc'  THEN t.[CreatedAtUtc] END ASC,
        CASE WHEN @SortBy = N'CreatedAt' AND @SortDirection = N'Desc' THEN t.[CreatedAtUtc] END DESC,
        CASE WHEN @SortBy <> N'CreatedAt' AND @SortDirection = N'Asc'  THEN t.[UpdatedAtUtc] END ASC,
        CASE WHEN @SortBy <> N'CreatedAt' AND @SortDirection = N'Desc' THEN t.[UpdatedAtUtc] END DESC,
        -- Deterministic tie-break. Without it OFFSET paging can repeat or skip
        -- rows sharing a timestamp, which tickets raised in a burst will.
        t.[TicketId] ASC
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;
GO
PRINT 'Created/updated [Support].[ListTickets].';
GO

-- Attaches one photo to a ticket that can carry evidence — everything except a
-- chat incident.
--
-- Scoped to the ticket's CREATOR, not to either party. The evidence is the
-- reporter's account of what happened, and the status vocabulary agrees — support
-- asks for clarification of the CREATOR and receives it from the CREATOR, so the
-- counterparty is never in the evidence loop.
--
-- The 5-photo cap is counted under UPDLOCK + HOLDLOCK because it is a race: two
-- uploads in flight would each read "room for one more". The endpoint pre-checks
-- it as well, so a caller already at the cap is not charged an upload first, but
-- this is the check that actually holds.
--
-- Returns TWO result sets: the ticket row, then ALL its photos oldest-first —
-- the same shape [Support].[GetTicket] returns, so the caller re-renders from one
-- mapping.
--
-- THROWs: 51344 ticket not found for this creator, 51345 already at the cap,
-- 51346 chat incident (carries no photos), 51347 ticket is closed.
CREATE OR ALTER PROCEDURE [Support].[AddTicketPhoto]
    @TicketId UNIQUEIDENTIFIER,
    @ActorType NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @MaxPhotos INT = 5;
    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @TicketType NVARCHAR(24);
    DECLARE @Status NVARCHAR(48);
    DECLARE @PhotoCount INT;

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK on the ticket row: it is what the photo count below is
    -- taken against, and it also serialises against a concurrent close.
    SELECT @TicketType = [TicketType],
           @Status = [Status]
    FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
    WHERE [TicketId] = @TicketId
      AND [RaisedByType] = @ActorType
      AND ((@ActorType = N'Provider'  AND [ProviderId]  = @ActorId)
        OR (@ActorType = N'PetParent' AND [PetParentId] = @ActorId));

    -- Unknown ticket and "not the one you raised" are one answer, so neither can
    -- be probed (the posture [Chat].[UnblockChatParticipant] and
    -- [Review].[AddBookingReviewPhoto] both take).
    IF @TicketType IS NULL
    BEGIN
        THROW 51344, 'Ticket was not found.', 1;
    END

    IF @TicketType = N'ChatIncident'
    BEGIN
        -- The one kind that carries no photos: the images already in the thread
        -- are the evidence, and the whole conversation is under legal hold.
        -- Booking incidents, event incidents and app issues all take them.
        THROW 51346, 'A reported chat cannot carry photos.', 1;
    END

    IF @Status = N'CLOSED'
    BEGIN
        THROW 51347, 'This ticket is closed.', 1;
    END

    SELECT @PhotoCount = COUNT(*)
    FROM [Support].[TicketPhotos]
    WHERE [TicketId] = @TicketId;

    IF @PhotoCount >= @MaxPhotos
    BEGIN
        THROW 51345, 'This ticket already has the maximum number of photos.', 1;
    END

    INSERT INTO [Support].[TicketPhotos] ([TicketId], [PhotoUrl], [CreatedAtUtc])
    VALUES (@TicketId, @PhotoUrl, @Now);

    -- Adding evidence is activity on the ticket, so it moves the timestamp the
    -- "my tickets" list sorts on by default.
    UPDATE [Support].[Tickets]
    SET [UpdatedAtUtc] = @Now
    WHERE [TicketId] = @TicketId;

    SELECT [TicketId],
           [TicketNumber],
           [TicketType],
           [ProviderId],
           [PetParentId],
           [RaisedByType],
           [BookingType],
           [BookingId],
           [PetId],
           [ConversationId],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [ClosedAtUtc],
           -- Appended LAST, here and in every other procedure projecting this row,
           -- so adding them shifted no existing reader ordinal.
           [Category],
           [Reason]
    FROM [Support].[Tickets]
    WHERE [TicketId] = @TicketId;

    SELECT [TicketPhotoId],
           [TicketId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Support].[TicketPhotos]
    WHERE [TicketId] = @TicketId
    ORDER BY [CreatedAtUtc] ASC, [TicketPhotoId] ASC;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Support].[AddTicketPhoto].';
GO

-- The creator has answered support's request for clarification: moves the ticket
-- CLARIFICATION_ASKED_TO_CREATOR -> CLARIFICATION_RECEIVED_FROM_CREATOR.
--
-- This is the ONE transition either app can drive. Every other status belongs to
-- the admin panel, which is why there is no general "set status" route on the two
-- hosts — only this, and it can move the ticket to exactly one place.
--
-- The reply TEXT is not here. It is appended to the ticket's Cosmos document
-- BEFORE this runs, and the ordering is deliberate, for the reason the chat send
-- learned the hard way: if the document write fails after the status has already
-- moved, the ticket claims an answer that was never recorded. This way a failure
-- leaves the ticket sitting in ASKED with the reply stored — visibly unfinished,
-- and fixed by retrying. The caller authorises through [Support].[GetTicket]
-- first; this re-checks anyway, since by now the Cosmos write has happened and
-- the check is nearly free.
--
-- Returns ONE result set: the updated ticket row.
--
-- THROWs: 51344 ticket not found for this creator, 51347 ticket is closed,
-- 51349 no clarification was asked for.
CREATE OR ALTER PROCEDURE [Support].[RecordTicketClarification]
    @TicketId UNIQUEIDENTIFIER,
    @ActorType NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Status NVARCHAR(48);

    BEGIN TRANSACTION;

    -- Scoped to the CREATOR: support asks the creator and receives from the
    -- creator, so the counterparty is never in this loop.
    SELECT @Status = [Status]
    FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
    WHERE [TicketId] = @TicketId
      AND [RaisedByType] = @ActorType
      AND ((@ActorType = N'Provider'  AND [ProviderId]  = @ActorId)
        OR (@ActorType = N'PetParent' AND [PetParentId] = @ActorId));

    IF @Status IS NULL
    BEGIN
        THROW 51344, 'Ticket was not found.', 1;
    END

    IF @Status = N'CLOSED'
    BEGIN
        THROW 51347, 'This ticket is closed.', 1;
    END

    IF @Status <> N'CLARIFICATION_ASKED_TO_CREATOR'
    BEGIN
        -- Nothing was asked, so there is nothing to answer. Rejecting rather than
        -- silently accepting keeps the status honest: support reads
        -- CLARIFICATION_RECEIVED as "the question I asked has been answered".
        THROW 51349, 'No clarification has been requested on this ticket.', 1;
    END

    UPDATE [Support].[Tickets]
    SET [Status] = N'CLARIFICATION_RECEIVED_FROM_CREATOR',
        [UpdatedAtUtc] = @Now
    WHERE [TicketId] = @TicketId;

    SELECT [TicketId],
           [TicketNumber],
           [TicketType],
           [ProviderId],
           [PetParentId],
           [RaisedByType],
           [BookingType],
           [BookingId],
           [PetId],
           [ConversationId],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [ClosedAtUtc],
           -- Appended LAST, here and in every other procedure projecting this row,
           -- so adding them shifted no existing reader ordinal.
           [Category],
           [Reason],
           [EventId]
    FROM [Support].[Tickets]
    WHERE [TicketId] = @TicketId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Support].[RecordTicketClarification].';
GO

-- Moves a ticket between the working statuses. The admin panel's procedure.
--
-- NO endpoint on either app host calls this, and none should: every status here
-- is support's own reading of the case. The one transition a user drives —
-- answering a clarification request — has its own procedure
-- ([Support].[RecordTicketClarification]), guarded to a single from-state and
-- scoped to the creator.
--
-- CLOSED is deliberately NOT settable here. Closing releases the three holds an
-- open ticket carries — the subject can be reported again, the reported chat can
-- be cleared, and the account and pet deletes stop refusing — so it goes through
-- [Support].[CloseTicket], where that is stated and stamps [ClosedAtUtc]. Routing
-- it here instead would make closure look like any other status move.
--
-- Returns ONE result set: the updated ticket row.
--
-- THROWs: 51344 ticket not found, 51347 ticket is closed (terminal — reopening is
-- not modelled; raise a fresh ticket), 51350 invalid or unsettable status.
CREATE OR ALTER PROCEDURE [Support].[UpdateTicketStatus]
    @TicketId UNIQUEIDENTIFIER,
    @Status NVARCHAR(48)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Status NOT IN (
        N'OPENED',
        N'IN_REVIEW',
        N'CLARIFICATION_ASKED_TO_CREATOR',
        N'CLARIFICATION_RECEIVED_FROM_CREATOR',
        N'PENDING_WITH_LEGAL_TEAM')
    BEGIN
        THROW 51350, 'That status cannot be set here.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Current NVARCHAR(48);

    BEGIN TRANSACTION;

    SELECT @Current = [Status]
    FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
    WHERE [TicketId] = @TicketId;

    IF @Current IS NULL
    BEGIN
        THROW 51344, 'Ticket was not found.', 1;
    END

    IF @Current = N'CLOSED'
    BEGIN
        THROW 51347, 'This ticket is closed.', 1;
    END

    -- Setting the status it already holds is a no-op rather than an error: the
    -- panel may well re-send it, and there is nothing to protect here (unlike a
    -- booking transition, no side effect hangs off the write).
    IF @Current <> @Status
    BEGIN
        UPDATE [Support].[Tickets]
        SET [Status] = @Status,
            [UpdatedAtUtc] = @Now
        WHERE [TicketId] = @TicketId;
    END

    SELECT [TicketId],
           [TicketNumber],
           [TicketType],
           [ProviderId],
           [PetParentId],
           [RaisedByType],
           [BookingType],
           [BookingId],
           [PetId],
           [ConversationId],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [ClosedAtUtc]
    FROM [Support].[Tickets]
    WHERE [TicketId] = @TicketId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Support].[UpdateTicketStatus].';
GO

-- Closes a ticket.
--
-- NO endpoint on either app host calls this — closing is the admin panel's job,
-- and neither party may close a ticket raised against them (or, for that matter,
-- their own). It ships now, ahead of the panel, so that closing a ticket is a
-- documented single call rather than hand-edited rows.
--
-- Nothing but the ticket's own status changes. Raising a ticket does not block
-- anybody, so closing one has no severance to lift: whatever is in
-- [Block].[BlockedParticipants] was put there by a user tapping Block, and is
-- theirs alone to remove.
--
-- What closing DOES release are the three holds keyed off "is there an open
-- ticket": the same booking or conversation can be reported again, the reported
-- conversation can be cleared, and the account and pet deletes stop refusing.
-- All three read [Status], so none of them needs anything written here.
--
-- Returns ONE result set: the closed ticket row. Idempotent — closing an
-- already-closed ticket returns it with its original [ClosedAtUtc].
--
-- THROWs: 51344 ticket not found.
CREATE OR ALTER PROCEDURE [Support].[CloseTicket]
    @TicketId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Status NVARCHAR(48);

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK: the [UX_Tickets_OpenBooking] /
    -- [UX_Tickets_OpenConversation] range this row occupies is what a concurrent
    -- report of the same subject is waiting on, so closing and re-reporting
    -- serialise rather than briefly allowing two open tickets on one booking.
    SELECT @Status = [Status]
    FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
    WHERE [TicketId] = @TicketId;

    IF @Status IS NULL
    BEGIN
        THROW 51344, 'Ticket was not found.', 1;
    END

    IF @Status <> N'CLOSED'
    BEGIN
        UPDATE [Support].[Tickets]
        SET [Status] = N'CLOSED',
            [ClosedAtUtc] = @Now,
            [UpdatedAtUtc] = @Now
        WHERE [TicketId] = @TicketId;
    END

    SELECT [TicketId],
           [TicketNumber],
           [TicketType],
           [ProviderId],
           [PetParentId],
           [RaisedByType],
           [BookingType],
           [BookingId],
           [PetId],
           [ConversationId],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [ClosedAtUtc],
           -- Appended LAST, here and in every other procedure projecting this row,
           -- so adding them shifted no existing reader ordinal.
           [Category],
           [Reason],
           [EventId]
    FROM [Support].[Tickets]
    WHERE [TicketId] = @TicketId;

    COMMIT TRANSACTION;
END;
GO
PRINT 'Created/updated [Support].[CloseTicket].';
GO

-- Is this conversation under legal hold, and if so which ticket holds it?
--
-- Exists because ONE of the two chat delete paths cannot check the hold for
-- itself. Clearing a thread is a T-SQL write
-- ([Chat].[DeleteConversationForParticipant]) and consults [Support].[Tickets]
-- inline. Deleting a MESSAGE is not: the body lives in Cosmos and the retraction
-- is a document replace, with no SQL statement in the path to hang the check on.
-- So the service reads this first.
--
-- Returns ONE result set, empty when the thread is free. A row means held, and
-- carries the ticket so the refusal can name it — "TK-000123 is open on this
-- chat" is actionable in a way that "you cannot delete this" is not.
--
-- Both parties are held, not just the reporter. The accused is the one with a
-- motive to erase, so a hold binding only the person who raised it would be
-- decorative.
--
-- Never THROWs.
CREATE OR ALTER PROCEDURE [Support].[GetConversationLegalHold]
    @ConversationId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Served by [UX_Tickets_OpenConversation], which is filtered to exactly this
    -- predicate: on the overwhelmingly common free-thread path this is an index
    -- seek that finds nothing.
    SELECT TOP (1)
           [TicketId],
           [TicketNumber],
           [TicketType],
           [Status]
    FROM [Support].[Tickets]
    WHERE [ConversationId] = @ConversationId
      AND [Status] <> N'CLOSED'
    ORDER BY [CreatedAtUtc] ASC;
END;
GO
PRINT 'Created/updated [Support].[GetConversationLegalHold].';
GO

-- "Which subjects do I already have an open ticket on?"
--
-- Feeds the [isTicketRaisedByMe] / [ticketId] pair on every surface that offers a
-- Report button: the booking detail and both booking lists, the event list and
-- detail, and the chat inbox and thread — on all three hosts. Without it those
-- screens offer "Report" on something the caller already reported, and the
-- attempt comes back 409.
--
-- Scoped by the CALLER as the REPORTER, deliberately — the flag is
-- "raised by me", not "raised by anybody". The counterparty's ticket on the same
-- booking is not the caller's to open, and its existence is not theirs to be told
-- about. (One consequence worth knowing: because the one-open-ticket rule runs in
-- either direction, a caller whose counterparty has already reported the booking
-- reads FALSE here and is still refused with 409 TicketAlreadyOpen.)
--
-- OPEN tickets only. A closed ticket is exactly the state in which a fresh report
-- is allowed again, so counting one would leave the app permanently offering to
-- open a ticket the user can no longer reach. It also keeps the answer
-- unambiguous: at most one open ticket exists per subject, so there is exactly
-- one id to return.
--
-- Returns ONE result set — one row per open ticket the caller raised, whatever
-- its kind. The caller keys them by subject in memory. Scoped to the actor rather
-- than taking a list of subject ids on purpose: a booking list page then costs
-- ONE read instead of one per card, the same reasoning behind
-- [Review].[ListPetParentBookingReviews].
--
-- Served by [IX_Tickets_OpenRaisedByProvider] / [IX_Tickets_OpenRaisedByPetParent],
-- which are filtered to exactly this predicate and INCLUDE every column below.
--
-- Never THROWs: "no open tickets" is the overwhelmingly common answer and is an
-- empty result set, not an error.
CREATE OR ALTER PROCEDURE [Support].[ListMyOpenTicketSubjects]
    @RaisedByType NVARCHAR(16),                 -- 'Provider' | 'PetParent'
    @ActorId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [TicketId],
           [TicketNumber],
           [TicketType],
           [BookingType],
           [BookingId],
           [ConversationId],
           [EventId]
    FROM [Support].[Tickets]
    WHERE [RaisedByType] = @RaisedByType
      AND [Status] <> N'CLOSED'
      AND ((@RaisedByType = N'Provider'  AND [ProviderId]  = @ActorId)
        OR (@RaisedByType = N'PetParent' AND [PetParentId] = @ActorId))
    -- An app issue has no subject and can never match a card, but it is returned
    -- anyway rather than filtered here: the shape stays "my open tickets", and a
    -- future surface that wants to show one needs no procedure change.
    ORDER BY [CreatedAtUtc] ASC, [TicketId] ASC;
END;
GO
PRINT 'Created/updated [Support].[ListMyOpenTicketSubjects].';
GO
