CREATE OR ALTER PROCEDURE [Parent].[GetPetParentPet]
    @PetId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Result set 1: the single pet (zero or one row). The application returns
    -- 404 PetNotFound when this set is empty.
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
           [ProfilePhotoUrl]
    FROM [Parent].[Pets]
    WHERE [PetId] = @PetId;

    -- Result set 2: the pet's photo gallery, oldest-first so the mobile
    -- gallery renders in upload order. Nested under the pet in the response.
    SELECT [PetPhotoId],
           [PetId],
           [PhotoUrl],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[PetPhotos]
    WHERE [PetId] = @PetId
    ORDER BY [CreatedAtUtc] ASC;
END;
