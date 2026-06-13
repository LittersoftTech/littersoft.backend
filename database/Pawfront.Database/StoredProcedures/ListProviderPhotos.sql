-- Returns every gallery photo on file for the given provider, oldest-first so
-- the mobile gallery renders in upload order. Empty when the provider has no
-- photos (or doesn't exist) — the application returns [] rather than 404.
CREATE OR ALTER PROCEDURE [Provider].[ListProviderPhotos]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ProviderPhotoId],
           [ProviderId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Provider].[ProviderPhotos]
    WHERE [ProviderId] = @ProviderId
    ORDER BY [CreatedAtUtc] ASC;
END;
