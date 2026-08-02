-- BR-17: a booking left sitting in CREATED for @PendingHours (default 24) without
-- the provider accepting has expired. Run periodically by the scheduled external
-- job (Azure Function, timer-triggered, replaces the retired in-database
-- Booking.ExpireStaleBookings sweep — see docs/booking-rules.md). Idempotent and
-- race-safe: the UPDATE only touches rows still in CREATED, so a concurrent
-- accept on the same row serialises on the row lock and one of the two loses.
-- The status-engine sprocs (UpdateBookingStatus / UpdateNightStayBookingStatus)
-- REJECT an accept attempted on a stale CREATED booking (THROW 51129 / 51249)
-- without writing anything — this sproc is the only writer of EXPIRED.
-- Returns one row: (ExpiredBookings, ExpiredNightStayBookings).
CREATE OR ALTER PROCEDURE [Booking].[ExpireStaleCreatedBookings]
    @PendingHours INT = 24
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Cutoff DATETIME2(7) = DATEADD(HOUR, -@PendingHours, @Now);
    DECLARE @Note NVARCHAR(500) =
        N'Automatically expired after ' + CAST(@PendingHours AS NVARCHAR(8))
        + N' hours awaiting provider acceptance.';

    DECLARE @ExpiredBookings TABLE ([BookingId] UNIQUEIDENTIFIER NOT NULL);
    DECLARE @ExpiredNightStays TABLE ([NightStayBookingId] UNIQUEIDENTIFIER NOT NULL);

    BEGIN TRANSACTION;

    UPDATE [Booking].[Bookings]
    SET [Status] = N'EXPIRED',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[BookingId] INTO @ExpiredBookings
    WHERE [Status] = N'CREATED'
      AND [CreatedAtUtc] <= @Cutoff;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [BookingId], N'CREATED', N'EXPIRED', N'System', NULL, @Note
    FROM @ExpiredBookings;

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'EXPIRED',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[NightStayBookingId] INTO @ExpiredNightStays
    WHERE [Status] = N'CREATED'
      AND [CreatedAtUtc] <= @Cutoff;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [NightStayBookingId], N'CREATED', N'EXPIRED', N'System', NULL, @Note
    FROM @ExpiredNightStays;

    COMMIT TRANSACTION;

    SELECT
        (SELECT COUNT(*) FROM @ExpiredBookings) AS [ExpiredBookings],
        (SELECT COUNT(*) FROM @ExpiredNightStays) AS [ExpiredNightStayBookings];
END;
