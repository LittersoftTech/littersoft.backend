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
    -- for whether the three required medical fields are populated.
    -- MedicalHistory is intentionally NOT part of the completion check —
    -- it's a free-text field that the spec marked optional.
    SELECT [PetId],
           [PetName],
           CASE
               WHEN [VaccinationStatus]   IS NOT NULL
                AND [SterilizationStatus] IS NOT NULL
                AND [Temperament]         IS NOT NULL
               THEN CAST(1 AS BIT)
               ELSE CAST(0 AS BIT)
           END AS [IsMedicalInfoComplete]
    FROM [Parent].[Pets]
    WHERE [PetParentId] = @PetParentId
    ORDER BY [CreatedAtUtc] ASC;

    -- Result set 3: identity. Zero rows = no identity uploaded yet (stage
    -- Remaining); one row = uploaded (stage Complete) with the
    -- IdentityType the parent declared.
    SELECT [IdentityType]
    FROM [Parent].[ParentIdentities]
    WHERE [PetParentId] = @PetParentId;
END;
