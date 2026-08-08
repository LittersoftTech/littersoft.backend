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
--   3. Anonymises the parent's PETS rather than deleting them, and marks them
--      IsDeleted (the same flag a per-pet delete sets). Bookings FK to PetId and
--      read the pet's details through that join, so a delete would blank out the
--      provider's own booking history. The pet's identity goes (name, microchip,
--      photo, free-text notes); the animal facts that give a past booking meaning
--      stay (type, breed, gender, DOB, weight, vaccination / sterilization
--      status, temperament). MicrochipId is cleared rather than replaced because
--      it is a real-world identifier and UNIQUE — freeing it lets the animal be
--      registered again under a new account.
--   4. Deletes only what is operational or pure PII and carries no history:
--      device tokens (stop push), mobile OTPs, the identity document, the parent
--      photo gallery, the pets' photo galleries, and the pets' next-consultation
--      reminders (forward-looking, not history).
--
-- The scrub is REFUSED while the parent still has unfinished jobs — any booking
-- (single-day or night-stay) that is neither finished nor cancelled: a request
-- the provider hasn't answered, a confirmed job still to come, one underway, or
-- one with an open modification proposal. Severing the login under a provider
-- who is holding a slot, or who is mid-job, is not something the parent can undo,
-- so the caller is handed the list to settle first (the API answers 409
-- PendingJobsExist). Deciding it HERE rather than in the app layer is what makes
-- it race-safe: the check runs inside the same transaction that already holds
-- UPDLOCK + HOLDLOCK on the parent row, which is the row a concurrent
-- [Booking].[CreateBooking] must read, so a booking landing at the same instant
-- serialises rather than slipping in behind the check.
--
-- All the placeholder values are derived from @PetParentId, so they are stable:
-- re-running on an already-deleted parent is a no-op that returns the original
-- DeletedAtUtc (@WasAlreadyDeleted = 1) instead of churning new values.
--
-- Returns three result sets, since SQL cannot reach Blob Storage:
--   1. summary — PetParentId, DeletedAtUtc, WasAlreadyDeleted, the number of pets
--                anonymised + retained counts (the retained counts document, in
--                the response, that history survived), plus BlockedByPendingJobs
--   2. blob URLs — profile photo, parent gallery, identity document, pet profile
--      photos and pet galleries. Booking evidence and event banners are NOT
--      returned: those belong to records that are being kept.
--   3. pending jobs — empty unless BlockedByPendingJobs = 1, in which case
--      NOTHING was scrubbed and result sets 1 and 2 describe an untouched account.
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
    DECLARE @BlockedByPendingJobs BIT = 0;

    -- Captured before the scrub so the caller can clean Blob Storage.
    DECLARE @BlobUrls TABLE
    (
        [BlobUrl] NVARCHAR(1000) NOT NULL,
        [Kind] NVARCHAR(32) NOT NULL
    );

    -- Unfinished jobs blocking the delete. Populated only when the parent still
    -- has some; the caller surfaces them so they can be cancelled or seen through.
    DECLARE @PendingJobs TABLE
    (
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [BookingType] NVARCHAR(16) NOT NULL,
        [JobId] NVARCHAR(32) NOT NULL,
        [ProviderId] UNIQUEIDENTIFIER NOT NULL,
        [ProviderName] NVARCHAR(201) NULL,
        [ServiceCategory] NVARCHAR(64) NOT NULL,
        [SubCategory] NVARCHAR(64) NOT NULL,
        [Status] NVARCHAR(48) NOT NULL,
        [ServiceDate] DATE NOT NULL,
        [StartTime] TIME(0) NULL,
        [EndTime] TIME(0) NULL,
        [PetName] NVARCHAR(100) NULL
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
        ------------------------------------------------------------------
        -- 0. Refuse while unfinished jobs exist.
        --    "Unfinished" is the complement of the terminal set, so a job that
        --    is done (COMPLETED / PAID) or dead (cancelled / declined / no-show
        --    / expired / OTP-cancelled) never blocks — only one that still has
        --    a provider waiting on it or a service still to be delivered.
        --    Both booking kinds count: a boarding stay is as live as an
        --    appointment. Kept in step with Pawfront.Application's
        --    BookingStatuses.Terminal — change one, change the other.
        --
        --    Note a CREATED booking that is already past its expiry rule still
        --    reads CREATED until the sweep job flips it (every 5 minutes), so it
        --    can block for that long. Self-correcting, and blocking briefly is
        --    the safe direction.
        ------------------------------------------------------------------
        INSERT INTO @PendingJobs
            ([BookingId], [BookingType], [JobId], [ProviderId], [ProviderName],
             [ServiceCategory], [SubCategory], [Status], [ServiceDate],
             [StartTime], [EndTime], [PetName])
        SELECT b.[BookingId],
               N'SingleDay',
               N'PF-' + FORMAT(b.[JobNumber], N'D6'),
               b.[ProviderId],
               NULLIF(LTRIM(RTRIM(ISNULL(pr.[FirstName], N'') + N' ' + ISNULL(pr.[LastName], N''))), N''),
               b.[ServiceCategory],
               b.[SubCategory],
               b.[Status],
               b.[BookingDate],
               b.[StartTime],
               b.[EndTime],
               pet.[PetName]
        FROM [Booking].[Bookings] AS b
        LEFT JOIN [Provider].[Providers] AS pr ON pr.[ProviderId] = b.[ProviderId]
        LEFT JOIN [Parent].[Pets] AS pet ON pet.[PetId] = b.[PetId]
        WHERE b.[PetParentId] = @PetParentId
          AND b.[Status] NOT IN (
                N'COMPLETED', N'PAID', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED',
                N'PARENT_CANCELLED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
        UNION ALL
        SELECT n.[NightStayBookingId],
               N'NightStay',
               N'PF-' + FORMAT(n.[JobNumber], N'D6'),
               n.[ProviderId],
               NULLIF(LTRIM(RTRIM(ISNULL(pr.[FirstName], N'') + N' ' + ISNULL(pr.[LastName], N''))), N''),
               n.[ServiceCategory],
               n.[SubCategory],
               n.[Status],
               n.[CheckInDate],
               n.[DropOffTime],
               n.[PickUpTime],
               pet.[PetName]
        FROM [Booking].[NightStayBookings] AS n
        LEFT JOIN [Provider].[Providers] AS pr ON pr.[ProviderId] = n.[ProviderId]
        LEFT JOIN [Parent].[Pets] AS pet ON pet.[PetId] = n.[PetId]
        WHERE n.[PetParentId] = @PetParentId
          AND n.[Status] NOT IN (
                N'COMPLETED', N'PAID', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED',
                N'PARENT_CANCELLED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED');

        IF EXISTS (SELECT 1 FROM @PendingJobs)
        BEGIN
            SET @BlockedByPendingJobs = 1;
        END
    END

    -- Everything below only runs when the account is live AND unblocked.
    IF @IsDeleted = 0 AND @BlockedByPendingJobs = 0
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
            -- The pets go with the account, so they carry the same flag a
            -- per-pet delete sets. COALESCE keeps the original timestamp on a
            -- pet the parent had already deleted individually.
            [IsDeleted] = 1,
            [DeletedAtUtc] = COALESCE([DeletedAtUtc], @Now),
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
    -- see that history survived the delete. When BlockedByPendingJobs = 1 the
    -- account is UNTOUCHED and every other column here is meaningless — the
    -- caller reads that flag first and goes to result set 3. [DeletedAtUtc]
    -- still carries @Now rather than NULL only to keep the column non-nullable
    -- for the reader; it is never surfaced in that case.
    SELECT @PetParentId AS [PetParentId],
           @Now AS [DeletedAtUtc],
           @WasAlreadyDeleted AS [WasAlreadyDeleted],
           @BlockedByPendingJobs AS [BlockedByPendingJobs],
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

    -- Result set 3: the unfinished jobs that refused the delete. Empty on the
    -- normal path. Ordered soonest-first — the parent has to deal with the next
    -- one before anything else.
    SELECT [BookingId], [BookingType], [JobId], [ProviderId], [ProviderName],
           [ServiceCategory], [SubCategory], [Status], [ServiceDate],
           [StartTime], [EndTime], [PetName]
    FROM @PendingJobs
    ORDER BY [ServiceDate] ASC, [StartTime] ASC;

    COMMIT TRANSACTION;
END;
