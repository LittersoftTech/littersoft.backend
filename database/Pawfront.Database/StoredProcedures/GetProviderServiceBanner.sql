CREATE OR ALTER PROCEDURE [Provider].[GetProviderServiceBanner]
    @ServiceId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [ServiceId], [ProviderId], [BannerImageUrl], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Provider].[ProviderServiceBanners]
    WHERE [ServiceId] = @ServiceId;
END;
