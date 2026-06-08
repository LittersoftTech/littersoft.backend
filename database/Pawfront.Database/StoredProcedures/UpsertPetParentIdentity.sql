CREATE OR ALTER PROCEDURE [Parent].[UpsertPetParentIdentity]
    @PetParentId UNIQUEIDENTIFIER,
    @IdentityType NVARCHAR(32),
    @IdentityPhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51206, 'Pet parent was not found.', 1;
    END

    -- One identity per parent (UNIQUE constraint). Re-uploading replaces
    -- the previous row's IdentityType + photo URL. The blob in storage
    -- from the previous upload becomes orphaned — same future-cleanup
    -- story as the other photo endpoints.
    IF EXISTS (
        SELECT 1
        FROM [Parent].[ParentIdentities] WITH (UPDLOCK, HOLDLOCK)
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        UPDATE [Parent].[ParentIdentities]
        SET [IdentityType] = @IdentityType,
            [IdentityPhotoUrl] = @IdentityPhotoUrl,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [PetParentId] = @PetParentId;
    END
    ELSE
    BEGIN
        INSERT INTO [Parent].[ParentIdentities]
        (
            [PetParentId],
            [IdentityType],
            [IdentityPhotoUrl]
        )
        VALUES
        (
            @PetParentId,
            @IdentityType,
            @IdentityPhotoUrl
        );
    END

    SELECT [ParentIdentityId],
           [PetParentId],
           [IdentityType],
           [IdentityPhotoUrl],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[ParentIdentities]
    WHERE [PetParentId] = @PetParentId;

    COMMIT TRANSACTION;
END;
