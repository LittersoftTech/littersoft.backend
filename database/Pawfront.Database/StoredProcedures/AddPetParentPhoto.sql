-- Inserts one row into [Parent].[PetParentPhotos] for a freshly-uploaded photo.
-- A parent can have many photos, so each call inserts a new row.
-- THROW 51212 = pet parent not found (parent photo add).
CREATE OR ALTER PROCEDURE [Parent].[AddPetParentPhoto]
    @PetParentId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51212, 'Pet parent was not found.', 1;
    END

    DECLARE @InsertedId TABLE ([PetParentPhotoId] UNIQUEIDENTIFIER);

    INSERT INTO [Parent].[PetParentPhotos]
    (
        [PetParentId],
        [PhotoUrl]
    )
    OUTPUT inserted.[PetParentPhotoId] INTO @InsertedId
    VALUES
    (
        @PetParentId,
        @PhotoUrl
    );

    DECLARE @PetParentPhotoId UNIQUEIDENTIFIER = (SELECT TOP (1) [PetParentPhotoId] FROM @InsertedId);

    SELECT [PetParentPhotoId],
           [PetParentId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Parent].[PetParentPhotos]
    WHERE [PetParentPhotoId] = @PetParentPhotoId;

    COMMIT TRANSACTION;
END;
