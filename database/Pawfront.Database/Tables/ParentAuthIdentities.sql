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

GO

CREATE UNIQUE INDEX [UX_ParentAuthIdentities_PetParentId]
    ON [Parent].[ParentAuthIdentities] ([PetParentId])
    WHERE [PetParentId] IS NOT NULL;

GO

CREATE INDEX [IX_ParentAuthIdentities_Email]
    ON [Parent].[ParentAuthIdentities] ([Email]);

-- FK back to [Parent].[PetParents] is added at the bottom of PetParents.sql,
-- once that table exists. Pattern mirrors Provider.ProviderAuthIdentities ↔ Providers.
