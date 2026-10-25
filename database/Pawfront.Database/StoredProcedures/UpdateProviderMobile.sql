CREATE OR ALTER PROCEDURE [Provider].[UpdateProviderMobile]
    @ProviderId UNIQUEIDENTIFIER,
    @MobileCountryCode NVARCHAR(8),
    @MobileNumber NVARCHAR(32)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;
    IF EXISTS (SELECT 1 FROM [Provider].[Providers] WITH (UPDLOCK, HOLDLOCK)
               WHERE [ProviderId] = @ProviderId AND [IsDeleted] = 1)
        THROW 51115, 'This provider account has been deleted.', 1;

    IF NOT EXISTS (SELECT 1 FROM [Provider].[Providers] WITH (UPDLOCK, HOLDLOCK)
                   WHERE [ProviderId] = @ProviderId)
        THROW 51113, 'Provider profile was not found.', 1;

    IF EXISTS (SELECT 1 FROM [Provider].[Providers]
               WHERE [MobileCountryCode] = @MobileCountryCode AND [MobileNumber] = @MobileNumber
                 AND [ProviderId] <> @ProviderId AND [IsDeleted] = 0)
        THROW 51116, 'Mobile number is already registered.', 1;

    IF EXISTS (SELECT 1 FROM [Provider].[Providers]
               WHERE [ProviderId] = @ProviderId
                 AND ([MobileCountryCode] <> @MobileCountryCode OR [MobileNumber] <> @MobileNumber))
    BEGIN
        UPDATE [Provider].[Providers]
        SET [MobileCountryCode] = @MobileCountryCode,
            [MobileNumber] = @MobileNumber,
            [MobileVerifiedAtUtc] = NULL,
            [OnboardingStatus] = N'MobileVerificationPending',
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ProviderId] = @ProviderId;

        UPDATE [Provider].[ProviderMobileOtps]
        SET [ValidationStatus] = N'Expired', [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ProviderId] = @ProviderId AND [ValidationStatus] <> N'Expired';
    END

    SELECT [ProviderId], [ProviderAuthIdentityId], [FirstName], [LastName],
           [Gender], [MobileCountryCode], [MobileNumber], [DateOfBirth],
           [MobileVerifiedAtUtc], [OnboardingStatus], [IsActive],
           [CreatedAtUtc], [UpdatedAtUtc], [BannerImageUrl]
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;
    COMMIT TRANSACTION;
END;
