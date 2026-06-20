-- Permanently removes a pet. Photo rows cascade via FK_PetPhotos_Pets_PetId
-- (ON DELETE CASCADE); the blobs themselves are left for a future sweep.
-- Bookings that referenced the pet are detached (PetId set null) so the
-- FK_Bookings_Pets_PetId constraint doesn't block the delete — the booking
-- rows keep their denormalised snapshots and remain meaningful.
-- THROW 51214 = pet not found (pet delete).
CREATE OR ALTER PROCEDURE [Parent].[DeletePetParentPet]
    @PetId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @PetParentId UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @PetParentId = [PetParentId]
    FROM [Parent].[Pets] WITH (UPDLOCK, HOLDLOCK)
    WHERE [PetId] = @PetId;

    IF @PetParentId IS NULL
    BEGIN
        THROW 51214, 'Pet was not found.', 1;
    END

    -- Detach historical bookings (keep the rows + their snapshots).
    UPDATE [Booking].[Bookings]
    SET [PetId] = NULL
    WHERE [PetId] = @PetId;

    -- Photo rows cascade-delete with the pet.
    DELETE FROM [Parent].[Pets]
    WHERE [PetId] = @PetId;

    SELECT @PetId AS [PetId],
           @PetParentId AS [PetParentId],
           SYSUTCDATETIME() AS [DeletedAtUtc];

    COMMIT TRANSACTION;
END;
