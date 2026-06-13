-- Removes a single gallery photo. Scoped by BOTH ProviderId and
-- ProviderPhotoId so a provider can only delete their own photos. Returns the
-- deleted row's URL so the API can best-effort delete the blob afterwards.
-- THROW 51111 = provider photo not found (provider photo delete).
CREATE OR ALTER PROCEDURE [Provider].[DeleteProviderPhoto]
    @ProviderId UNIQUEIDENTIFIER,
    @ProviderPhotoId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @PhotoUrl NVARCHAR(1000);

    BEGIN TRANSACTION;

    SELECT @PhotoUrl = [PhotoUrl]
    FROM [Provider].[ProviderPhotos] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderPhotoId] = @ProviderPhotoId
      AND [ProviderId] = @ProviderId;

    IF @PhotoUrl IS NULL
    BEGIN
        THROW 51111, 'Provider photo was not found.', 1;
    END

    DELETE FROM [Provider].[ProviderPhotos]
    WHERE [ProviderPhotoId] = @ProviderPhotoId;

    SELECT @ProviderPhotoId AS [ProviderPhotoId],
           @ProviderId AS [ProviderId],
           @PhotoUrl AS [PhotoUrl],
           SYSUTCDATETIME() AS [DeletedAtUtc];

    COMMIT TRANSACTION;
END;
