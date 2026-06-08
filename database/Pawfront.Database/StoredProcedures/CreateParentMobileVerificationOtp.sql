CREATE OR ALTER PROCEDURE [Parent].[CreateMobileVerificationOtp]
    @PetParentId UNIQUEIDENTIFIER,
    @OtpCode NVARCHAR(10)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ParentMobileOtpId UNIQUEIDENTIFIER = NEWID();
    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @MobileCountryCode NVARCHAR(8);
    DECLARE @MobileNumber NVARCHAR(32);

    SELECT @MobileCountryCode = [MobileCountryCode],
           @MobileNumber = [MobileNumber]
    FROM [Parent].[PetParents]
    WHERE [PetParentId] = @PetParentId;

    IF @MobileNumber IS NULL
    BEGIN
        THROW 51210, 'Pet parent profile was not found.', 1;
    END

    INSERT INTO [Parent].[ParentMobileOtps]
    (
        [ParentMobileOtpId],
        [PetParentId],
        [MobileCountryCode],
        [MobileNumber],
        [OtpCodeHash],
        [OtpCodeLastTwo],
        [DateSentUtc],
        [ExpiresAtUtc],
        [CreatedAtUtc],
        [UpdatedAtUtc]
    )
    VALUES
    (
        @ParentMobileOtpId,
        @PetParentId,
        @MobileCountryCode,
        @MobileNumber,
        HASHBYTES('SHA2_256', CONVERT(NVARCHAR(36), @ParentMobileOtpId) + N':' + @OtpCode),
        RIGHT(@OtpCode, 2),
        @Now,
        DATEADD(MINUTE, 10, @Now),
        @Now,
        @Now
    );

    SELECT [ParentMobileOtpId],
           [PetParentId],
           [MobileCountryCode],
           [MobileNumber],
           [DateSentUtc],
           [ExpiresAtUtc]
    FROM [Parent].[ParentMobileOtps]
    WHERE [ParentMobileOtpId] = @ParentMobileOtpId;
END;
