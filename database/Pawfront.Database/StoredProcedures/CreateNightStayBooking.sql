-- Race-safe insert of a multi-night boarding booking. Mirrors
-- [Booking].[CreateBooking] but capacity is enforced PER NIGHT across
-- [@CheckInDate, @CheckOutDate) rather than by time-overlap on a single date.
-- THROWs: 51230 provider not found, 51231 provider inactive, 51232 pet parent
-- not found, 51233 pet not found / not owned, 51234 service unknown/inactive/
-- not owned/not a NightStay service, 51235 no capacity on one or more nights.
CREATE OR ALTER PROCEDURE [Booking].[CreateNightStayBooking]
    @ProviderId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @PetId UNIQUEIDENTIFIER = NULL,
    @ServiceId UNIQUEIDENTIFIER,
    @ServiceCategory NVARCHAR(64),
    @SubCategory NVARCHAR(64),
    @CheckInDate DATE,
    @CheckOutDate DATE,
    @DropOffTime TIME(0),
    @PickUpTime TIME(0),
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
        THROW 51230, 'Provider was not found.', 1;
    END

    -- Master Active/Inactive switch — when the provider has flipped themselves
    -- inactive, NO new bookings are accepted on ANY of their services. The
    -- UPDLOCK + HOLDLOCK above serialises us against a concurrent
    -- SetProviderActiveStatus call, so the check is race-safe.
    IF @ProviderIsActive = 0
    BEGIN
        THROW 51231, 'Provider is currently inactive and is not accepting new bookings.', 1;
    END

    IF NOT EXISTS (
        SELECT 1
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51232, 'Pet parent was not found.', 1;
    END

    -- Defense-in-depth: the API validates pet ownership before calling, but a
    -- direct sproc caller must not be able to pin someone else's pet on a stay.
    IF @PetId IS NOT NULL AND NOT EXISTS (
        SELECT 1
        FROM [Parent].[Pets]
        WHERE [PetId] = @PetId
          AND [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51233, 'Pet was not found or does not belong to the pet parent.', 1;
    END

    -- ServiceId must belong to the provider, be active, AND be a NightStay
    -- service. UPDLOCK + HOLDLOCK serialises us against DeactivateProviderService.
    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[ProviderServices] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [ProviderId] = @ProviderId
          AND [IsActive] = 1
          AND [ServiceType] = N'NightStay'
    )
    BEGIN
        THROW 51234, 'Service is not a valid, active NightStay service for this provider.', 1;
    END

    -- Per-night capacity check. Enumerate every stayed night in
    -- [@CheckInDate, @CheckOutDate) and count active bookings whose range
    -- covers that night (existing.CheckInDate <= night < existing.CheckOutDate).
    -- UPDLOCK + HOLDLOCK serialises concurrent creates on this service so the
    -- (N+1)-th overlapping stay is rejected once a night is full.
    DECLARE @FullNight DATE;

    ;WITH [Nights] AS
    (
        SELECT @CheckInDate AS [Night]
        UNION ALL
        SELECT DATEADD(DAY, 1, [Night])
        FROM [Nights]
        WHERE DATEADD(DAY, 1, [Night]) < @CheckOutDate
    )
    SELECT TOP (1) @FullNight = n.[Night]
    FROM [Nights] n
    LEFT JOIN [Booking].[NightStayBookings] b WITH (UPDLOCK, HOLDLOCK)
        ON b.[ServiceId] = @ServiceId
       AND b.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED')
       AND b.[CheckInDate] <= n.[Night]
       AND b.[CheckOutDate] > n.[Night]
    GROUP BY n.[Night]
    HAVING COUNT(b.[NightStayBookingId]) >= @Capacity
    OPTION (MAXRECURSION 366);

    IF @FullNight IS NOT NULL
    BEGIN
        THROW 51235, 'No remaining capacity for one or more nights in the stay.', 1;
    END

    DECLARE @InsertedId TABLE ([NightStayBookingId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[NightStayBookings]
    (
        [ProviderId],
        [PetParentId],
        [PetId],
        [ServiceId],
        [ServiceCategory],
        [SubCategory],
        [CheckInDate],
        [CheckOutDate],
        [DropOffTime],
        [PickUpTime]
    )
    OUTPUT inserted.[NightStayBookingId] INTO @InsertedId
    VALUES
    (
        @ProviderId,
        @PetParentId,
        @PetId,
        @ServiceId,
        @ServiceCategory,
        @SubCategory,
        @CheckInDate,
        @CheckOutDate,
        @DropOffTime,
        @PickUpTime
    );

    DECLARE @NightStayBookingId UNIQUEIDENTIFIER =
        (SELECT TOP (1) [NightStayBookingId] FROM @InsertedId);

    -- Seed the audit trail with the creation entry (Status defaults to CREATED).
    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, NULL, N'CREATED', N'System', NULL, N'Night stay booking created');

    SELECT [NightStayBookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [CheckInDate],
           [CheckOutDate],
           [DropOffTime],
           [PickUpTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
