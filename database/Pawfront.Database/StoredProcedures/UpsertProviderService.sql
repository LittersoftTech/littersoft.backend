CREATE OR ALTER PROCEDURE [Provider].[UpsertProviderService]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceCategory NVARCHAR(64),
    @SubCategory NVARCHAR(64),
    @ServiceType NVARCHAR(64),
    @ServiceId UNIQUEIDENTIFIER = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    BEGIN TRANSACTION;

    -- A provider must exist before we can pin a service to them.
    IF NOT EXISTS (
        SELECT 1 FROM [Provider].[Providers] WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51080, 'Provider profile was not found.', 1;
    END

    SELECT @ServiceId = [ServiceId]
    FROM [Provider].[ProviderServices] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId AND [ServiceType] = @ServiceType;

    IF @ServiceId IS NULL
    BEGIN
        SET @ServiceId = NEWID();

        INSERT INTO [Provider].[ProviderServices]
            ([ServiceId], [ProviderId], [ServiceCategory], [SubCategory], [ServiceType], [IsActive])
        VALUES
            (@ServiceId, @ProviderId, @ServiceCategory, @SubCategory, @ServiceType, 1);
    END
    ELSE
    BEGIN
        -- Re-activate + refresh SubCategory in case the provider switched
        -- sub-categories (e.g. PetHotel → FreelancePetSitter is blocked by
        -- the registration UNIQUE, but SubCategory naming may vary).
        UPDATE [Provider].[ProviderServices]
        SET [ServiceCategory] = @ServiceCategory,
            [SubCategory]     = @SubCategory,
            [IsActive]        = 1,
            [UpdatedAtUtc]    = @Now
        WHERE [ServiceId] = @ServiceId;
    END

    SELECT [ServiceId], [ProviderId], [ServiceCategory], [SubCategory],
           [ServiceType], [IsActive], [CreatedAtUtc], [UpdatedAtUtc]
    FROM [Provider].[ProviderServices]
    WHERE [ServiceId] = @ServiceId;

    COMMIT TRANSACTION;
END;
