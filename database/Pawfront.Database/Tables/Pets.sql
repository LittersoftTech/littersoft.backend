CREATE TABLE [Customer].[Pets]
(
    [PetId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_Pets_PetId] DEFAULT NEWSEQUENTIALID(),
    [PetParentId] UNIQUEIDENTIFIER NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Pets_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Pets_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_Pets] PRIMARY KEY CLUSTERED ([PetId] ASC),
    CONSTRAINT [FK_Pets_PetParents_PetParentId]
        FOREIGN KEY ([PetParentId]) REFERENCES [Customer].[PetParents] ([PetParentId])
);

GO

CREATE INDEX [IX_Pets_PetParentId]
    ON [Customer].[Pets] ([PetParentId]);
