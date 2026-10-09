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
           pai.[IsEmailVerified],
           CAST(CASE WHEN
               NULLIF(LTRIM(RTRIM(pp.[FirstName])), N'') IS NOT NULL
               AND NULLIF(LTRIM(RTRIM(pp.[LastName])), N'') IS NOT NULL
               AND NULLIF(LTRIM(RTRIM(pp.[Gender])), N'') IS NOT NULL
               AND NULLIF(LTRIM(RTRIM(pp.[MobileCountryCode])), N'') IS NOT NULL
               AND NULLIF(LTRIM(RTRIM(pp.[MobileNumber])), N'') IS NOT NULL
               AND pp.[DateOfBirth] > '00010101'
               AND NULLIF(LTRIM(RTRIM(pp.[AddressLine])), N'') IS NOT NULL
               AND NULLIF(LTRIM(RTRIM(pp.[ZipCode])), N'') IS NOT NULL
               AND NULLIF(LTRIM(RTRIM(pp.[City])), N'') IS NOT NULL
               AND pp.[Latitude] IS NOT NULL AND pp.[Longitude] IS NOT NULL
               AND (pp.[Latitude] <> 0 OR pp.[Longitude] <> 0)
               THEN 1 ELSE 0 END AS BIT) AS [IsBasicInfoComplete]
    FROM [Parent].[PetParents] AS pp
    INNER JOIN [Parent].[ParentAuthIdentities] AS pai
        ON pai.[ParentAuthIdentityId] = pp.[ParentAuthIdentityId]
    WHERE pp.[PetParentId] = @PetParentId AND pp.[IsDeleted] = 0;

    -- Result set 2: pets summary. Each row carries a server-computed flag
    -- for whether the two required medical fields are populated.
    -- MedicalHistory and Temperament are intentionally NOT part of the
    -- completion check — both are optional (Temperament can be left empty).
    SELECT [PetId],
           [PetName],
           CASE
               WHEN NULLIF(LTRIM(RTRIM([VaccinationStatus])), N'') IS NOT NULL
                AND NULLIF(LTRIM(RTRIM([SterilizationStatus])), N'') IS NOT NULL
               THEN CAST(1 AS BIT)
               ELSE CAST(0 AS BIT)
           END AS [IsMedicalInfoComplete],
           CAST(CASE
                    WHEN [ProfilePhotoUrl] IS NULL OR LTRIM(RTRIM([ProfilePhotoUrl])) = N'' THEN 0
                    ELSE 1
                END AS BIT) AS [HasProfilePhoto],
           CAST(CASE WHEN
               NULLIF(LTRIM(RTRIM([PetName])), N'') IS NOT NULL
               AND NULLIF(LTRIM(RTRIM([PetType])), N'') IS NOT NULL
               AND NULLIF(LTRIM(RTRIM([Breed])), N'') IS NOT NULL
               AND NULLIF(LTRIM(RTRIM([Gender])), N'') IS NOT NULL
               AND [DateOfBirth] > '00010101'
               THEN 1 ELSE 0 END AS BIT) AS [IsDetailsComplete]
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
