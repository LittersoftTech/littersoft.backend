CREATE TABLE [Parent].[PetParents]
(
    [PetParentId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_PetParents_PetParentId] DEFAULT NEWSEQUENTIALID(),
    [ParentAuthIdentityId] UNIQUEIDENTIFIER NOT NULL,
    [FirstName] NVARCHAR(100) NOT NULL,
    [LastName] NVARCHAR(100) NOT NULL,
    [Gender] NVARCHAR(32) NOT NULL,
    [MobileCountryCode] NVARCHAR(8) NOT NULL,
    [MobileNumber] NVARCHAR(32) NOT NULL,
    [DateOfBirth] DATE NOT NULL,
    [AddressLine] NVARCHAR(500) NOT NULL,
    [Latitude] DECIMAL(9, 6) NOT NULL,
    [Longitude] DECIMAL(9, 6) NOT NULL,
    [ZipCode] NVARCHAR(16) NOT NULL,
    [City] NVARCHAR(100) NOT NULL,
    [Description] NVARCHAR(2000) NOT NULL,
    [ProfilePhotoUrl] NVARCHAR(1000) NULL,
    [MobileVerifiedAtUtc] DATETIME2(7) NULL,
    -- Account delete = anonymise + permanently disable, never a row delete: the
    -- PetParentId has to keep its meaning for every booking, event, and payment
    -- that references it. [Parent].[DeletePetParent] sets these; [IsDeleted] is
    -- permanent and blocks profile edits (which would undo the anonymisation).
    [IsDeleted] BIT NOT NULL
        CONSTRAINT [DF_PetParents_IsDeleted] DEFAULT 0,
    [DeletedAtUtc] DATETIME2(7) NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_PetParents_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_PetParents_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_PetParents] PRIMARY KEY CLUSTERED ([PetParentId] ASC),
    CONSTRAINT [UQ_PetParents_ParentAuthIdentityId] UNIQUE ([ParentAuthIdentityId]),
    CONSTRAINT [FK_PetParents_ParentAuthIdentities_ParentAuthIdentityId]
        FOREIGN KEY ([ParentAuthIdentityId]) REFERENCES [Parent].[ParentAuthIdentities] ([ParentAuthIdentityId]),
    CONSTRAINT [CK_PetParents_Gender]
        CHECK ([Gender] IN (N'Male', N'Female', N'NonBinary', N'Other', N'PreferNotToSay')),
    CONSTRAINT [CK_PetParents_Latitude]
        CHECK ([Latitude] BETWEEN -90 AND 90),
    CONSTRAINT [CK_PetParents_Longitude]
        CHECK ([Longitude] BETWEEN -180 AND 180)
);

GO

-- Filtered so legacy rows migrated without a mobile number (nullable on the
-- upgrade path in DeployAll.sql) don't collide; inert on fresh installs where
-- the column is NOT NULL.
CREATE UNIQUE INDEX [UX_PetParents_MobileNumber]
    ON [Parent].[PetParents] ([MobileCountryCode], [MobileNumber])
    WHERE [MobileNumber] IS NOT NULL;

GO

ALTER TABLE [Parent].[ParentAuthIdentities]
ADD CONSTRAINT [FK_ParentAuthIdentities_PetParents_PetParentId]
    FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId]);
