-- Inserts one row into [Provider].[ProviderPhotos] for a freshly-uploaded photo.
-- A provider can have many photos, so each call inserts a new row.
-- THROW 51110 = provider not found (provider photo add).
CREATE OR ALTER PROCEDURE [Provider].[AddProviderPhoto]
    @ProviderId UNIQUEIDENTIFIER,
    @PhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[Providers]
        WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51110, 'Provider was not found.', 1;
    END

    DECLARE @InsertedId TABLE ([ProviderPhotoId] UNIQUEIDENTIFIER);

    INSERT INTO [Provider].[ProviderPhotos]
    (
        [ProviderId],
        [PhotoUrl]
    )
    OUTPUT inserted.[ProviderPhotoId] INTO @InsertedId
    VALUES
    (
        @ProviderId,
        @PhotoUrl
    );

    DECLARE @ProviderPhotoId UNIQUEIDENTIFIER = (SELECT TOP (1) [ProviderPhotoId] FROM @InsertedId);

    SELECT [ProviderPhotoId],
           [ProviderId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Provider].[ProviderPhotos]
    WHERE [ProviderPhotoId] = @ProviderPhotoId;

    COMMIT TRANSACTION;
END;
