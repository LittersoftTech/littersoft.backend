-- BR-30 (widened 2026-08-02 to cover BOTH proposal directions — previously
-- PARENT-only, see the retired [[BR-31]] note in docs/booking-rules.md): an
-- unanswered modification proposal, from EITHER party, expires 2 hours before
-- the service starts (BookingDate + StartTime for a single-day booking,
-- CheckInDate + DropOffTime for a stay; all UTC) — the staging row is discarded
-- and the booking REVERTS to CONFIRMED (NOT terminal: it goes back into a
-- startable state so neither party is left blocked by a stale proposal).
--
-- Run periodically by the scheduled external job, BEFORE
-- Booking.SettleUnstartedJobsAsNoShow in the same tick — a booking whose
-- proposal expires AND whose provider's working day has also ended should
-- settle as a no-show in that same pass rather than waiting for the next one.
--
-- The status-engine's respond sprocs (RespondBookingModification /
-- RespondNightStayBookingModification) REJECT a response landing after the
-- cutoff (THROW 51152 / 51272) without performing the revert — this sproc is
-- the only writer of the CONFIRMED revert + the staging-row delete.
--
-- Replaces [Booking].[RevertExpiredParentModificationRequests] (dropped by
-- DeployAll.sql on re-deploy) now that the rule is symmetric — the old name's
-- "Parent" would have been misleading once MODIFICATION_REQUEST_BY_PROVIDER
-- rows are reverted here too.
-- Returns one row: (RevertedBookings, RevertedNightStayBookings).
CREATE OR ALTER PROCEDURE [Booking].[RevertExpiredModificationRequests]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Note NVARCHAR(500) =
        N'Modification request expired unanswered 2 hours before the service start time.';

    -- FromStatus is captured per row (not a hardcoded literal) since a reverted
    -- booking may have come from either MODIFICATION_REQUEST_BY_PARENT or
    -- MODIFICATION_REQUEST_BY_PROVIDER.
    DECLARE @RevertedBookings TABLE (
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [FromStatus] NVARCHAR(48) NOT NULL);
    DECLARE @RevertedNightStays TABLE (
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
        [FromStatus] NVARCHAR(48) NOT NULL);

    BEGIN TRANSACTION;

    -- Single-day: cutoff = BookingDate + StartTime, minus 2 hours.
    UPDATE [Booking].[Bookings]
    SET [Status] = N'CONFIRMED',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[BookingId], deleted.[Status] INTO @RevertedBookings
    WHERE [Status] IN (N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER')
      AND @Now >= DATEADD(HOUR, -2,
              DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), [StartTime]),
                      CAST([BookingDate] AS DATETIME2(7))));

    DELETE m
    FROM [Booking].[BookingModifications] m
    INNER JOIN @RevertedBookings r ON r.[BookingId] = m.[BookingId];

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [BookingId], [FromStatus], N'CONFIRMED', N'System', NULL, @Note
    FROM @RevertedBookings;

    -- Night stay: cutoff = CheckInDate + DropOffTime, minus 2 hours.
    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'CONFIRMED',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[NightStayBookingId], deleted.[Status] INTO @RevertedNightStays
    WHERE [Status] IN (N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER')
      AND @Now >= DATEADD(HOUR, -2,
              DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), [DropOffTime]),
                      CAST([CheckInDate] AS DATETIME2(7))));

    DELETE m
    FROM [Booking].[NightStayBookingModifications] m
    INNER JOIN @RevertedNightStays r ON r.[NightStayBookingId] = m.[NightStayBookingId];

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [NightStayBookingId], [FromStatus], N'CONFIRMED', N'System', NULL, @Note
    FROM @RevertedNightStays;

    COMMIT TRANSACTION;

    SELECT
        (SELECT COUNT(*) FROM @RevertedBookings) AS [RevertedBookings],
        (SELECT COUNT(*) FROM @RevertedNightStays) AS [RevertedNightStayBookings];
END;
