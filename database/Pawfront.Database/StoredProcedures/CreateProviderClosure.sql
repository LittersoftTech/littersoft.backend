CREATE OR ALTER PROCEDURE [Provider].[CreateClosure]
    @ProviderId UNIQUEIDENTIFIER,
    @StartDate DATE,
    @EndDate DATE,
    @StartTime TIME(0) = NULL,
    @EndTime TIME(0) = NULL,
    @Reason NVARCHAR(500) = NULL,
    @ClosureId UNIQUEIDENTIFIER = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    SET @ClosureId = NULL;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[Providers]
        WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51070, 'Provider profile was not found.', 1;
    END

    -- Race-safe conflict check. UPDLOCK + HOLDLOCK serialises us against any
    -- concurrent Booking.CreateBooking on the same provider, so a booking
    -- inserted between our check and our INSERT will block until we commit.
    DECLARE @Conflicts TABLE (
        BookingId UNIQUEIDENTIFIER NOT NULL,
        PetParentId UNIQUEIDENTIFIER NOT NULL,
        BookingDate DATE NOT NULL,
        StartTime TIME(0) NOT NULL,
        EndTime TIME(0) NOT NULL
    );

    INSERT INTO @Conflicts (BookingId, PetParentId, BookingDate, StartTime, EndTime)
    SELECT b.[BookingId], b.[PetParentId], b.[BookingDate], b.[StartTime], b.[EndTime]
    FROM [Booking].[Bookings] AS b WITH (UPDLOCK, HOLDLOCK)
    WHERE b.[ProviderId] = @ProviderId
      AND b.[Status] = N'Confirmed'
      AND b.[BookingDate] BETWEEN @StartDate AND @EndDate
      AND (
          -- Full-day closure: ANY confirmed booking on a covered date conflicts.
          @StartTime IS NULL
          -- Partial-day closure: only bookings overlapping the time window conflict.
          OR (b.[StartTime] < @EndTime AND b.[EndTime] > @StartTime)
      );

    IF EXISTS (SELECT 1 FROM @Conflicts)
    BEGIN
        SELECT BookingId, PetParentId, BookingDate, StartTime, EndTime
        FROM @Conflicts
        ORDER BY BookingDate, StartTime;

        -- @ClosureId remains NULL: signals to caller that creation was refused.
        ROLLBACK TRANSACTION;
        RETURN;
    END

    DECLARE @InsertedId UNIQUEIDENTIFIER = NEWID();

    INSERT INTO [Provider].[ProviderClosures]
        ([ClosureId], [ProviderId], [StartDate], [EndDate], [StartTime], [EndTime], [Reason])
    VALUES
        (@InsertedId, @ProviderId, @StartDate, @EndDate, @StartTime, @EndTime, @Reason);

    SET @ClosureId = @InsertedId;

    SELECT [ClosureId], [ProviderId], [StartDate], [EndDate],
           [StartTime], [EndTime], [Reason], [CreatedAtUtc]
    FROM [Provider].[ProviderClosures]
    WHERE [ClosureId] = @InsertedId;

    COMMIT TRANSACTION;
END;
