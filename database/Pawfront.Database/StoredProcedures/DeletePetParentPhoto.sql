-- Removes a single gallery photo. Scoped by BOTH PetParentId and
-- PetParentPhotoId so a parent can only delete their own photos. Returns the
-- deleted row's URL so the API can best-effort delete the blob afterwards.
-- THROW 51213 = pet parent photo not found (parent photo delete).
CREATE OR ALTER PROCEDURE [Parent].[DeletePetParentPhoto]
    @PetParentId UNIQUEIDENTIFIER,
    @PetParentPhotoId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @PhotoUrl NVARCHAR(1000);

    BEGIN TRANSACTION;

    SELECT @PhotoUrl = [PhotoUrl]
    FROM [Parent].[PetParentPhotos] WITH (UPDLOCK, HOLDLOCK)
    WHERE [PetParentPhotoId] = @PetParentPhotoId
      AND [PetParentId] = @PetParentId;

    IF @PhotoUrl IS NULL
    BEGIN
        THROW 51213, 'Pet parent photo was not found.', 1;
    END

    DELETE FROM [Parent].[PetParentPhotos]
    WHERE [PetParentPhotoId] = @PetParentPhotoId;

    SELECT @PetParentPhotoId AS [PetParentPhotoId],
           @PetParentId AS [PetParentId],
           @PhotoUrl AS [PhotoUrl],
           SYSUTCDATETIME() AS [DeletedAtUtc];

    COMMIT TRANSACTION;
END;
