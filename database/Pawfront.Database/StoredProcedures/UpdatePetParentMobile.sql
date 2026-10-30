CREATE OR ALTER PROCEDURE [Parent].[UpdatePetParentMobile]
    @PetParentId UNIQUEIDENTIFIER,
    @MobileCountryCode NVARCHAR(8),
    @MobileNumber NVARCHAR(32)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;
    IF EXISTS (SELECT 1 FROM [Parent].[PetParents] WITH (UPDLOCK, HOLDLOCK)
               WHERE [PetParentId] = @PetParentId AND [IsDeleted] = 1)
        THROW 51224, 'This account has been deleted and can no longer be edited.', 1;

    IF NOT EXISTS (SELECT 1 FROM [Parent].[PetParents] WITH (UPDLOCK, HOLDLOCK)
                   WHERE [PetParentId] = @PetParentId)
        THROW 51208, 'Pet parent was not found.', 1;

    IF EXISTS (SELECT 1 FROM [Parent].[PetParents]
               WHERE [MobileCountryCode] = @MobileCountryCode AND [MobileNumber] = @MobileNumber
                 AND [PetParentId] <> @PetParentId)
        THROW 51222, 'Mobile number is already registered.', 1;

    IF EXISTS (SELECT 1 FROM [Parent].[PetParents]
               WHERE [PetParentId] = @PetParentId
                 AND ([MobileCountryCode] <> @MobileCountryCode OR [MobileNumber] <> @MobileNumber))
    BEGIN
        UPDATE [Parent].[PetParents]
        SET [MobileCountryCode] = @MobileCountryCode,
            [MobileNumber] = @MobileNumber,
            [MobileVerifiedAtUtc] = NULL,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [PetParentId] = @PetParentId;

        UPDATE [Parent].[ParentMobileOtps]
        SET [ValidationStatus] = N'Expired', [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [PetParentId] = @PetParentId AND [ValidationStatus] <> N'Expired';
    END

    SELECT p.[PetParentId], p.[FirstName], p.[LastName], p.[Gender],
           a.[Email], a.[IsEmailVerified], p.[MobileCountryCode], p.[MobileNumber],
           p.[DateOfBirth], p.[AddressLine], p.[Latitude], p.[Longitude],
           p.[ZipCode], p.[City], p.[Description], p.[ProfilePhotoUrl],
           p.[MobileVerifiedAtUtc], p.[CreatedAtUtc], p.[UpdatedAtUtc]
    FROM [Parent].[PetParents] p
    INNER JOIN [Parent].[ParentAuthIdentities] a
        ON a.[ParentAuthIdentityId] = p.[ParentAuthIdentityId]
    WHERE p.[PetParentId] = @PetParentId;
    COMMIT TRANSACTION;
END;
