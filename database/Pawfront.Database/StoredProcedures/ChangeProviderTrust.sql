CREATE OR ALTER PROCEDURE [Provider].[ChangeProviderTrust]
    @ProviderId UNIQUEIDENTIFIER,
    @Action NVARCHAR(32),
    @DocumentUrl NVARCHAR(1000)=NULL,
    @RevisionId UNIQUEIDENTIFIER=NULL,
    @Reason NVARCHAR(2000)=NULL,
    @Reference NVARCHAR(1000)=NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRY
        BEGIN TRANSACTION;
        DECLARE @Category NVARCHAR(64);
        -- Serialize all writes for a provider, including first upload vs first review.
        SELECT @Category=r.ServiceCategory FROM Provider.Providers p WITH (UPDLOCK,HOLDLOCK)
        JOIN Provider.ProviderServiceRegistrations r ON r.ProviderId=p.ProviderId
        WHERE p.ProviderId=@ProviderId AND p.IsDeleted=0;
        IF @Category IS NULL THROW 51520,'Provider registration was not found.',1;
        IF @Action=N'SyncOffering'
        BEGIN
            IF @Category NOT IN (N'PetSitter',N'PetGroomer',N'PetTrainer')
                THROW 51521,'Only optional offering documents can be cleared.',1;
            -- Preserve a review/approval when the same document is saved again.
            -- Removing or replacing evidence invalidates the old badge immediately.
            IF NULLIF(LTRIM(RTRIM(@DocumentUrl)),N'') IS NULL
                DELETE Provider.ProviderTrust WHERE ProviderId=@ProviderId;
            ELSE IF NOT EXISTS (SELECT 1 FROM Provider.ProviderTrust WHERE ProviderId=@ProviderId
                                AND ServiceCategory=@Category AND DocumentUrl=@DocumentUrl)
            BEGIN
                DELETE Provider.ProviderTrust WHERE ProviderId=@ProviderId;
                INSERT Provider.ProviderTrust VALUES(@ProviderId,NEWID(),@Category,N'Document',N'InReview',@DocumentUrl,NULL,NULL,SYSUTCDATETIME());
            END
        END
        ELSE IF @Action=N'Upload'
        BEGIN
            IF @Category NOT IN (N'PetSitter',N'PetGroomer',N'PetTrainer',N'Vet') OR NULLIF(@DocumentUrl,N'') IS NULL
                THROW 51521,'Trust documents are only available for sitters, groomers, trainers and vets.',1;
            DELETE Provider.ProviderTrust WHERE ProviderId=@ProviderId;
            INSERT Provider.ProviderTrust VALUES(@ProviderId,NEWID(),@Category,N'Document',N'InReview',@DocumentUrl,NULL,NULL,SYSUTCDATETIME());
        END
        ELSE IF @Action IN (N'Accept',N'Reject')
        BEGIN
            IF @Action=N'Reject' AND NULLIF(LTRIM(RTRIM(@Reason)),N'') IS NULL
                THROW 51521,'Rejection requires a reason.',1;
            UPDATE Provider.ProviderTrust SET Status=CASE WHEN @Action=N'Accept' THEN N'Added' ELSE N'NotApproved' END,
                RejectionReason=CASE WHEN @Action=N'Reject' THEN @Reason ELSE NULL END,UpdatedAtUtc=SYSUTCDATETIME()
            WHERE ProviderId=@ProviderId AND RevisionId=@RevisionId AND Status=N'InReview'
                AND EvidenceKind=N'Document' AND ServiceCategory=@Category AND (@Category<>N'Vet' OR @Action=N'Reject');
            IF @@ROWCOUNT<>1 THROW 51522,'Document changed or is no longer in review.',1;
        END
        ELSE IF @Action IN (N'MedRegListed',N'MedRegNotListed')
        BEGIN
            IF @Category<>N'Vet' OR NULLIF(LTRIM(RTRIM(@Reference)),N'') IS NULL
                THROW 51521,'MedReg results require a vet and the lookup reference.',1;
            UPDATE Provider.ProviderTrust SET EvidenceKind=N'MedReg',
                Status=CASE WHEN @Action=N'MedRegListed' THEN N'Added' ELSE N'NotApproved' END,
                RejectionReason=CASE WHEN @Action=N'MedRegNotListed' THEN N'Not listed in MedReg' ELSE NULL END,
                MedRegReference=@Reference,UpdatedAtUtc=SYSUTCDATETIME()
            WHERE ProviderId=@ProviderId AND RevisionId=@RevisionId AND ServiceCategory=N'Vet'
                AND DocumentUrl IS NOT NULL AND (Status=N'InReview' OR @Action=N'MedRegNotListed');
            IF @@ROWCOUNT<>1 THROW 51522,'Vet document changed, is missing, or is no longer in review.',1;
        END
        ELSE THROW 51521,'Invalid trust action.',1;
        SELECT * FROM Provider.ProviderTrust WHERE ProviderId=@ProviderId;
        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
