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

GO

CREATE UNIQUE INDEX [UX_ProviderAuthIdentities_ProviderId]
    ON [Provider].[ProviderAuthIdentities] ([ProviderId])
    WHERE [ProviderId] IS NOT NULL;

GO

CREATE INDEX [IX_ProviderAuthIdentities_Email]
    ON [Provider].[ProviderAuthIdentities] ([Email]);
