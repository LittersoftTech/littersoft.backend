-- Reads the parent's single identity row (one per parent — UNIQUE
-- PetParentId), including the document's blob URL. Returns zero or one row;
-- the caller maps an empty result to 404 ParentIdentityNotFound.
CREATE OR ALTER PROCEDURE [Parent].[GetPetParentIdentity]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ParentIdentityId],
           [PetParentId],
           [IdentityType],
           [IdentityPhotoUrl],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[ParentIdentities]
    WHERE [PetParentId] = @PetParentId;
END;
