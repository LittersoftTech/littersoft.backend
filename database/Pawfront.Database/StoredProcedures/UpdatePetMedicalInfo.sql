CREATE OR ALTER PROCEDURE [Parent].[UpdatePetMedicalInfo]
    @PetId UNIQUEIDENTIFIER,
    @VaccinationStatus NVARCHAR(32),
    @SterilizationStatus NVARCHAR(32),
    @MedicalHistory NVARCHAR(MAX) = NULL,
    -- Temperament is optional — null when the parent hasn't set one.
    @Temperament NVARCHAR(32) = NULL,
    -- Free-text optional medical fields — null when not provided.
    @VaccinationType NVARCHAR(100) = NULL,
    @VaccinationDose NVARCHAR(64) = NULL,
    @Prescription NVARCHAR(MAX) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE [Parent].[Pets]
    SET [VaccinationStatus] = @VaccinationStatus,
        [SterilizationStatus] = @SterilizationStatus,
        [MedicalHistory] = @MedicalHistory,
        [Temperament] = @Temperament,
        [VaccinationType] = @VaccinationType,
        [VaccinationDose] = @VaccinationDose,
        [Prescription] = @Prescription,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [PetId] = @PetId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51203, 'Pet was not found.', 1;
    END

    SELECT [PetId],
           [PetParentId],
           [PetType],
           [PetName],
           [Breed],
           [Gender],
           [DateOfBirth],
           [Weight],
           [MicrochipId],
           [Description],
           [VaccinationStatus],
           [SterilizationStatus],
           [MedicalHistory],
           [Temperament],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [ProfilePhotoUrl],
           [VaccinationType],
           [VaccinationDose],
           [Prescription]
    FROM [Parent].[Pets]
    WHERE [PetId] = @PetId;
END;
