-- Sets a pet's single primary/profile photo URL (distinct from the photo
-- gallery in [Parent].[PetPhotos]). The blob upload happens at the endpoint
-- layer; this sproc only persists the resulting URL. Mirrors
-- [Parent].[UpdatePetParentProfilePhoto].
CREATE OR ALTER PROCEDURE [Parent].[UpdatePetProfilePhoto]
    @PetId UNIQUEIDENTIFIER,
    @ProfilePhotoUrl NVARCHAR(1000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    UPDATE [Parent].[Pets]
    SET [ProfilePhotoUrl] = @ProfilePhotoUrl,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [PetId] = @PetId;

    IF @@ROWCOUNT = 0
    BEGIN
        THROW 51220, 'Pet was not found.', 1;
    END

    SELECT [PetId],
           [ProfilePhotoUrl],
           [UpdatedAtUtc]
    FROM [Parent].[Pets]
    WHERE [PetId] = @PetId;
END;
