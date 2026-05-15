CREATE OR ALTER PROCEDURE [Booking].[CreateBooking]
    @ProviderId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @ServiceCategory NVARCHAR(64),
    @SubCategory NVARCHAR(64),
    @BookingDate DATE,
    @StartTime TIME(0),
    @EndTime TIME(0),
    @Capacity INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[Providers]
        WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51061, 'Provider was not found.', 1;
    END

    IF NOT EXISTS (
        SELECT 1
        FROM [Customer].[PetParents]
        WHERE [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51060, 'Pet parent was not found.', 1;
    END

    -- Race-safe capacity check: count confirmed bookings overlapping the requested window,
    -- holding UPDLOCK + HOLDLOCK so concurrent CreateBooking calls serialise.
    DECLARE @Concurrent INT;
    SELECT @Concurrent = COUNT(*)
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId
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
        [ServiceCategory],
        [SubCategory],
        [BookingDate],
        [StartTime],
        [EndTime]
    )
    OUTPUT inserted.[BookingId] INTO @InsertedBookingId
    VALUES
    (
        @ProviderId,
        @PetParentId,
        @ServiceCategory,
        @SubCategory,
        @BookingDate,
        @StartTime,
        @EndTime
    );

    DECLARE @BookingId UNIQUEIDENTIFIER = (SELECT TOP (1) [BookingId] FROM @InsertedBookingId);

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
