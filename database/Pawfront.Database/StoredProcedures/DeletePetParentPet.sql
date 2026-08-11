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
-- The scrub is REFUSED while this PET still has unfinished jobs — any booking
-- (single-day or night-stay) naming it that is neither finished nor cancelled: a
-- request the provider hasn't answered, a confirmed job still to come, one
-- underway, or one with an open modification proposal. Mirror of the same rule in
-- [Parent].[DeletePetParent], for the same reason: a provider is holding a slot
-- for this animal, or has it in their care right now, and anonymising it out from
-- under them is not something the parent can undo. The caller is handed the list
-- to settle first (the API answers 409 PendingJobsExist). Deciding it HERE rather
-- than in the app layer is what makes it race-safe — the check runs inside the
-- same transaction that already holds UPDLOCK + HOLDLOCK on the pet row, which is
-- the row a concurrent [Booking].[CreateBooking] must read.
--
-- Idempotent: a second call on an already-deleted pet returns the original
-- DeletedAtUtc with WasAlreadyDeleted = 1 rather than re-scrubbing, and skips the
-- pending-job check (there is nothing left to refuse).
--
-- Returns TWO result sets:
--   1. summary — PetId, PetParentId, DeletedAtUtc, WasAlreadyDeleted, plus
--                BlockedByPendingJobs
--   2. pending jobs — empty unless BlockedByPendingJobs = 1, in which case NOTHING
--      was scrubbed and result set 1 describes an untouched pet.
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
    DECLARE @BlockedByPendingJobs BIT = 0;

    -- Unfinished jobs blocking the delete. Same shape as the account delete's
    -- third result set, so both refusals hand the caller the identical payload —
    -- the last four columns are pricing inputs the caller turns into a money
    -- block, since SQL cannot reach the Cosmos offering.
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
        [PetName] NVARCHAR(100) NULL,
        [ServiceId] UNIQUEIDENTIFIER NOT NULL,
        [ServiceItemCode] NVARCHAR(64) NULL,
        [CheckOutDate] DATE NULL,
        [SnapshotUnitPrice] DECIMAL(10, 2) NULL
    );

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
        ------------------------------------------------------------------
        -- 0. Refuse while this pet has unfinished jobs.
        --    "Unfinished" is the complement of the terminal set, so a job that
        --    is done (COMPLETED / PAID) or dead (cancelled / declined / no-show
        --    / expired / OTP-cancelled) never blocks. Kept in step with
        --    Pawfront.Application's BookingStatuses.Terminal AND with the same
        --    list in [Parent].[DeletePetParent] — change one, change both.
        --
        --    Note a CREATED booking already past its expiry rule still reads
        --    CREATED until the sweep job flips it (every 5 minutes), so it can
        --    block for that long. Self-correcting, and blocking briefly is the
        --    safe direction.
        ------------------------------------------------------------------
        INSERT INTO @PendingJobs
            ([BookingId], [BookingType], [JobId], [ProviderId], [ProviderName],
             [ServiceCategory], [SubCategory], [Status], [ServiceDate],
             [StartTime], [EndTime], [PetName], [ServiceId], [ServiceItemCode],
             [CheckOutDate], [SnapshotUnitPrice])
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
               pet.[PetName],
               b.[ServiceId],
               b.[ServiceItemCode],
               NULL,
               b.[PricePerHour]
        FROM [Booking].[Bookings] AS b
        LEFT JOIN [Provider].[Providers] AS pr ON pr.[ProviderId] = b.[ProviderId]
        LEFT JOIN [Parent].[Pets] AS pet ON pet.[PetId] = b.[PetId]
        WHERE b.[PetId] = @PetId
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
               pet.[PetName],
               n.[ServiceId],
               NULL,
               n.[CheckOutDate],
               n.[PricePerNight]
        FROM [Booking].[NightStayBookings] AS n
        LEFT JOIN [Provider].[Providers] AS pr ON pr.[ProviderId] = n.[ProviderId]
        LEFT JOIN [Parent].[Pets] AS pet ON pet.[PetId] = n.[PetId]
        WHERE n.[PetId] = @PetId
          AND n.[Status] NOT IN (
                N'COMPLETED', N'PAID', N'PROVIDER_DECLINED', N'PROVIDER_CANCELLED',
                N'PARENT_CANCELLED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED');

        IF EXISTS (SELECT 1 FROM @PendingJobs)
        BEGIN
            SET @BlockedByPendingJobs = 1;
        END
    END

    -- Only scrub when the pet is live AND unblocked.
    IF @IsDeleted = 0 AND @BlockedByPendingJobs = 0
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

    -- Result set 1: summary. When BlockedByPendingJobs = 1 the pet is UNTOUCHED
    -- and [DeletedAtUtc] carries @Now only to keep the column non-nullable for the
    -- reader; it is never surfaced in that case.
    SELECT @PetId AS [PetId],
           @PetParentId AS [PetParentId],
           @Now AS [DeletedAtUtc],
           @WasAlreadyDeleted AS [WasAlreadyDeleted],
           @BlockedByPendingJobs AS [BlockedByPendingJobs];

    -- Result set 2: the unfinished jobs that refused the delete. Empty on the
    -- normal path. Ordered soonest-first — the parent has to deal with the next
    -- one before anything else.
    SELECT [BookingId], [BookingType], [JobId], [ProviderId], [ProviderName],
           [ServiceCategory], [SubCategory], [Status], [ServiceDate],
           [StartTime], [EndTime], [PetName], [ServiceId], [ServiceItemCode],
           [CheckOutDate], [SnapshotUnitPrice]
    FROM @PendingJobs
    ORDER BY [ServiceDate] ASC, [StartTime] ASC;

    COMMIT TRANSACTION;
END;
