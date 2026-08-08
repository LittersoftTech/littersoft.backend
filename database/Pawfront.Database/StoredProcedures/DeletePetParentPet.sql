-- "Delete pet" is an ANONYMISE + HIDE, not a row delete — the same shape as the
-- two account deletes ([Parent].[DeletePetParent] / [Provider].[DeleteProvider]),
-- and for the same reason: the PetId is referenced by history that is not only
-- the parent's.
--
-- [Booking].[Bookings].[PetId] points at this row and [Booking].[GetBookingDetail]
-- reads the whole petDetails block through that join. The previous version
-- deleted the row and NULLed PetId on every booking that referenced it, which
-- silently blanked the pet out of the PROVIDER's record of a job they actually
-- performed — they were left with a booking whose pet no longer existed.
--
-- What goes: everything that identifies the animal — name, microchip, photo,
-- description, and the free-text medical fields. MicrochipId is cleared rather
-- than replaced because it is a real-world ISO 11784/11785 identifier under a
-- UNIQUE index; clearing it frees the chip so the animal can be registered again.
-- What stays: the facts that keep a past booking meaningful — type, breed,
-- gender, date of birth, weight, and the vaccination / sterilization / temperament
-- statuses.
--
-- [IsDeleted] then hides the pet from every parent-facing read, from the
-- ownership filter behind /pets/{petId}/*, from onboarding status, and from
-- booking creation. Booking reads deliberately do NOT filter on it — that is the
-- history the row is being kept for.
--
-- Photo rows are removed outright (gallery media, no historical value) along with
-- the forward-looking next-consultation reminders. The blobs themselves are left
-- for a future sweep, unchanged from the previous behaviour.
--
-- Idempotent: a second call on an already-deleted pet returns the original
-- DeletedAtUtc with WasAlreadyDeleted = 1 rather than re-scrubbing.
--
-- THROW 51214 = pet not found (pet delete).
CREATE OR ALTER PROCEDURE [Parent].[DeletePetParentPet]
    @PetId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @Exists BIT = 0;
    DECLARE @IsDeleted BIT;
    DECLARE @DeletedAtUtc DATETIME2(7);
    DECLARE @WasAlreadyDeleted BIT = 0;

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK: serialise against a concurrent booking create naming
    -- this pet, so the create either sees a live pet or is rejected by the
    -- IsDeleted guard — never reads "live" while this scrub is committing.
    SELECT @Exists = 1,
           @PetParentId = [PetParentId],
           @IsDeleted = [IsDeleted],
           @DeletedAtUtc = [DeletedAtUtc]
    FROM [Parent].[Pets] WITH (UPDLOCK, HOLDLOCK)
    WHERE [PetId] = @PetId;

    -- XACT_ABORT ON rolls the transaction back on THROW.
    IF @Exists = 0
    BEGIN
        THROW 51214, 'Pet was not found.', 1;
    END

    IF @IsDeleted = 1
    BEGIN
        SET @WasAlreadyDeleted = 1;
        SET @Now = ISNULL(@DeletedAtUtc, @Now);
    END
    ELSE
    BEGIN
        UPDATE [Parent].[Pets]
        SET [PetName] = N'Deleted Pet',
            [MicrochipId] = NULL,
            [Description] = NULL,
            [MedicalHistory] = NULL,
            [VaccinationType] = NULL,
            [VaccinationDose] = NULL,
            [Prescription] = NULL,
            [ProfilePhotoUrl] = NULL,
            [IsDeleted] = 1,
            [DeletedAtUtc] = @Now,
            [UpdatedAtUtc] = @Now
        WHERE [PetId] = @PetId;

        DELETE FROM [Parent].[PetPhotos] WHERE [PetId] = @PetId;
        DELETE FROM [Parent].[PetNextConsultations] WHERE [PetId] = @PetId;
    END

    SELECT @PetId AS [PetId],
           @PetParentId AS [PetParentId],
           @Now AS [DeletedAtUtc],
           @WasAlreadyDeleted AS [WasAlreadyDeleted];

    COMMIT TRANSACTION;
END;
