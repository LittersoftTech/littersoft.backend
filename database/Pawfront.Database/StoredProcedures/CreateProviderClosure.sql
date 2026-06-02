CREATE OR ALTER PROCEDURE [Provider].[CreateClosures]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceIds [Provider].[ServiceIdList] READONLY,
    @StartDate DATE,
    @EndDate DATE,
    @StartTime TIME(0) = NULL,
    @EndTime TIME(0) = NULL,
    @Reason NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    -- Caller passed an empty list. Treat as a request error.
    IF NOT EXISTS (SELECT 1 FROM @ServiceIds)
    BEGIN
        THROW 51075, 'At least one service id is required.', 1;
    END

    IF NOT EXISTS (
        SELECT 1 FROM [Provider].[Providers] WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51070, 'Provider profile was not found.', 1;
    END

    -- Every requested ServiceId must exist, belong to this provider, and be active.
    -- UPDLOCK + HOLDLOCK serialise us against concurrent DeactivateProviderService.
    IF EXISTS (
        SELECT 1
        FROM @ServiceIds AS s
        LEFT JOIN [Provider].[ProviderServices] AS ps WITH (UPDLOCK, HOLDLOCK)
            ON s.[ServiceId] = ps.[ServiceId]
        WHERE ps.[ServiceId] IS NULL
           OR ps.[ProviderId] <> @ProviderId
           OR ps.[IsActive] = 0
    )
    BEGIN
        THROW 51072, 'One or more service ids are not valid or active for this provider.', 1;
    END

    -- Race-safe conflict check, scoped by ServiceId — a DayCare closure should
    -- only conflict with DayCare bookings, not NightStay bookings. UPDLOCK +
    -- HOLDLOCK serialises us against concurrent Booking.CreateBooking on the
    -- same service.
    DECLARE @Conflicts TABLE (
        ServiceId UNIQUEIDENTIFIER NOT NULL,
        BookingId UNIQUEIDENTIFIER NOT NULL,
        PetParentId UNIQUEIDENTIFIER NULL,
        Source NVARCHAR(16) NOT NULL,
        CustomerName NVARCHAR(200) NULL,
        BookingDate DATE NOT NULL,
        StartTime TIME(0) NOT NULL,
        EndTime TIME(0) NOT NULL
    );

    INSERT INTO @Conflicts (ServiceId, BookingId, PetParentId, Source, CustomerName, BookingDate, StartTime, EndTime)
    SELECT b.[ServiceId], b.[BookingId], b.[PetParentId], b.[Source], b.[CustomerName],
           b.[BookingDate], b.[StartTime], b.[EndTime]
    FROM [Booking].[Bookings] AS b WITH (UPDLOCK, HOLDLOCK)
    INNER JOIN @ServiceIds AS s ON s.[ServiceId] = b.[ServiceId]
    WHERE b.[Status] = N'Confirmed'
      AND b.[BookingDate] BETWEEN @StartDate AND @EndDate
      AND (
          -- Full-day closure: ANY confirmed booking on a covered date conflicts.
          @StartTime IS NULL
          -- Partial-day closure: only bookings overlapping the time window conflict.
          OR (b.[StartTime] < @EndTime AND b.[EndTime] > @StartTime)
      );

    IF EXISTS (SELECT 1 FROM @Conflicts)
    BEGIN
        SELECT ServiceId, BookingId, PetParentId, Source, CustomerName,
               BookingDate, StartTime, EndTime
        FROM @Conflicts
        ORDER BY ServiceId, BookingDate, StartTime;

        ROLLBACK TRANSACTION;
        RETURN;
    END

    -- All-or-nothing batch insert: every service id gets its own closure row.
    DECLARE @Inserted TABLE (
        ClosureId UNIQUEIDENTIFIER,
        ProviderId UNIQUEIDENTIFIER,
        ServiceId UNIQUEIDENTIFIER,
        StartDate DATE,
        EndDate DATE,
        StartTime TIME(0),
        EndTime TIME(0),
        Reason NVARCHAR(500),
        CreatedAtUtc DATETIME2(7)
    );

    INSERT INTO [Provider].[ProviderClosures]
        ([ProviderId], [ServiceId], [StartDate], [EndDate], [StartTime], [EndTime], [Reason])
    OUTPUT inserted.[ClosureId], inserted.[ProviderId], inserted.[ServiceId],
           inserted.[StartDate], inserted.[EndDate], inserted.[StartTime], inserted.[EndTime],
           inserted.[Reason], inserted.[CreatedAtUtc]
    INTO @Inserted
    SELECT @ProviderId, s.[ServiceId], @StartDate, @EndDate, @StartTime, @EndTime, @Reason
    FROM @ServiceIds AS s;

    SELECT ClosureId, ProviderId, ServiceId, StartDate, EndDate, StartTime, EndTime, Reason, CreatedAtUtc
    FROM @Inserted
    ORDER BY ServiceId;

    COMMIT TRANSACTION;
END;
