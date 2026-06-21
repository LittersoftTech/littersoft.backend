-- Removes a single photo from a pet's gallery. Scoped by BOTH PetId and
-- PetPhotoId so a photo can only be deleted via the pet it belongs to.
-- Returns the deleted row's URL so the API can best-effort delete the blob
-- afterwards. THROW 51215 = pet photo not found (pet photo delete).
CREATE OR ALTER PROCEDURE [Parent].[DeletePetPhoto]
    @PetId UNIQUEIDENTIFIER,
    @PetPhotoId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @PhotoUrl NVARCHAR(1000);

    BEGIN TRANSACTION;

    SELECT @PhotoUrl = [PhotoUrl]
    FROM [Parent].[PetPhotos] WITH (UPDLOCK, HOLDLOCK)
    WHERE [PetPhotoId] = @PetPhotoId
      AND [PetId] = @PetId;

    IF @PhotoUrl IS NULL
    BEGIN
        THROW 51215, 'Pet photo was not found.', 1;
    END

    DELETE FROM [Parent].[PetPhotos]
    WHERE [PetPhotoId] = @PetPhotoId;

    SELECT @PetPhotoId AS [PetPhotoId],
           @PetId AS [PetId],
           @PhotoUrl AS [PhotoUrl],
           SYSUTCDATETIME() AS [DeletedAtUtc];

    COMMIT TRANSACTION;
END;
