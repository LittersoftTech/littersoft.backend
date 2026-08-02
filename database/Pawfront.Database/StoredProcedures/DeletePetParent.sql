-- Backs the pet-parent app's "Delete account" action as an ANONYMISE + DISABLE,
-- not a row delete. Mirror of [Provider].[DeleteProvider]. The PetParentId is
-- deliberately kept so everything that references it keeps its meaning: service
-- bookings, night-stay bookings, their audit / evidence / OTP / modification /
-- prescription children, the events the parent organised, their event ticket
-- bookings, and the [Booking].[BookingPayments] ledger are ALL retained
-- untouched. Deleting them would destroy BOTH parties' history — a provider's
-- past bookings reference this parent too.
--
-- What the sproc does, in one transaction:
--   1. Scrubs the personal fields on [Parent].[PetParents] — name becomes
--      'Deleted User', gender/DOB/mobile/address/about are replaced or cleared —
--      and sets IsDeleted = 1 + DeletedAtUtc. IsDeleted is permanent and blocks
--      profile edits (THROW 51224), since an edit would undo the anonymisation.
--   2. Scrubs the Firebase link on [Parent].[ParentAuthIdentities] so the account
--      can never be signed into again. FirebaseUserId is UNIQUE, so replacing it
--      also FREES the real Firebase uid — signing up again creates a brand-new
--      identity and a brand-new PetParentId. The mobile number on the profile row
--      is replaced for the same reason: (MobileCountryCode, MobileNumber) is
--      UNIQUE, so the real number becomes available for re-registration.
--   3. Anonymises the parent's PETS rather than deleting them. Bookings FK to
--      PetId and read the pet's details through that join, so a delete would
--      blank out the provider's own booking history. The pet's identity goes
--      (name, microchip, photo, free-text notes); the animal facts that give a
--      past booking meaning stay (type, breed, gender, DOB, weight, vaccination /
--      sterilization status, temperament). MicrochipId is cleared rather than
--      replaced because it is a real-world identifier and UNIQUE — freeing it
--      lets the animal be registered again under a new account.
--   4. Deletes only what is operational or pure PII and carries no history:
--      device tokens (stop push), mobile OTPs, the identity document, the parent
--      photo gallery, the pets' photo galleries, and the pets' next-consultation
--      reminders (forward-looking, not history).
--
-- All the placeholder values are derived from @PetParentId, so they are stable:
-- re-running on an already-deleted parent is a no-op that returns the original
-- DeletedAtUtc (@WasAlreadyDeleted = 1) instead of churning new values.
--
-- Returns two result sets, since SQL cannot reach Blob Storage:
--   1. summary — PetParentId, DeletedAtUtc, WasAlreadyDeleted, the number of pets
--                anonymised + retained counts (the retained counts document, in
--                the response, that history survived)
--   2. blob URLs — profile photo, parent gallery, identity document, pet profile
--      photos and pet galleries. Booking evidence and event banners are NOT
--      returned: those belong to records that are being kept.
--
-- THROW 51223 = pet parent not found (account delete).
CREATE OR ALTER PROCEDURE [Parent].[DeletePetParent]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ParentAuthIdentityId UNIQUEIDENTIFIER;
    DECLARE @Exists BIT = 0;
    DECLARE @IsDeleted BIT;
    DECLARE @DeletedAtUtc DATETIME2(7);
    DECLARE @WasAlreadyDeleted BIT = 0;
    DECLARE @AnonymisedPetCount INT = 0;

    -- Captured before the scrub so the caller can clean Blob Storage.
    DECLARE @BlobUrls TABLE
    (
        [BlobUrl] NVARCHAR(1000) NOT NULL,
        [Kind] NVARCHAR(32) NOT NULL
    );

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK: serialise against a concurrent booking create or
    -- profile edit on this parent.
    SELECT @Exists = 1,
           @ParentAuthIdentityId = [ParentAuthIdentityId],
           @IsDeleted = [IsDeleted],
           @DeletedAtUtc = [DeletedAtUtc]
    FROM [Parent].[PetParents] WITH (UPDLOCK, HOLDLOCK)
    WHERE [PetParentId] = @PetParentId;

    -- XACT_ABORT ON rolls the transaction back on THROW.
    IF @Exists = 0
    BEGIN
        THROW 51223, 'Pet parent was not found.', 1;
    END

    IF @IsDeleted = 1
    BEGIN
        -- Idempotent: already anonymised. Report the original timestamp and skip
        -- straight to the result sets (nothing left to clean up out of SQL).
        SET @WasAlreadyDeleted = 1;
        SET @Now = ISNULL(@DeletedAtUtc, @Now);
    END
    ELSE
    BEGIN
        INSERT INTO @BlobUrls ([BlobUrl], [Kind])
        SELECT [ProfilePhotoUrl], N'ParentProfilePhoto'
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId AND [ProfilePhotoUrl] IS NOT NULL
        UNION ALL
        SELECT [PhotoUrl], N'ParentPhoto'
        FROM [Parent].[PetParentPhotos]
        WHERE [PetParentId] = @PetParentId
        UNION ALL
        SELECT [IdentityPhotoUrl], N'ParentIdentity'
        FROM [Parent].[ParentIdentities]
        WHERE [PetParentId] = @PetParentId
        UNION ALL
        SELECT p.[ProfilePhotoUrl], N'PetProfilePhoto'
        FROM [Parent].[Pets] p
        WHERE p.[PetParentId] = @PetParentId AND p.[ProfilePhotoUrl] IS NOT NULL
        UNION ALL
        SELECT ph.[PhotoUrl], N'PetPhoto'
        FROM [Parent].[PetPhotos] ph
        INNER JOIN [Parent].[Pets] p ON p.[PetId] = ph.[PetId]
        WHERE p.[PetParentId] = @PetParentId;

        ------------------------------------------------------------------
        -- 1. Anonymise the profile row and disable the account.
        --    The mobile placeholder is derived from the PetParentId so it stays
        --    unique under UX_PetParents_MobileNumber, and frees the real number.
        ------------------------------------------------------------------
        UPDATE [Parent].[PetParents]
        SET [FirstName] = N'Deleted',
            [LastName] = N'User',
            [Gender] = N'PreferNotToSay',
            [DateOfBirth] = '1900-01-01',
            [MobileCountryCode] = N'+00',
            [MobileNumber] = N'DEL' + LEFT(REPLACE(CONVERT(NVARCHAR(36), @PetParentId), N'-', N''), 29),
            [MobileVerifiedAtUtc] = NULL,
            [AddressLine] = N'Deleted',
            [Latitude] = 0,
            [Longitude] = 0,
            [ZipCode] = N'00000',
            [City] = N'Deleted',
            [Description] = N'',
            [ProfilePhotoUrl] = NULL,
            [IsDeleted] = 1,
            [DeletedAtUtc] = @Now,
            [UpdatedAtUtc] = @Now
        WHERE [PetParentId] = @PetParentId;

        ------------------------------------------------------------------
        -- 2. Sever the Firebase login. FirebaseUserId is UNIQUE, so replacing it
        --    frees the real uid for a fresh sign-up. The PetParentId link is kept
        --    for audit — the row is unreachable by any real Firebase user now.
        ------------------------------------------------------------------
        UPDATE [Parent].[ParentAuthIdentities]
        SET [FirebaseUserId] = N'deleted:' + CONVERT(NVARCHAR(36), @PetParentId),
            [FirebaseTenantId] = NULL,
            [Email] = N'deleted+' + CONVERT(NVARCHAR(36), @PetParentId) + N'@deleted.invalid',
            [IsEmailVerified] = 0,
            [DisplayName] = NULL,
            [FirebasePhoneNumber] = NULL,
            [PhotoUrl] = NULL,
            [UpdatedAtUtc] = @Now
        WHERE [ParentAuthIdentityId] = @ParentAuthIdentityId;

        ------------------------------------------------------------------
        -- 3. Anonymise the pets. The rows stay — bookings FK to PetId and read
        --    the pet through that join — but nothing identifying survives.
        ------------------------------------------------------------------
        UPDATE [Parent].[Pets]
        SET [PetName] = N'Deleted Pet',
            [MicrochipId] = NULL,
            [Description] = NULL,
            [MedicalHistory] = NULL,
            [VaccinationType] = NULL,
            [VaccinationDose] = NULL,
            [Prescription] = NULL,
            [ProfilePhotoUrl] = NULL,
            [UpdatedAtUtc] = @Now
        WHERE [PetParentId] = @PetParentId;
        SET @AnonymisedPetCount = @@ROWCOUNT;

        ------------------------------------------------------------------
        -- 4. Remove operational data + media only. Nothing here carries
        --    historical meaning: each booking already froze the parent details
        --    and address it was created with.
        ------------------------------------------------------------------
        DELETE FROM [Parent].[ParentDeviceTokens]
        WHERE [PetParentId] = @PetParentId
           OR [ParentAuthIdentityId] = @ParentAuthIdentityId;

        DELETE FROM [Parent].[ParentMobileOtps] WHERE [PetParentId] = @PetParentId;
        DELETE FROM [Parent].[ParentIdentities] WHERE [PetParentId] = @PetParentId;
        DELETE FROM [Parent].[PetParentPhotos] WHERE [PetParentId] = @PetParentId;

        DELETE ph
        FROM [Parent].[PetPhotos] ph
        INNER JOIN [Parent].[Pets] p ON p.[PetId] = ph.[PetId]
        WHERE p.[PetParentId] = @PetParentId;

        DELETE nc
        FROM [Parent].[PetNextConsultations] nc
        INNER JOIN [Parent].[Pets] p ON p.[PetId] = nc.[PetId]
        WHERE p.[PetParentId] = @PetParentId;
    END

    -- Result set 1: summary. The retained counts are reported so the caller can
    -- see that history survived the delete.
    SELECT @PetParentId AS [PetParentId],
           @Now AS [DeletedAtUtc],
           @WasAlreadyDeleted AS [WasAlreadyDeleted],
           @AnonymisedPetCount AS [AnonymisedPetCount],
           (SELECT COUNT(*) FROM [Booking].[Bookings] WHERE [PetParentId] = @PetParentId)
               AS [RetainedBookingCount],
           (SELECT COUNT(*) FROM [Booking].[NightStayBookings] WHERE [PetParentId] = @PetParentId)
               AS [RetainedNightStayBookingCount],
           (SELECT COUNT(*) FROM [Event].[Events] WHERE [PetParentId] = @PetParentId)
               AS [RetainedEventCount],
           (SELECT COUNT(*) FROM [Booking].[BookingPayments] WHERE [PetParentId] = @PetParentId)
               AS [RetainedPaymentCount];

    -- Result set 2: blob URLs to delete best-effort.
    SELECT [BlobUrl], [Kind] FROM @BlobUrls;

    COMMIT TRANSACTION;
END;
