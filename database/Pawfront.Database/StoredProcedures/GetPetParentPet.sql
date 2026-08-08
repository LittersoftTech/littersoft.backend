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
           [ProfilePhotoUrl],
           [VaccinationType],
           [VaccinationDose],
           [Prescription]
    FROM [Parent].[Pets]
    -- Soft-deleted pets are invisible to both apps' pet screens: the row only
    -- survives so a past booking's petDetails join still resolves.
    WHERE [PetId] = @PetId AND [IsDeleted] = 0;

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

    -- Result set 3: the pet's next-consultation dates, one row per provider
    -- type (Groomer | Vet | Trainer). Written by the booking-complete flow.
    SELECT [PetId],
           [ConsultationType],
           [NextConsultationDate]
    FROM [Parent].[PetNextConsultations]
    WHERE [PetId] = @PetId
    ORDER BY [ConsultationType] ASC;
END;
