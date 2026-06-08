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
    CONSTRAINT [CK_ParentDeviceTokens_DevicePlatform] CHECK ([DevicePlatform] IS NULL OR [DevicePlatform] IN (N'Android', N'iOS'))
);

GO

CREATE INDEX [IX_ParentDeviceTokens_PetParentId_IsActive]
    ON [Parent].[ParentDeviceTokens] ([PetParentId], [IsActive]);

GO

CREATE INDEX [IX_ParentDeviceTokens_ParentAuthIdentityId_IsActive]
    ON [Parent].[ParentDeviceTokens] ([ParentAuthIdentityId], [IsActive]);
