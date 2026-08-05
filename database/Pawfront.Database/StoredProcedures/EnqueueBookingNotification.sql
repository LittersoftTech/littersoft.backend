-- Builds the standard booking notification payload and enqueues it.
--
-- THE single place the booking `data` object is assembled. Every booking
-- notification in the product goes through here — the transition sprocs
-- (accept / decline / cancel / start / OTP / complete / paid / modification) and
-- all of the timer sweeps — so the mobile contract is defined once instead of
-- being hand-rolled at ~30 call sites where one of them would inevitably drift.
--
-- It emits the canonical id block the apps read on every notification:
--   category, bookingId, eventId, parentId, providerId, petId, isNightStay, payoutId
-- plus the template parameters the copy needs (petName, providerName, parentName,
-- serviceDate/startTime or checkInDate/dropOffTime, serviceType, serviceItemCode).
--
-- NOTE on `serviceName`: deliberately NOT built here. The 18 grooming menu-item
-- display names live in the C# GroomingServiceCatalog, so this sproc emits the
-- raw [ServiceType] + [ServiceItemCode] and NotificationRenderer derives the
-- label at render time. Duplicating those names in T-SQL would create a second
-- copy that silently drifts from the catalog.
--
-- Values are all CAST to NVARCHAR: FCM rejects non-string data values, so a
-- number reaching the payload would have to be re-stringified anyway. NULL
-- columns are omitted by FOR JSON (no INCLUDE_NULL_VALUES) — the C#
-- NotificationPayloadBuilder fills the canonical fields back in as empty strings,
-- which is what makes "not applicable" distinguishable from "forgotten".
--
-- Never THROWs, for the same reason Notification.EnqueueNotification doesn't: a
-- notification must never be the reason a booking transaction rolls back. An
-- unknown @BookingId simply enqueues nothing.
CREATE OR ALTER PROCEDURE [Notification].[EnqueueBookingNotification]
    @BookingId UNIQUEIDENTIFIER,
    @IsNightStay BIT,
    @Audience NVARCHAR(16),
    @NotificationType NVARCHAR(64),
    -- Extra template parameters, supplied only by the callers whose copy needs
    -- them. Explicit parameters rather than a JSON blob to merge: T-SQL has no
    -- clean object-merge, and naming them keeps the contract greppable.
    @Amount NVARCHAR(64) = NULL,
    @NewServiceDate NVARCHAR(32) = NULL,
    @NewStartTime NVARCHAR(16) = NULL,
    @AbsentParty NVARCHAR(32) = NULL,
    @Location NVARCHAR(500) = NULL,
    @ClosingTime NVARCHAR(16) = NULL,
    -- Idempotency. Callers pass a suffix rather than a whole key so the
    -- type + booking prefix stays consistent across every producer.
    @DedupeSuffix NVARCHAR(64) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @PetId UNIQUEIDENTIFIER;
    DECLARE @ServiceId UNIQUEIDENTIFIER;
    DECLARE @PayoutId NVARCHAR(64);
    DECLARE @ServiceItemCode NVARCHAR(64);
    DECLARE @ServiceDate DATE;
    DECLARE @StartTime TIME(0);
    DECLARE @CheckOutDate DATE;
    -- Custom walk-ins carry the pet's name on the booking row rather than a PetId.
    DECLARE @RowPetName NVARCHAR(100);
    DECLARE @SnapshotAddressLine NVARCHAR(500);
    DECLARE @UnitPrice DECIMAL(10, 2);
    DECLARE @Quantity DECIMAL(10, 4);

    IF @IsNightStay = 1
    BEGIN
        SELECT @ProviderId = [ProviderId],
               @PetParentId = [PetParentId],
               @PetId = [PetId],
               @ServiceId = [ServiceId],
               @PayoutId = [PayoutId],
               @ServiceDate = [CheckInDate],
               @CheckOutDate = [CheckOutDate],
               -- A stay's "start time" is the drop-off on the check-in day: the
               -- same instant BR-01 / BR-53 / the modification cutoff all measure
               -- against, so the notification quotes what the rules use.
               @StartTime = [DropOffTime],
               @SnapshotAddressLine = [SnapshotAddressLine],
               @UnitPrice = [PricePerNight],
               -- The checkout day is not a stayed night.
               @Quantity = DATEDIFF(DAY, [CheckInDate], [CheckOutDate])
        FROM [Booking].[NightStayBookings]
        WHERE [NightStayBookingId] = @BookingId;
    END
    ELSE
    BEGIN
        SELECT @ProviderId = [ProviderId],
               @PetParentId = [PetParentId],
               @PetId = [PetId],
               @ServiceId = [ServiceId],
               @PayoutId = [PayoutId],
               @ServiceItemCode = [ServiceItemCode],
               @ServiceDate = [BookingDate],
               @StartTime = [StartTime],
               @RowPetName = [PetName],
               @SnapshotAddressLine = [SnapshotAddressLine],
               @UnitPrice = [PricePerHour],
               @Quantity = DATEDIFF(MINUTE, [StartTime], [EndTime]) / 60.0
        FROM [Booking].[Bookings]
        WHERE [BookingId] = @BookingId;
    END

    IF @ProviderId IS NULL
    BEGIN
        -- Unknown booking. Nothing to say, and throwing would take the caller's
        -- transaction down with it.
        RETURN;
    END

    -- A notification with no recipient is not an error either: a Custom walk-in
    -- has no PetParentId, so parent-audience notifications simply don't apply.
    DECLARE @RecipientId UNIQUEIDENTIFIER =
        CASE WHEN @Audience = N'Provider' THEN @ProviderId ELSE @PetParentId END;

    IF @RecipientId IS NULL
    BEGIN
        RETURN;
    END

    DECLARE @ProviderName NVARCHAR(200);
    DECLARE @ParentName NVARCHAR(200);
    DECLARE @PetName NVARCHAR(100);
    DECLARE @ServiceType NVARCHAR(32);

    SELECT @ProviderName = NULLIF(LTRIM(RTRIM(
        ISNULL([FirstName], N'') + N' ' + ISNULL([LastName], N''))), N'')
    FROM [Provider].[Providers]
    WHERE [ProviderId] = @ProviderId;

    IF @PetParentId IS NOT NULL
    BEGIN
        SELECT @ParentName = NULLIF(LTRIM(RTRIM(
            ISNULL([FirstName], N'') + N' ' + ISNULL([LastName], N''))), N'')
        FROM [Parent].[PetParents]
        WHERE [PetParentId] = @PetParentId;
    END

    IF @PetId IS NOT NULL
    BEGIN
        SELECT @PetName = [PetName] FROM [Parent].[Pets] WHERE [PetId] = @PetId;
    END

    -- Falls back to the walk-in's free-text pet name when there is no linked pet.
    SET @PetName = COALESCE(@PetName, @RowPetName);

    SELECT @ServiceType = [ServiceType]
    FROM [Provider].[ProviderServices]
    WHERE [ServiceId] = @ServiceId;

    DECLARE @EntityType NVARCHAR(32) =
        CASE WHEN @IsNightStay = 1 THEN N'NightStayBooking' ELSE N'Booking' END;

    -- Derive the amount from the price frozen onto the booking at creation, unless
    -- the caller supplied one. Same rule the booking-detail read uses: only the
    -- unit RATE is locked, so the total is always rate x quantity and an accepted
    -- modification that changes the duration still re-totals correctly.
    --
    -- Legacy rows created before price-locking have no rate. They resolve to NULL,
    -- the key drops out of the JSON, and the renderer's "the agreed amount"
    -- fallback carries the sentence — rather than quoting a wrong figure, which on
    -- a "please pay" notification would be worse than quoting none.
    IF @Amount IS NULL AND @UnitPrice IS NOT NULL AND @Quantity > 0
    BEGIN
        SET @Amount = N'CHF ' + CONVERT(NVARCHAR(32),
            CAST(ROUND(@UnitPrice * @Quantity, 2) AS DECIMAL(12, 2)));
    END

    DECLARE @DataJson NVARCHAR(MAX) =
    (
        SELECT
            -- --- the canonical id block ---
            N'BOOKING'                                   AS [category],
            CAST(@BookingId AS NVARCHAR(36))             AS [bookingId],
            CAST(@PetParentId AS NVARCHAR(36))           AS [parentId],
            CAST(@ProviderId AS NVARCHAR(36))            AS [providerId],
            CAST(@PetId AS NVARCHAR(36))                 AS [petId],
            CASE WHEN @IsNightStay = 1 THEN N'true' ELSE N'false' END AS [isNightStay],
            @PayoutId                                    AS [payoutId],
            -- Kept alongside isNightStay for the clients built before it existed.
            CASE WHEN @IsNightStay = 1 THEN N'NightStay' ELSE N'SingleDay' END AS [bookingType],
            -- --- template parameters ---
            @ProviderName                                AS [providerName],
            @ParentName                                  AS [parentName],
            @PetName                                     AS [petName],
            @ServiceType                                 AS [serviceType],
            @ServiceItemCode                             AS [serviceItemCode],
            FORMAT(@ServiceDate, N'd MMM', N'en-GB')     AS [serviceDate],
            CONVERT(NVARCHAR(5), @StartTime, 108)        AS [startTime],
            -- Night-stay only; NULL columns drop out of the JSON.
            CASE WHEN @IsNightStay = 1
                 THEN FORMAT(@ServiceDate, N'd MMM', N'en-GB') END   AS [checkInDate],
            CASE WHEN @IsNightStay = 1
                 THEN FORMAT(@CheckOutDate, N'd MMM', N'en-GB') END  AS [checkOutDate],
            CASE WHEN @IsNightStay = 1
                 THEN CONVERT(NVARCHAR(5), @StartTime, 108) END      AS [dropOffTime],
            -- --- caller-supplied extras ---
            @Amount                                      AS [amount],
            @NewServiceDate                              AS [newServiceDate],
            @NewStartTime                                AS [newStartTime],
            @AbsentParty                                 AS [absentParty],
            COALESCE(@Location, @SnapshotAddressLine)    AS [location],
            @ClosingTime                                 AS [closingTime]
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER
    );

    DECLARE @DedupeKey NVARCHAR(200) =
        @NotificationType + N':' + CAST(@BookingId AS NVARCHAR(36))
        + N':' + @Audience
        + CASE WHEN @DedupeSuffix IS NULL THEN N'' ELSE N':' + @DedupeSuffix END;

    EXEC [Notification].[EnqueueNotification]
        @Audience = @Audience,
        @RecipientId = @RecipientId,
        @NotificationType = @NotificationType,
        @EntityType = @EntityType,
        @EntityId = @BookingId,
        @DataJson = @DataJson,
        @DedupeKey = @DedupeKey,
        -- Callers are transition sprocs returning their own booking row; a nested
        -- result set here would corrupt that.
        @SuppressResultSet = 1;
END
