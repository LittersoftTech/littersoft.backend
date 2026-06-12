-- Removes the parent's single identity row (one per parent — UNIQUE
-- PetParentId). Returns the deleted row's IdentityType + photo URL so the
-- API can best-effort delete the blob afterwards.
-- THROW 51209 = pet parent identity not found (identity delete).
CREATE OR ALTER PROCEDURE [Parent].[DeletePetParentIdentity]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ParentIdentityId UNIQUEIDENTIFIER;
    DECLARE @IdentityType NVARCHAR(32);
    DECLARE @IdentityPhotoUrl NVARCHAR(1000);

    BEGIN TRANSACTION;

    SELECT @ParentIdentityId = [ParentIdentityId],
           @IdentityType = [IdentityType],
           @IdentityPhotoUrl = [IdentityPhotoUrl]
    FROM [Parent].[ParentIdentities] WITH (UPDLOCK, HOLDLOCK)
    WHERE [PetParentId] = @PetParentId;

    IF @ParentIdentityId IS NULL
    BEGIN
        THROW 51209, 'Pet parent identity was not found.', 1;
    END

    DELETE FROM [Parent].[ParentIdentities]
    WHERE [ParentIdentityId] = @ParentIdentityId;

    SELECT @ParentIdentityId AS [ParentIdentityId],
           @PetParentId AS [PetParentId],
           @IdentityType AS [IdentityType],
           @IdentityPhotoUrl AS [IdentityPhotoUrl],
           SYSUTCDATETIME() AS [DeletedAtUtc];

    COMMIT TRANSACTION;
END;
