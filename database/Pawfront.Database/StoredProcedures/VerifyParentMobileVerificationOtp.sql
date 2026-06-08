CREATE OR ALTER PROCEDURE [Parent].[VerifyMobileVerificationOtp]
    @PetParentId UNIQUEIDENTIFIER,
    @ParentMobileOtpId UNIQUEIDENTIFIER,
    @OtpCode NVARCHAR(10)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @OtpCodeHash VARBINARY(32);
    DECLARE @ValidationStatus NVARCHAR(32);
    DECLARE @DateSentUtc DATETIME2(7);
    DECLARE @DateValidatedUtc DATETIME2(7);
    DECLARE @ExpiresAtUtc DATETIME2(7);
    DECLARE @ResponseStatus NVARCHAR(32);
    DECLARE @IsValidated BIT = 0;

    BEGIN TRANSACTION;

    SELECT @OtpCodeHash = [OtpCodeHash],
           @ValidationStatus = [ValidationStatus],
           @DateSentUtc = [DateSentUtc],
           @DateValidatedUtc = [DateValidatedUtc],
           @ExpiresAtUtc = [ExpiresAtUtc]
    FROM [Parent].[ParentMobileOtps] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ParentMobileOtpId] = @ParentMobileOtpId
      AND [PetParentId] = @PetParentId;

    IF @OtpCodeHash IS NULL
    BEGIN
        THROW 51211, 'Pet parent mobile OTP entry was not found.', 1;
    END

    IF @ValidationStatus = N'Validated'
    BEGIN
        SET @IsValidated = 1;
        SET @ResponseStatus = N'Validated';
    END
    ELSE IF @ValidationStatus = N'Expired' OR @Now >= @ExpiresAtUtc
    BEGIN
        UPDATE [Parent].[ParentMobileOtps]
        SET [ValidationStatus] = N'Expired',
            [UpdatedAtUtc] = @Now
        WHERE [ParentMobileOtpId] = @ParentMobileOtpId;

        SET @ResponseStatus = N'Expired';
    END
    ELSE IF @OtpCodeHash = HASHBYTES('SHA2_256', CONVERT(NVARCHAR(36), @ParentMobileOtpId) + N':' + @OtpCode)
    BEGIN
        UPDATE [Parent].[ParentMobileOtps]
        SET [ValidationStatus] = N'Validated',
            [DateValidatedUtc] = @Now,
            [UpdatedAtUtc] = @Now
        WHERE [ParentMobileOtpId] = @ParentMobileOtpId;

        UPDATE [Parent].[PetParents]
        SET [MobileVerifiedAtUtc] = COALESCE([MobileVerifiedAtUtc], @Now),
            [UpdatedAtUtc] = @Now
        WHERE [PetParentId] = @PetParentId;

        SET @IsValidated = 1;
        SET @ResponseStatus = N'Validated';
        SET @DateValidatedUtc = @Now;
    END
    ELSE
    BEGIN
        UPDATE [Parent].[ParentMobileOtps]
        SET [FailedAttemptCount] = [FailedAttemptCount] + 1,
            [UpdatedAtUtc] = @Now
        WHERE [ParentMobileOtpId] = @ParentMobileOtpId;

        SET @ResponseStatus = N'Invalid';
    END

    SELECT [ParentMobileOtpId],
           [PetParentId],
           @IsValidated AS [IsValidated],
           @ResponseStatus AS [ValidationStatus],
           [DateSentUtc],
           [DateValidatedUtc],
           [ExpiresAtUtc]
    FROM [Parent].[ParentMobileOtps]
    WHERE [ParentMobileOtpId] = @ParentMobileOtpId;

    COMMIT TRANSACTION;
END;
