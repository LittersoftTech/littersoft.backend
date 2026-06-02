CREATE OR ALTER PROCEDURE [Booking].[CreateCustomBooking]
    @ProviderId                UNIQUEIDENTIFIER,
    @ServiceId                 UNIQUEIDENTIFIER,
    @ServiceCategory           NVARCHAR(64),
    @SubCategory               NVARCHAR(64),
    @CustomerName              NVARCHAR(200),
    @CustomerMobileCountryCode NVARCHAR(8),
    @CustomerMobile            NVARCHAR(32),
    @AnimalType                NVARCHAR(32),
    @PetName                   NVARCHAR(100),
    @BookingDate               DATE,
    @StartTime                 TIME(0),
    @EndTime                   TIME(0),
    @ServiceLocation           NVARCHAR(32),
    @CustomerLocation          NVARCHAR(500) = NULL,
    @PricePerHour              DECIMAL(10, 2),
    @JobNotes                  NVARCHAR(2000) = NULL,
    @Capacity                  INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    -- Provider exists + master Active switch (same race-safe path as
    -- [Booking].[CreateBooking]).
    DECLARE @ProviderIsActive BIT;
    SELECT @ProviderIsActive = [IsActive]
    FROM [Provider].[Providers] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId;

    IF @ProviderIsActive IS NULL
    BEGIN
        THROW 51061, 'Provider was not found.', 1;
    END

    IF @ProviderIsActive = 0
    BEGIN
        THROW 51067, 'Provider is currently inactive and is not accepting new bookings.', 1;
    END

    -- ServiceId belongs to the provider AND is active.
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

    -- Race-safe capacity check — identical to [Booking].[CreateBooking].
    -- Custom and App bookings share the same per-service capacity bucket so the
    -- COUNT(*) sees both.
    DECLARE @Concurrent INT;
    SELECT @Concurrent = COUNT(*)
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ServiceId] = @ServiceId
      AND [BookingDate] = @BookingDate
      AND [Status] = N'Confirmed'
      AND [StartTime] < @EndTime
      AND [EndTime] > @StartTime;

    IF @Concurrent >= @Capacity
    BEGIN
        THROW 51062, 'No remaining capacity for this slot.', 1;
    END

    DECLARE @InsertedBookingId TABLE ([BookingId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[Bookings]
    (
        [ProviderId],
        [PetParentId],
        [ServiceId],
        [ServiceCategory],
        [SubCategory],
        [ServiceItemCode],
        [BookingDate],
        [StartTime],
        [EndTime],
        [Source],
        [CustomerName],
        [CustomerMobileCountryCode],
        [CustomerMobile],
        [AnimalType],
        [PetName],
        [ServiceLocation],
        [CustomerLocation],
        [PricePerHour],
        [JobNotes]
    )
    OUTPUT inserted.[BookingId] INTO @InsertedBookingId
    VALUES
    (
        @ProviderId,
        NULL,                        -- No pet parent for custom bookings.
        @ServiceId,
        @ServiceCategory,
        @SubCategory,
        NULL,                        -- ServiceItemCode unused for custom bookings.
        @BookingDate,
        @StartTime,
        @EndTime,
        N'Custom',
        @CustomerName,
        @CustomerMobileCountryCode,
        @CustomerMobile,
        @AnimalType,
        @PetName,
        @ServiceLocation,
        @CustomerLocation,
        @PricePerHour,
        @JobNotes
    );

    DECLARE @BookingId UNIQUEIDENTIFIER = (SELECT TOP (1) [BookingId] FROM @InsertedBookingId);

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
           [JobNotes]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
