-- Run only against a dedicated test database after Deployment/DeployAll.sql.
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Parent UNIQUEIDENTIFIER=NEWID(), @Auth UNIQUEIDENTIFIER=NEWID(), @Identity UNIQUEIDENTIFIER,
    @Version DATETIME2(7), @Stale DATETIME2(7), @Count INT;
BEGIN TRY
    INSERT Parent.ParentAuthIdentities(ParentAuthIdentityId,FirebaseUserId,AuthProvider,Email)
    VALUES(@Auth,CONVERT(NVARCHAR(36),@Auth),N'EmailPassword',CONCAT(@Auth,N'@identity.test.invalid'));
    INSERT Parent.PetParents(PetParentId,ParentAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth,AddressLine,Latitude,Longitude,ZipCode,City,Description)
    VALUES(@Parent,@Auth,N'Identity',N'Test',N'Other',N'+41',LEFT(CONVERT(NVARCHAR(36),@Parent),16),'1990-01-01',N'Test',47,8,N'8000',N'Zurich',N'Test');
    EXEC Parent.UpsertPetParentIdentity @Parent,N'Other',N'https://test.invalid/first';
    SELECT @Identity=ParentIdentityId, @Version=UpdatedAtUtc FROM Parent.ParentIdentities WHERE PetParentId=@Parent;
    IF NOT EXISTS(SELECT 1 FROM Parent.ParentIdentities WHERE ParentIdentityId=@Identity AND Status=N'InReview' AND RejectionReason IS NULL)
        THROW 51900,'Upload must start in review with no reason.',1;
    EXEC Parent.ReviewPetParentIdentity @Identity,@Version,N'Added';
    IF NOT EXISTS(SELECT 1 FROM Parent.ParentIdentities WHERE ParentIdentityId=@Identity AND Status=N'Added')
        THROW 51900,'Accept did not add badge.',1;
    IF (SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE RecipientId=@Parent AND EntityId=@Identity AND NotificationType=N'IDENTITY_ADDED')<>1
        THROW 51900,'Accept must queue one identity notification.',1;
    BEGIN TRY
        EXEC Parent.ReviewPetParentIdentity @Identity,@Version,N'Added';
        THROW 51900,'Repeated acceptance must conflict.',1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER()<>51210 THROW;
    END CATCH;
    EXEC Parent.UpsertPetParentIdentity @Parent,N'Passport',N'https://test.invalid/replacement';
    IF (SELECT COUNT(*) FROM Parent.ParentIdentities WHERE PetParentId=@Parent)<>1
        THROW 51900,'Replacement must preserve one document per parent.',1;
    IF NOT EXISTS(SELECT 1 FROM Parent.ParentIdentities WHERE ParentIdentityId=@Identity AND Status=N'InReview' AND RejectionReason IS NULL)
        THROW 51900,'Replacement must remove badge and clear reason.',1;
    SELECT @Version=UpdatedAtUtc FROM Parent.ParentIdentities WHERE ParentIdentityId=@Identity;
    SET @Stale=DATEADD(SECOND,-1,@Version);
    BEGIN TRY
        EXEC Parent.ReviewPetParentIdentity @Identity,@Stale,N'Added';
        THROW 51900,'Stale review must conflict.',1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER()<>51210 THROW;
    END CATCH;
    BEGIN TRY
        EXEC Parent.ReviewPetParentIdentity @Identity,@Version,N'NotApproved',N' ';
        THROW 51900,'Reject requires a reason.',1;
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER()<>51211 THROW;
    END CATCH;
    EXEC Parent.ReviewPetParentIdentity @Identity,@Version,N'NotApproved',N'The photo is blurry.';
    IF NOT EXISTS(SELECT 1 FROM Parent.ParentIdentities WHERE ParentIdentityId=@Identity AND Status=N'NotApproved' AND RejectionReason=N'The photo is blurry.')
        THROW 51900,'Reject must save reason without a badge.',1;
    IF (SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE RecipientId=@Parent AND EntityId=@Identity AND NotificationType=N'IDENTITY_NOT_APPROVED')<>1
        THROW 51900,'Reject must queue one identity notification.',1;
    EXEC Parent.GetPetParentIdentity @Parent;
    EXEC Parent.UpsertPetParentIdentity @Parent,N'Other',N'https://test.invalid/reupload';
    IF NOT EXISTS(SELECT 1 FROM Parent.ParentIdentities WHERE ParentIdentityId=@Identity AND Status=N'InReview' AND RejectionReason IS NULL)
        THROW 51900,'Re-upload after rejection must clear the reason.',1;
    SELECT @Version=UpdatedAtUtc FROM Parent.ParentIdentities WHERE ParentIdentityId=@Identity;
    EXEC Parent.ReviewPetParentIdentity @Identity,@Version,N'Added';
    IF (SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE RecipientId=@Parent AND EntityId=@Identity AND NotificationType=N'IDENTITY_ADDED')<>2
        THROW 51900,'New review cycle needs a new notification.',1;
    EXEC Parent.DeletePetParentIdentity @Parent;
    IF EXISTS(SELECT 1 FROM Parent.ParentIdentities WHERE PetParentId=@Parent)
        THROW 51900,'Deleting an accepted ID must remove the badge.',1;
    EXEC Parent.UpsertPetParentIdentity @Parent,N'Other',N'https://test.invalid/pending';
    EXEC Parent.DeletePetParentIdentity @Parent;
    IF EXISTS(SELECT 1 FROM Parent.ParentIdentities WHERE PetParentId=@Parent)
        THROW 51900,'Deleting an ID in review must work.',1;
    DELETE Notification.NotificationOutbox WHERE RecipientId=@Parent;
    DELETE Parent.PetParents WHERE PetParentId=@Parent;
    DELETE Parent.ParentAuthIdentities WHERE ParentAuthIdentityId=@Auth;
    PRINT 'Passed identity SQL lifecycle, replacement, delete, concurrency and notification checks.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK;
    DELETE Notification.NotificationOutbox WHERE RecipientId=@Parent;
    DELETE Parent.PetParents WHERE PetParentId=@Parent;
    DELETE Parent.ParentAuthIdentities WHERE ParentAuthIdentityId=@Auth;
    THROW;
END CATCH;
