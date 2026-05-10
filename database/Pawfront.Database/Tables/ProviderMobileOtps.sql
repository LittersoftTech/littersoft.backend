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
    CONSTRAINT [CK_ProviderMobileOtps_ValidationStatus] CHECK ([ValidationStatus] IN (N'Pending', N'Validated', N'Expired'))
);

GO

CREATE INDEX [IX_ProviderMobileOtps_ProviderId_DateSentUtc]
    ON [Provider].[ProviderMobileOtps] ([ProviderId], [DateSentUtc] DESC);
