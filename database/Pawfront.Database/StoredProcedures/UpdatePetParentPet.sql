CREATE OR ALTER PROCEDURE [Parent].[UpdatePetParentPet]
    @PetId UNIQUEIDENTIFIER,
    @PetType NVARCHAR(32),
    @PetName NVARCHAR(100),
    @Breed NVARCHAR(100),
    @Gender NVARCHAR(16),
    @DateOfBirth DATE,
    @Weight DECIMAL(5, 2),
    @MicrochipId NVARCHAR(32) = NULL,
    @Description NVARCHAR(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Only the basic-info subset is updated here. Medical-info fields
    -- (VaccinationStatus / SterilizationStatus / MedicalHistory / Temperament)
    -- are owned by [Parent].[UpdatePetMedicalInfo] and intentionally left
    -- alone — passing them via this sproc would let an "edit basic info"
    -- screen accidentally wipe a parent's medical entries.
    UPDATE [Parent].[Pets]
    SET [PetType] = @PetType,
        [PetName] = @PetName,
        [Breed] = @Breed,
        [Gender] = @Gender,
        [DateOfBirth] = @DateOfBirth,
        [Weight] = @Weight,
        [MicrochipId] = @MicrochipId,
        [Description] = @Description,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [PetId] = @PetId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51205, 'Pet was not found.', 1;
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
           [UpdatedAtUtc]
    FROM [Parent].[Pets]
    WHERE [PetId] = @PetId;
END;
