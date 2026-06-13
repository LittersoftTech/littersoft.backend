-- Edits the basic-profile subset: name, gender, birth date, address fields,
-- description. Deliberately untouched: mobile number (changes must go back
-- through OTP verification), latitude/longitude (no coordinates accompany
-- an address edit today), profile photo (own endpoint).
-- THROW 51208 = pet parent not found (profile update).
CREATE OR ALTER PROCEDURE [Parent].[UpdatePetParentProfile]
    @PetParentId UNIQUEIDENTIFIER,
    @FirstName NVARCHAR(100),
    @LastName NVARCHAR(100),
    @Gender NVARCHAR(32),
    @DateOfBirth DATE,
    @AddressLine NVARCHAR(500),
    @ZipCode NVARCHAR(16),
    @City NVARCHAR(100),
    @Description NVARCHAR(2000)
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE [Parent].[PetParents]
    SET [FirstName] = @FirstName,
        [LastName] = @LastName,
        [Gender] = @Gender,
        [DateOfBirth] = @DateOfBirth,
        [AddressLine] = @AddressLine,
        [ZipCode] = @ZipCode,
        [City] = @City,
        [Description] = @Description,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [PetParentId] = @PetParentId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51208, 'Pet parent was not found.', 1;
    END

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
