CREATE OR ALTER PROCEDURE [Provider].[SaveProviderServiceBanner]
    @ServiceId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
    @BannerImageUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    -- The ServiceId must belong to the provider and be active. UPDLOCK + HOLDLOCK
    -- serialises against a concurrent service deactivation.
    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[ProviderServices] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [ProviderId] = @ProviderId
          AND [IsActive] = 1
    )
    BEGIN
        THROW 51081, 'Service is not valid or active for this provider.', 1;
    END

    UPDATE [Provider].[ProviderServiceBanners]
    SET [BannerImageUrl] = @BannerImageUrl,
        [ProviderId] = @ProviderId,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [ServiceId] = @ServiceId;

    IF @@ROWCOUNT = 0
    BEGIN
        INSERT INTO [Provider].[ProviderServiceBanners]
            ([ServiceId], [ProviderId], [BannerImageUrl])
        VALUES (@ServiceId, @ProviderId, @BannerImageUrl);
    END

    SELECT [ServiceId], [ProviderId], [BannerImageUrl], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Provider].[ProviderServiceBanners]
    WHERE [ServiceId] = @ServiceId;

    COMMIT TRANSACTION;
END;
