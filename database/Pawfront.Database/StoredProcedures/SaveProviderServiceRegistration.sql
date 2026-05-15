CREATE OR ALTER PROCEDURE [Provider].[SaveProviderServiceRegistration]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceCategory NVARCHAR(64),
    @SubCategory NVARCHAR(64),
    @Latitude DECIMAL(9, 6),
    @Longitude DECIMAL(9, 6)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ExistingId UNIQUEIDENTIFIER;
    DECLARE @ExistingCategory NVARCHAR(64);

    BEGIN TRANSACTION;

    -- A provider can have at most one registration. Look up by ProviderId alone.
    SELECT @ExistingId = [ProviderServiceRegistrationId],
           @ExistingCategory = [ServiceCategory]
    FROM [Provider].[ProviderServiceRegistrations] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId;

    IF @ExistingId IS NULL
    BEGIN
        IF NOT EXISTS (
            SELECT 1
            FROM [Provider].[Providers]
            WHERE [ProviderId] = @ProviderId
        )
        BEGIN
            THROW 51010, 'Provider profile was not found.', 1;
        END

        INSERT INTO [Provider].[ProviderServiceRegistrations]
        (
            [ProviderId],
            [ServiceCategory],
            [SubCategory],
            [Latitude],
            [Longitude]
        )
        VALUES
        (
            @ProviderId,
            @ServiceCategory,
            @SubCategory,
            @Latitude,
            @Longitude
        );
    END
    ELSE IF @ExistingCategory <> @ServiceCategory
    BEGIN
        -- Provider is already registered under a different category. Reject (409).
        DECLARE @ConflictMessage NVARCHAR(400) =
            N'Provider is already registered under ''' + @ExistingCategory +
            N''' and cannot register under ''' + @ServiceCategory + N'''.';
        THROW 51011, @ConflictMessage, 1;
    END
    ELSE
    BEGIN
        -- Same category — refresh the sub-category, lat/lng.
        UPDATE [Provider].[ProviderServiceRegistrations]
        SET [SubCategory] = @SubCategory,
            [Latitude] = @Latitude,
            [Longitude] = @Longitude,
            [UpdatedAtUtc] = @Now
        WHERE [ProviderServiceRegistrationId] = @ExistingId;
    END

    SELECT [ProviderServiceRegistrationId],
           [ProviderId],
           [ServiceCategory],
           [SubCategory],
           [Latitude],
           [Longitude],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Provider].[ProviderServiceRegistrations]
    WHERE [ProviderId] = @ProviderId;

    COMMIT TRANSACTION;
END;
