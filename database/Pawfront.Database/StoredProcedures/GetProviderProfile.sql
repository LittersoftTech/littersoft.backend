CREATE OR ALTER PROCEDURE [Provider].[GetProviderProfile]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Returns the provider's persisted personal information. Same row shape as the
    -- [Provider].[CompleteProviderProfile] result set, so the ADO.NET reader code
    -- can be reused.
    SELECT [ProviderId],
           [ProviderAuthIdentityId],
           [FirstName],
           [LastName],
           [Gender],
           [MobileCountryCode],
           [MobileNumber],
           [DateOfBirth],
           [MobileVerifiedAtUtc],
           [OnboardingStatus],
           [IsActive],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;
END;
