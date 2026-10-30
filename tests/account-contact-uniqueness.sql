-- Run against a disposable database after DeployAll.sql. Uses synthetic accounts.
SET NOCOUNT ON;
DECLARE @Suffix nvarchar(36)=CONVERT(nvarchar(36),NEWID());
DECLARE @Email nvarchar(320)=CONCAT(@Suffix,N'@test.invalid'),
        @Uid nvarchar(128)=CONCAT(N'contact-',@Suffix),
        @OtherUid nvarchar(128)=CONCAT(N'other-',@Suffix),
        @OtherEmail nvarchar(320)=CONCAT(N'other-',@Suffix,N'@test.invalid'),
        @Phone nvarchar(32)=LEFT(@Suffix,32);
-- Even identical Firebase UID strings belong to independent projects.
EXEC [Parent].[SaveParentAuthIdentity] @FirebaseUserId=@Uid,
    @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
-- Repeat authentication must remain idempotent.
EXEC [Parent].[SaveParentAuthIdentity] @FirebaseUserId=@Uid,
    @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@Uid,
    @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
-- Repeat authentication must remain idempotent.
EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@Uid,
    @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
DECLARE @ProviderAuth uniqueidentifier=(SELECT ProviderAuthIdentityId FROM [Provider].[ProviderAuthIdentities] WHERE FirebaseUserId=@Uid);
EXEC [Provider].[CompleteProviderProfile] @ProviderAuth,N'Test',N'Provider',N'Other',N'+41',@Phone,'1990-01-01';
EXEC [Parent].[CompletePetParentProfile] @Uid,N'Test',N'Parent',N'Other',N'+41',@Phone,'1990-01-01',N'Address',47,8,N'8000',N'Zurich',N'Test';
PRINT 'PASS parent and provider share email, mobile and project-local UID';
BEGIN TRY
    EXEC [Parent].[SaveParentAuthIdentity] @FirebaseUserId=@OtherUid,
        @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
    THROW 50000, 'Same-type duplicate email was accepted.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF ERROR_NUMBER() <> 51981 THROW;
END CATCH;
EXEC [Parent].[SaveParentAuthIdentity] @FirebaseUserId=@OtherUid,
    @AuthProvider=N'EmailPassword', @Email=@OtherEmail, @IsEmailVerified=1;
-- Email updates must enforce the same rule as inserts.
BEGIN TRY
    EXEC [Parent].[SaveParentAuthIdentity] @FirebaseUserId=@OtherUid,
        @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
    THROW 50000, 'Same-type duplicate email update was accepted.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF ERROR_NUMBER() <> 51981 THROW;
END CATCH;
PRINT 'PASS parent duplicate email insert and update rejected';
BEGIN TRY
    EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@OtherUid,
        @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
    THROW 50000, 'Same-type duplicate email was accepted.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF ERROR_NUMBER() <> 51980 THROW;
END CATCH;
EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@OtherUid,
    @AuthProvider=N'EmailPassword', @Email=@OtherEmail, @IsEmailVerified=1;
-- Email updates must enforce the same rule as inserts.
BEGIN TRY
    EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@OtherUid,
        @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
    THROW 50000, 'Same-type duplicate email update was accepted.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF ERROR_NUMBER() <> 51980 THROW;
END CATCH;
PRINT 'PASS provider duplicate email insert and update rejected';
DECLARE @OtherProviderAuth uniqueidentifier=(SELECT ProviderAuthIdentityId FROM [Provider].[ProviderAuthIdentities] WHERE FirebaseUserId=@OtherUid);
BEGIN TRY
    EXEC [Provider].[CompleteProviderProfile] @OtherProviderAuth,N'Test',N'Provider',N'Other',N'+41',@Phone,'1990-01-01';
    THROW 50000, 'Same-type duplicate provider mobile was accepted.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF ERROR_NUMBER() NOT IN (2601,2627) THROW;
    IF ERROR_MESSAGE() NOT LIKE '%UX_Providers_MobileNumber%' THROW;
END CATCH;
BEGIN TRY
    EXEC [Parent].[CompletePetParentProfile] @OtherUid,N'Test',N'Parent',N'Other',N'+41',@Phone,'1990-01-01',N'Address',47,8,N'8000',N'Zurich',N'Test';
    THROW 50000, 'Same-type duplicate parent mobile was accepted.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF ERROR_NUMBER() <> 51222 THROW;
END CATCH;
PRINT 'PASS same-type duplicate mobiles rejected';
