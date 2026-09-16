CREATE OR ALTER PROCEDURE [Parent].[GetPetParentOnboardingStatus]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Result set 1: parent profile + joined auth-identity flags. Empty if
    -- the parent doesn't exist — the application reader treats that as a
    -- 404 signal.
    SELECT pp.[PetParentId],
           pp.[ProfilePhotoUrl],
           pp.[MobileVerifiedAtUtc],
           pai.[IsEmailVerified]
    FROM [Parent].[PetParents] AS pp
    INNER JOIN [Parent].[ParentAuthIdentities] AS pai
        ON pai.[ParentAuthIdentityId] = pp.[ParentAuthIdentityId]
    WHERE pp.[PetParentId] = @PetParentId;

    -- Result set 2: pets summary. Each row carries a server-computed flag
    -- for whether the two required medical fields are populated.
    -- MedicalHistory and Temperament are intentionally NOT part of the
    -- completion check — both are optional (Temperament can be left empty).
    SELECT [PetId],
           [PetName],
           CASE
               WHEN [VaccinationStatus]   IS NOT NULL
                AND [SterilizationStatus] IS NOT NULL
               THEN CAST(1 AS BIT)
               ELSE CAST(0 AS BIT)
           END AS [IsMedicalInfoComplete],
           -- APPENDED LAST so the existing reader ordinals stay stable.
           -- Reported so the app can name which section of a pet is
           -- unfinished and deep-link straight to it; it deliberately does
           -- NOT gate onboarding, exactly as the parent's own profile photo
           -- does not. There is no basic-info flag because every basic-info
           -- column on [Parent].[Pets] is NOT NULL -- a pet that exists
           -- always has them, and a section that can never be missing would
           -- be noise.
           CAST(CASE
                    WHEN [ProfilePhotoUrl] IS NULL OR LTRIM(RTRIM([ProfilePhotoUrl])) = N'' THEN 0
                    ELSE 1
                END AS BIT) AS [HasProfilePhoto]
    FROM [Parent].[Pets]
    -- Soft-deleted pets don't count towards onboarding: the parent no longer
    -- has them, and their (retained) medical fields would otherwise keep the
    -- pet-medical-info stage looking Complete.
    WHERE [PetParentId] = @PetParentId AND [IsDeleted] = 0
    ORDER BY [CreatedAtUtc] ASC;

    -- Result set 3: identity. Zero rows = no identity uploaded yet (stage
    -- Remaining); one row = uploaded (stage Complete) with the
    -- IdentityType the parent declared.
    SELECT [IdentityType]
    FROM [Parent].[ParentIdentities]
    WHERE [PetParentId] = @PetParentId;
END;
