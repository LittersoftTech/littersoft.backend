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
    CONSTRAINT [CK_ProviderDeviceTokens_DevicePlatform] CHECK ([DevicePlatform] IS NULL OR [DevicePlatform] IN (N'Android', N'iOS'))
);

GO

CREATE INDEX [IX_ProviderDeviceTokens_ProviderId_IsActive]
    ON [Provider].[ProviderDeviceTokens] ([ProviderId], [IsActive]);

GO

CREATE INDEX [IX_ProviderDeviceTokens_ProviderAuthIdentityId_IsActive]
    ON [Provider].[ProviderDeviceTokens] ([ProviderAuthIdentityId], [IsActive]);
