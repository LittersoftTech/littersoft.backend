CREATE OR ALTER PROCEDURE [Parent].[ListPetParentPets]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Result set 1: pets for this parent. Empty when the parent has no
    -- pets (or doesn't exist) — the application returns [] rather than 404,
    -- matching typical REST list semantics.
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
    -- Soft-deleted pets never appear in the parent's list; the row survives only
    -- so a past booking's petDetails join still resolves.
    WHERE [PetParentId] = @PetParentId AND [IsDeleted] = 0
    ORDER BY [CreatedAtUtc] ASC;

    -- Result set 2: photos for those pets. Grouped by PetId in the C# layer
    -- and nested under each pet in the response. Ordered oldest-first so the
    -- mobile gallery renders in upload order.
    SELECT ph.[PetPhotoId],
           ph.[PetId],
           ph.[PhotoUrl],
           ph.[CreatedAtUtc],
           ph.[UpdatedAtUtc]
    FROM [Parent].[PetPhotos] AS ph
    INNER JOIN [Parent].[Pets] AS p
        ON p.[PetId] = ph.[PetId]
    WHERE p.[PetParentId] = @PetParentId AND p.[IsDeleted] = 0
    ORDER BY ph.[CreatedAtUtc] ASC;

    -- Result set 3: next-consultation dates for those pets, one row per
    -- (pet, provider type). Grouped by PetId in the C# layer.
    SELECT c.[PetId],
           c.[ConsultationType],
           c.[NextConsultationDate]
    FROM [Parent].[PetNextConsultations] AS c
    INNER JOIN [Parent].[Pets] AS p
        ON p.[PetId] = c.[PetId]
    WHERE p.[PetParentId] = @PetParentId AND p.[IsDeleted] = 0
    ORDER BY c.[ConsultationType] ASC;
END;
