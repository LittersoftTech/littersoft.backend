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
