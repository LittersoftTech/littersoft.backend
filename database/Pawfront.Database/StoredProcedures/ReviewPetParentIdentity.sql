CREATE OR ALTER PROCEDURE [Parent].[ReviewPetParentIdentity]
    @ParentIdentityId UNIQUEIDENTIFIER,
    @ExpectedUpdatedAtUtc DATETIME2(7),
    @Status NVARCHAR(16),
    @RejectionReason NVARCHAR(2000) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @Status NOT IN (N'Added', N'NotApproved') OR @Status IS NULL
        THROW 51211, 'Invalid identity review decision.', 1;
    IF @Status = N'NotApproved' AND NULLIF(LTRIM(RTRIM(@RejectionReason)), N'') IS NULL
        THROW 51211, 'Rejection reason is required.', 1;
    IF @Status = N'Added' SET @RejectionReason = NULL;
    BEGIN TRY
    BEGIN TRANSACTION;
    DECLARE @ParentId UNIQUEIDENTIFIER;
    SELECT @ParentId = PetParentId FROM Parent.ParentIdentities WITH (UPDLOCK, HOLDLOCK)
    WHERE ParentIdentityId = @ParentIdentityId AND Status = N'InReview' AND UpdatedAtUtc = @ExpectedUpdatedAtUtc;
    IF @ParentId IS NULL THROW 51210, 'Identity changed or is no longer in review.', 1;
    UPDATE Parent.ParentIdentities SET Status = @Status, RejectionReason = @RejectionReason,
        UpdatedAtUtc = SYSUTCDATETIME() WHERE ParentIdentityId = @ParentIdentityId;
    DECLARE @Type NVARCHAR(64) = CASE WHEN @Status = N'Added' THEN N'IDENTITY_ADDED' ELSE N'IDENTITY_NOT_APPROVED' END;
    EXEC Notification.EnqueueNotification @Audience=N'PetParent', @RecipientId=@ParentId,
        @NotificationType=@Type, @EntityType=N'Identity', @EntityId=@ParentIdentityId, @SuppressResultSet=1;
    COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;
END;
