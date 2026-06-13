-- General photo gallery owned directly by a provider (not tied to a service).
-- One row per uploaded photo. ON DELETE CASCADE so removing a provider removes
-- the photo URLs from this table (the blobs themselves are not cleaned up by
-- the cascade — the delete endpoint best-effort removes the blob, and a future
-- job sweeps orphans).
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

GO

CREATE INDEX [IX_ProviderPhotos_ProviderId]
    ON [Provider].[ProviderPhotos] ([ProviderId])
    INCLUDE ([PhotoUrl], [CreatedAtUtc]);
