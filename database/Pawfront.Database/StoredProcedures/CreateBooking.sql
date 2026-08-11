CREATE OR ALTER PROCEDURE [Booking].[CreateBooking]
    @ProviderId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @PetId UNIQUEIDENTIFIER = NULL,
    @ServiceId UNIQUEIDENTIFIER,
    @ServiceCategory NVARCHAR(64),
    @SubCategory NVARCHAR(64),
    @ServiceItemCode NVARCHAR(64) = NULL,
    @BookingDate DATE,
    @StartTime TIME(0),
    @EndTime TIME(0),
    -- Free-text notes the parent attaches to the job at booking time (e.g.
    -- access instructions, the pet's quirks). Optional; surfaced on the
    -- booking-detail read. Stored on App rows too (not a Custom-only column).
    @JobNotes NVARCHAR(2000) = NULL,
    -- Where the service is delivered, as chosen by the parent at booking time:
    -- 'ParentLocation' or 'ProviderLocation'. Optional (NULL for provider-host
    -- and legacy callers); the booking-detail read resolves the address live.
    @LocationType NVARCHAR(32) = NULL,
    -- Snapshot of the offering's unit rate at booking time (per-hour for DayCare,
    -- the flat fee for Vet/Trainer/grooming). Locks the price in so a later rate
    -- change by the provider never re-prices this booking. NULL only for legacy
    -- callers that don't pass it (the detail read falls back to the live rate).
    @PricePerHour DECIMAL(10, 2) = NULL,
    -- Provider business address, resolved by the caller (Cosmos service doc +
    -- registration coordinates) and passed in so a ProviderLocation booking can
    -- snapshot the "where the service happens" address. Ignored for ParentLocation
    -- (the parent's address is snapshotted from SQL) and when no location is set.
    @SnapshotProviderAddressLine NVARCHAR(500) = NULL,
    @SnapshotProviderCity NVARCHAR(200) = NULL,
    @SnapshotProviderZipCode NVARCHAR(32) = NULL,
    @SnapshotProviderLatitude DECIMAL(9, 6) = NULL,
    @SnapshotProviderLongitude DECIMAL(9, 6) = NULL,
    @Capacity INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @ProviderIsActive BIT;
    SELECT @ProviderIsActive = [IsActive]
    FROM [Provider].[Providers] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId;

    IF @ProviderIsActive IS NULL
    BEGIN
        THROW 51061, 'Provider was not found.', 1;
    END

    -- Master Active/Inactive switch — when the provider has flipped themselves
    -- inactive, NO new bookings are accepted on ANY of their services. The
    -- UPDLOCK + HOLDLOCK above serialises us against a concurrent
    -- SetProviderActiveStatus call, so the check is race-safe.
    IF @ProviderIsActive = 0
    BEGIN
        THROW 51067, 'Provider is currently inactive and is not accepting new bookings.', 1;
    END

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51060, 'Pet parent was not found.', 1;
    END

    -- Defense-in-depth: the API validates pet ownership before calling, but a
    -- direct sproc caller must not be able to pin someone else's pet on a booking.
    IF @PetId IS NOT NULL AND NOT EXISTS (
        SELECT 1
        FROM [Parent].[Pets]
        WHERE [PetId] = @PetId
          AND [PetParentId] = @PetParentId
          -- A soft-deleted pet can't be booked. The row survives only to keep
          -- EXISTING bookings readable; it is not a pet the parent still has.
          AND [IsDeleted] = 0
    )
    BEGIN
        THROW 51068, 'Pet was not found or does not belong to the pet parent.', 1;
    END

    -- Validate that the ServiceId belongs to the provider and is active.
    -- UPDLOCK + HOLDLOCK serialises us against concurrent DeactivateProviderService
    -- so a service can't disappear between our check and the insert.
    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[ProviderServices] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [ProviderId] = @ProviderId
          AND [IsActive] = 1
    )
    BEGIN
        THROW 51066, 'Service is not valid or active for this provider.', 1;
    END

    -- Reject a duplicate booking for the same pet: if this pet already has an
    -- active (non-cancelled) booking on THIS service overlapping the requested
    -- window, block it — a pet can't be in two places for the same slot. The
    -- client can't fully prevent this (two devices, races), so it's enforced here
    -- under the SAME UPDLOCK + HOLDLOCK range as the capacity count below (fully
    -- race-safe: a concurrent duplicate serialises behind us and then sees our row).
    -- Only applies to App bookings that name a pet; Custom walk-ins carry no @PetId.
    -- The existing booking's window ends at COALESCE([ActualEndTime], [EndTime]):
    -- once its job has finished early the pet is demonstrably free again, so it
    -- must not block a fresh booking in the time that was released.
    IF @PetId IS NOT NULL AND EXISTS (
        SELECT 1
        FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [PetId] = @PetId
          AND [BookingDate] = @BookingDate
          AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
          AND [StartTime] < @EndTime
          AND COALESCE([ActualEndTime], [EndTime]) > @StartTime
    )
    BEGIN
        THROW 51069, 'This pet already has a booking for this slot.', 1;
    END

    -- Race-safe capacity check: count active (non-cancelled) bookings overlapping
    -- the requested window FOR THIS SERVICE, holding UPDLOCK + HOLDLOCK so
    -- concurrent CreateBooking calls on the same service serialise. DayCare and
    -- NightStay each have their own capacity bucket. A booking holds its slot in
    -- every status except the two cancelled ones — and only up to
    -- COALESCE([ActualEndTime], [EndTime]), so a job that finished early has
    -- already handed its remaining hours back and does not count against them.
    -- This is the gate the slot grid promises: [Booking].[GetBookingsForDate]
    -- uses the identical expression, so what is shown as free is what is
    -- admitted here.
    DECLARE @Concurrent INT;
    SELECT @Concurrent = COUNT(*)
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ServiceId] = @ServiceId
      AND [BookingDate] = @BookingDate
      AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
      AND [StartTime] < @EndTime
      AND COALESCE([ActualEndTime], [EndTime]) > @StartTime;

    IF @Concurrent >= @Capacity
    BEGIN
        THROW 51062, 'No remaining capacity for this slot.', 1;
    END

    -- Snapshot the provider's current cancellation policy so a later policy change
    -- never re-rules this booking. NULL = no restriction (itself a valid snapshot).
    DECLARE @CancellationPolicyHours INT =
        (SELECT [MinimumHoursBeforeCancellation]
         FROM [Provider].[ProviderCancellationPolicies]
         WHERE [ProviderId] = @ProviderId);

    -- Snapshot the SELECTED service-location address. ParentLocation → the parent's
    -- profile address (in SQL); ProviderLocation → the provider's business address,
    -- resolved by the caller (Cosmos) and passed in. Frozen so a later edit to
    -- either party's address never moves this booking.
    DECLARE @SnapshotAddressLine NVARCHAR(500) = NULL;
    DECLARE @SnapshotCity NVARCHAR(200) = NULL;
    DECLARE @SnapshotZipCode NVARCHAR(32) = NULL;
    DECLARE @SnapshotLatitude DECIMAL(9, 6) = NULL;
    DECLARE @SnapshotLongitude DECIMAL(9, 6) = NULL;

    IF @LocationType = N'ParentLocation'
    BEGIN
        SELECT @SnapshotAddressLine = [AddressLine],
               @SnapshotCity        = [City],
               @SnapshotZipCode     = [ZipCode],
               @SnapshotLatitude    = [Latitude],
               @SnapshotLongitude   = [Longitude]
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId;
    END
    ELSE IF @LocationType = N'ProviderLocation'
    BEGIN
        SET @SnapshotAddressLine = @SnapshotProviderAddressLine;
        SET @SnapshotCity        = @SnapshotProviderCity;
        SET @SnapshotZipCode     = @SnapshotProviderZipCode;
        SET @SnapshotLatitude    = @SnapshotProviderLatitude;
        SET @SnapshotLongitude   = @SnapshotProviderLongitude;
    END

    DECLARE @InsertedBookingId TABLE ([BookingId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[Bookings]
    (
        [ProviderId],
        [PetParentId],
        [PetId],
        [ServiceId],
        [ServiceCategory],
        [SubCategory],
        [ServiceItemCode],
        [BookingDate],
        [StartTime],
        [EndTime],
        [JobNotes],
        [LocationType],
        [PricePerHour],
        [CancellationPolicyHours],
        [SnapshotAddressLine],
        [SnapshotCity],
        [SnapshotZipCode],
        [SnapshotLatitude],
        [SnapshotLongitude]
    )
    OUTPUT inserted.[BookingId] INTO @InsertedBookingId
    VALUES
    (
        @ProviderId,
        @PetParentId,
        @PetId,
        @ServiceId,
        @ServiceCategory,
        @SubCategory,
        @ServiceItemCode,
        @BookingDate,
        @StartTime,
        @EndTime,
        @JobNotes,
        @LocationType,
        @PricePerHour,
        @CancellationPolicyHours,
        @SnapshotAddressLine,
        @SnapshotCity,
        @SnapshotZipCode,
        @SnapshotLatitude,
        @SnapshotLongitude
    );

    DECLARE @BookingId UNIQUEIDENTIFIER = (SELECT TOP (1) [BookingId] FROM @InsertedBookingId);

    -- Seed the audit trail with the creation entry (Status defaults to CREATED).
    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, NULL, N'CREATED', N'System', NULL, N'Booking created');

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [ServiceItemCode],
           [Source],
           [CustomerName],
           [CustomerMobileCountryCode],
           [CustomerMobile],
           [AnimalType],
           [PetName],
           [ServiceLocation],
           [CustomerLocation],
           [PricePerHour],
           [JobNotes],
           [PetId]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
