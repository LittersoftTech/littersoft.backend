-- General photo gallery owned directly by a pet parent (not tied to a pet).
-- One row per uploaded photo. ON DELETE CASCADE so removing a parent removes
-- the photo URLs from this table (the blobs themselves are not cleaned up by
-- the cascade — the delete endpoint best-effort removes the blob, and a future
-- job sweeps orphans).
CREATE TABLE [Parent].[PetParentPhotos]
(
    [PetParentPhotoId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_PetParentPhotos_PetParentPhotoId] DEFAULT NEWSEQUENTIALID(),
    [PetParentId] UNIQUEIDENTIFIER NOT NULL,
    [PhotoUrl] NVARCHAR(1000) NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_PetParentPhotos_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_PetParentPhotos] PRIMARY KEY CLUSTERED ([PetParentPhotoId] ASC),
    CONSTRAINT [FK_PetParentPhotos_PetParents_PetParentId]
        FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId])
        ON DELETE CASCADE
);

GO

CREATE INDEX [IX_PetParentPhotos_PetParentId]
    ON [Parent].[PetParentPhotos] ([PetParentId])
    INCLUDE ([PhotoUrl], [CreatedAtUtc]);
