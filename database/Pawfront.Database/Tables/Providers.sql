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

GO

CREATE UNIQUE INDEX [UX_Providers_MobileNumber]
    ON [Provider].[Providers] ([MobileCountryCode], [MobileNumber]);

GO

ALTER TABLE [Provider].[ProviderAuthIdentities]
ADD CONSTRAINT [FK_ProviderAuthIdentities_Providers_ProviderId]
    FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]);
