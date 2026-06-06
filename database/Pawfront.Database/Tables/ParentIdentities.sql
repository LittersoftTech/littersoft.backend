CREATE TABLE [Parent].[ParentIdentities]
(
    [ParentIdentityId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_ParentIdentities_ParentIdentityId] DEFAULT NEWSEQUENTIALID(),
    [PetParentId] UNIQUEIDENTIFIER NOT NULL,
    [IdentityType] NVARCHAR(32) NOT NULL,
    [IdentityPhotoUrl] NVARCHAR(1000) NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ParentIdentities_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ParentIdentities_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_ParentIdentities] PRIMARY KEY CLUSTERED ([ParentIdentityId] ASC),
    CONSTRAINT [UQ_ParentIdentities_PetParentId] UNIQUE ([PetParentId]),
    CONSTRAINT [FK_ParentIdentities_PetParents_PetParentId]
        FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId])
        ON DELETE CASCADE,
    CONSTRAINT [CK_ParentIdentities_IdentityType]
        CHECK ([IdentityType] IN (N'Passport', N'DriverLicense', N'NationalId', N'ResidencePermit'))
);
