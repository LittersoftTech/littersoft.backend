CREATE OR ALTER PROCEDURE [Parent].[UpdatePetParentProfilePhoto]
    @PetParentId UNIQUEIDENTIFIER,
    @ProfilePhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE [Parent].[PetParents]
    SET [ProfilePhotoUrl] = @ProfilePhotoUrl,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [PetParentId] = @PetParentId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51201, 'Pet parent was not found.', 1;
    END

    SELECT [PetParentId],
           [ProfilePhotoUrl],
           [UpdatedAtUtc]
    FROM [Parent].[PetParents]
    WHERE [PetParentId] = @PetParentId;
END;
