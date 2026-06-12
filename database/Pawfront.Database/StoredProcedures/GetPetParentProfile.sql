-- Read-back of the persisted pet-parent profile, joined with the auth
-- identity so the response carries Email + IsEmailVerified without a
-- second round-trip. Empty result set = parent not found (C# maps to 404).
CREATE OR ALTER PROCEDURE [Parent].[GetPetParentProfile]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT p.[PetParentId],
           p.[FirstName],
           p.[LastName],
           p.[Gender],
           a.[Email],
           a.[IsEmailVerified],
           p.[MobileCountryCode],
           p.[MobileNumber],
           p.[DateOfBirth],
           p.[AddressLine],
           p.[Latitude],
           p.[Longitude],
           p.[ZipCode],
           p.[City],
           p.[Description],
           p.[ProfilePhotoUrl],
           p.[MobileVerifiedAtUtc],
           p.[CreatedAtUtc],
           p.[UpdatedAtUtc]
    FROM [Parent].[PetParents] p
    INNER JOIN [Parent].[ParentAuthIdentities] a
        ON a.[ParentAuthIdentityId] = p.[ParentAuthIdentityId]
    WHERE p.[PetParentId] = @PetParentId;
END;
