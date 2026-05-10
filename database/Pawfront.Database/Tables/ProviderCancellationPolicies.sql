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
