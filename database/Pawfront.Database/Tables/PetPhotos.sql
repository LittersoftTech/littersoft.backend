CREATE TABLE [Parent].[PetPhotos]
(
    [PetPhotoId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_PetPhotos_PetPhotoId] DEFAULT NEWSEQUENTIALID(),
    [PetId] UNIQUEIDENTIFIER NOT NULL,
    [PhotoUrl] NVARCHAR(1000) NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_PetPhotos_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_PetPhotos_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_PetPhotos] PRIMARY KEY CLUSTERED ([PetPhotoId] ASC),
    CONSTRAINT [FK_PetPhotos_Pets_PetId]
        FOREIGN KEY ([PetId]) REFERENCES [Parent].[Pets] ([PetId])
        ON DELETE CASCADE
);

GO

CREATE INDEX [IX_PetPhotos_PetId]
    ON [Parent].[PetPhotos] ([PetId])
    INCLUDE ([PhotoUrl], [CreatedAtUtc]);
