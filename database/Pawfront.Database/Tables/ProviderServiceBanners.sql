-- One banner image per bookable service (Provider.ProviderServices row). A
-- provider uploads a banner for a specific ServiceId (e.g. their DayCare or
-- NightStay service can each carry their own banner). Distinct from the
-- Cosmos offering image (the discovery/profile photo) — this is a wide banner
-- shown on the service's own screen. Upserted: re-uploading replaces the row.
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
