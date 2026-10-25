CREATE OR ALTER PROCEDURE [Parent].[AddPetPhoto]
    @PetId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[Pets] WITH (UPDLOCK, HOLDLOCK)
        WHERE [PetId] = @PetId
    )
    BEGIN
        THROW 51204, 'Pet was not found.', 1;
    END

    IF (SELECT COUNT(*) FROM [Parent].[PetPhotos] WHERE [PetId] = @PetId) >= 12
        THROW 51420, 'A pet can have at most 12 photos.', 1;

    DECLARE @InsertedPetPhotoId TABLE ([PetPhotoId] UNIQUEIDENTIFIER);

    INSERT INTO [Parent].[PetPhotos]
    (
        [PetId],
        [PhotoUrl]
    )
    OUTPUT inserted.[PetPhotoId] INTO @InsertedPetPhotoId
    VALUES
    (
        @PetId,
        @PhotoUrl
    );

    DECLARE @PetPhotoId UNIQUEIDENTIFIER = (SELECT TOP (1) [PetPhotoId] FROM @InsertedPetPhotoId);

    SELECT [PetPhotoId],
           [PetId],
           [PhotoUrl],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[PetPhotos]
    WHERE [PetPhotoId] = @PetPhotoId;

    COMMIT TRANSACTION;
END;
