CREATE OR ALTER PROCEDURE [Provider].[UpdateProviderBannerImage]
    @ProviderId UNIQUEIDENTIFIER,
    @BannerImageUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- Provider-level banner (one per provider, overwritten on re-upload). The
    -- per-service banner lives in [Provider].[ProviderServiceBanners] and is a
    -- separate image.
    UPDATE [Provider].[Providers]
    SET [BannerImageUrl] = @BannerImageUrl,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [ProviderId] = @ProviderId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51112, 'Provider was not found.', 1;
    END

    SELECT [ProviderId],
           [BannerImageUrl],
           [UpdatedAtUtc]
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;
END;
