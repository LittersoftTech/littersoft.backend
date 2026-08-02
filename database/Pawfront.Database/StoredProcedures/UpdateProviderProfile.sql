-- Edits the provider's personal details: first name, last name, gender, date of
-- birth. Deliberately untouched: mobile number + country code (a change must go
-- back through OTP verification, and the pair is UNIQUE), banner image (own
-- endpoint), OnboardingStatus / IsActive (own flows).
-- Mirror of [Parent].[UpdatePetParentProfile] on the pet-parent side.
-- THROW 51113 = provider profile not found (profile update).
-- THROW 51115 = the account has been deleted; editing would undo the
--               anonymisation done by [Provider].[DeleteProvider].
CREATE OR ALTER PROCEDURE [Provider].[UpdateProviderProfile]
    @ProviderId UNIQUEIDENTIFIER,
    @FirstName NVARCHAR(100),
    @LastName NVARCHAR(100),
    @Gender NVARCHAR(32),
    @DateOfBirth DATE
AS
BEGIN
    SET NOCOUNT ON;

    IF EXISTS (SELECT 1 FROM [Provider].[Providers]
               WHERE [ProviderId] = @ProviderId AND [IsDeleted] = 1)
    BEGIN
        THROW 51115, 'This provider account has been deleted.', 1;
    END

    UPDATE [Provider].[Providers]
    SET [FirstName] = @FirstName,
        [LastName] = @LastName,
        [Gender] = @Gender,
        [DateOfBirth] = @DateOfBirth,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [ProviderId] = @ProviderId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51113, 'Provider profile was not found.', 1;
    END

    -- Same row shape as [Provider].[GetProviderProfile] / [CompleteProviderProfile]
    -- so the ADO.NET reader code is reused.
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
           [UpdatedAtUtc],
           [BannerImageUrl]
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;
END;
