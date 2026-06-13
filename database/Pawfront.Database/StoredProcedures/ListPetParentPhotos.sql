-- Returns every gallery photo on file for the given parent, oldest-first so
-- the mobile gallery renders in upload order. Empty when the parent has no
-- photos (or doesn't exist) — the application returns [] rather than 404.
CREATE OR ALTER PROCEDURE [Parent].[ListPetParentPhotos]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [PetParentPhotoId],
           [PetParentId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Parent].[PetParentPhotos]
    WHERE [PetParentId] = @PetParentId
    ORDER BY [CreatedAtUtc] ASC;
END;
