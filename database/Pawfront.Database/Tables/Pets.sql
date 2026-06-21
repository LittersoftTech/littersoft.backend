CREATE TABLE [Parent].[Pets]
(
    [PetId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_Pets_PetId] DEFAULT NEWSEQUENTIALID(),
    [PetParentId] UNIQUEIDENTIFIER NOT NULL,
    [PetType] NVARCHAR(32) NOT NULL,
    [PetName] NVARCHAR(100) NOT NULL,
    [Breed] NVARCHAR(100) NOT NULL,
    [Gender] NVARCHAR(16) NOT NULL,
    [DateOfBirth] DATE NOT NULL,
    [Weight] DECIMAL(5, 2) NOT NULL,
    [MicrochipId] NVARCHAR(32) NULL,
    [Description] NVARCHAR(2000) NULL,
    -- Medical-info fields. Populated by the separate
    -- PATCH /pets/{petId}/medical-info endpoint, so all four columns stay
    -- nullable here even though the three enum fields are required from
    -- the client at PATCH time (the application layer enforces).
    [VaccinationStatus] NVARCHAR(32) NULL,
    [SterilizationStatus] NVARCHAR(32) NULL,
    [MedicalHistory] NVARCHAR(MAX) NULL,
    [Temperament] NVARCHAR(32) NULL,
    -- The pet's single primary/profile photo. Distinct from the photo gallery
    -- in [Parent].[PetPhotos]; set via POST /pets/{petId}/profile-image.
    [ProfilePhotoUrl] NVARCHAR(1000) NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Pets_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Pets_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_Pets] PRIMARY KEY CLUSTERED ([PetId] ASC),
    CONSTRAINT [FK_Pets_PetParents_PetParentId]
        FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId]),
    CONSTRAINT [CK_Pets_PetType]
        CHECK ([PetType] IN (N'Dog', N'Cat', N'Hamster', N'GuineaPig')),
    CONSTRAINT [CK_Pets_Gender]
        CHECK ([Gender] IN (N'Male', N'Female')),
    CONSTRAINT [CK_Pets_Weight_Positive]
        CHECK ([Weight] > 0),
    CONSTRAINT [CK_Pets_VaccinationStatus]
        CHECK ([VaccinationStatus] IS NULL OR [VaccinationStatus] IN (N'Vaccinated', N'NotVaccinated')),
    CONSTRAINT [CK_Pets_SterilizationStatus]
        CHECK ([SterilizationStatus] IS NULL OR [SterilizationStatus] IN (N'Sterilized', N'Intact')),
    CONSTRAINT [CK_Pets_Temperament]
        CHECK ([Temperament] IS NULL OR [Temperament] IN (N'Anxious', N'Friendly', N'Aggressive'))
);

GO

CREATE INDEX [IX_Pets_PetParentId]
    ON [Parent].[Pets] ([PetParentId]);

GO

-- Microchip IDs are globally unique per ISO 11784/11785. Filtered so rows
-- without a chip can still coexist.
CREATE UNIQUE INDEX [UX_Pets_MicrochipId]
    ON [Parent].[Pets] ([MicrochipId])
    WHERE [MicrochipId] IS NOT NULL;
