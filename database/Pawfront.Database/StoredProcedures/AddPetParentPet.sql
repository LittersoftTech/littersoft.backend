CREATE OR ALTER PROCEDURE [Parent].[AddPetParentPet]
    @PetParentId UNIQUEIDENTIFIER,
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

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51202, 'Pet parent was not found.', 1;
    END

    DECLARE @InsertedPetId TABLE ([PetId] UNIQUEIDENTIFIER);

    INSERT INTO [Parent].[Pets]
    (
        [PetParentId],
        [PetType],
        [PetName],
        [Breed],
        [Gender],
        [DateOfBirth],
        [Weight],
        [MicrochipId],
        [Description]
    )
    OUTPUT inserted.[PetId] INTO @InsertedPetId
    VALUES
    (
        @PetParentId,
        @PetType,
        @PetName,
        @Breed,
        @Gender,
        @DateOfBirth,
        @Weight,
        @MicrochipId,
        @Description
    );

    DECLARE @PetId UNIQUEIDENTIFIER = (SELECT TOP (1) [PetId] FROM @InsertedPetId);

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

    COMMIT TRANSACTION;
END;
