-- Run against a test database containing the deployed procedures.
-- Uses a new provider and rolls back all fixture data, on success or failure.
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @ProviderId UNIQUEIDENTIFIER = NEWID();
    DECLARE @AuthId UNIQUEIDENTIFIER = NEWID();
    DECLARE @Key NVARCHAR(32) = REPLACE(CONVERT(NVARCHAR(36), @ProviderId), N'-', N'');
    INSERT INTO [Provider].[ProviderAuthIdentities]
        ([ProviderAuthIdentityId], [FirebaseUserId], [AuthProvider], [Email])
    VALUES (@AuthId, @Key, N'EmailPassword', @Key + N'@example.invalid');
    INSERT INTO [Provider].[Providers]
        ([ProviderId], [ProviderAuthIdentityId], [FirstName], [LastName], [Gender],
         [MobileCountryCode], [MobileNumber], [DateOfBirth])
    VALUES (@ProviderId, @AuthId, N'Status', N'Check', N'Other', N'+41', @Key, '19900101');

    DECLARE @Online BIT = 1;
    DECLARE @Pass INT = 0;
    WHILE @Pass < 2
    BEGIN
        EXEC [Provider].[SetProviderActiveStatus] @ProviderId = @ProviderId, @IsActive = @Online;
        EXEC [Provider].[UpsertProviderService] @ProviderId = @ProviderId,
            @ServiceCategory = N'PetSitter', @SubCategory = N'PetHotel', @ServiceType = N'DayCare';
        IF NOT EXISTS (SELECT 1 FROM [Provider].[Providers] WHERE [ProviderId] = @ProviderId AND [IsActive] = @Online)
            THROW 52000, 'Activating a service changed business status.', 1;

        EXEC [Provider].[DeactivateProviderService] @ProviderId = @ProviderId, @ServiceType = N'DayCare';
        IF EXISTS (SELECT 1 FROM [Provider].[ProviderServices] WHERE [ProviderId] = @ProviderId AND [IsActive] = 1)
            THROW 52001, 'Expected no active services.', 1;
        IF NOT EXISTS (SELECT 1 FROM [Provider].[Providers] WHERE [ProviderId] = @ProviderId AND [IsActive] = @Online)
            THROW 52002, 'Deactivating the last service changed business status.', 1;

        EXEC [Provider].[UpsertProviderService] @ProviderId = @ProviderId,
            @ServiceCategory = N'PetSitter', @SubCategory = N'PetHotel', @ServiceType = N'DayCare';
        IF NOT EXISTS (SELECT 1 FROM [Provider].[Providers] WHERE [ProviderId] = @ProviderId AND [IsActive] = @Online)
            THROW 52003, 'Reactivating a service changed business status.', 1;
        SET @Online = 0;
        SET @Pass += 1;
    END;
    ROLLBACK TRANSACTION;
    PRINT 'Passed independent business/service status database checks.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
