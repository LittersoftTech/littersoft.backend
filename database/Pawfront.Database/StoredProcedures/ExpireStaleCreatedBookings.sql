-- A booking still sitting in CREATED — nobody has accepted it — expires on
-- EITHER of two triggers, both of which mean the same thing: the provider has
-- run out of time to accept.
--   * BR-17: it has been pending for @PendingHours (default 24).
--   * BR-53: the service now starts in under @LeadTimeHours (default 2), i.e.
--     serviceStart < @Now + @LeadTimeHours. That is the SAME cutoff BR-01 uses
--     to decide a booking is too soon to be created, so an unaccepted booking
--     dies exactly when a fresh one for that slot could no longer be made.
--     serviceStart is BookingDate + StartTime (single-day) and
--     CheckInDate + DropOffTime (night stay), matching the modification-window
--     and lead-time arithmetic elsewhere. Comparison is strict (<), so a
--     booking whose service is exactly @LeadTimeHours away survives this tick —
--     mirroring BookingLeadTime.IsTooSoon.
-- A booking already past its start time is caught by the same test.
--
-- Run periodically by the scheduled external job (Azure Function, timer-
-- triggered, replaces the retired in-database Booking.ExpireStaleBookings sweep
-- — see docs/booking-rules.md). Idempotent and race-safe: the UPDATE only
-- touches rows still in CREATED, so a concurrent accept on the same row
-- serialises on the row lock and one of the two loses.
-- The status-engine sprocs (UpdateBookingStatus / UpdateNightStayBookingStatus)
-- REJECT an accept attempted on a booking that either trigger has caught
-- (THROW 51129 / 51153 and their night-stay mirrors 51249 / 51273) without
-- writing anything — this sproc is the only writer of EXPIRED.
-- Returns one row: (ExpiredBookings, ExpiredNightStayBookings).
CREATE OR ALTER PROCEDURE [Booking].[ExpireStaleCreatedBookings]
    @PendingHours INT = 24,
    @LeadTimeHours INT = 2
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @PendingCutoff DATETIME2(7) = DATEADD(HOUR, -@PendingHours, @Now);
    DECLARE @LeadTimeCutoff DATETIME2(7) = DATEADD(HOUR, @LeadTimeHours, @Now);
    DECLARE @PendingNote NVARCHAR(500) =
        N'Automatically expired after ' + CAST(@PendingHours AS NVARCHAR(8))
        + N' hours awaiting provider acceptance.';
    DECLARE @LeadTimeNote NVARCHAR(500) =
        N'Automatically expired: never accepted, and the service now starts in under '
        + CAST(@LeadTimeHours AS NVARCHAR(8)) + N' hours.';

    -- [Reason] records WHICH trigger fired, so the audit row explains itself.
    -- The pending trigger wins when both apply — it is the older claim on the row.
    DECLARE @ExpiredBookings TABLE (
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [Reason] NVARCHAR(16) NOT NULL);
    DECLARE @ExpiredNightStays TABLE (
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
        [Reason] NVARCHAR(16) NOT NULL);

    BEGIN TRANSACTION;

    UPDATE [Booking].[Bookings]
    SET [Status] = N'EXPIRED',
        -- The booking was never accepted, so no money can ever move on it. This
        -- job is the ONLY writer of EXPIRED (the status-engine sprocs reject a
        -- late transition without writing), which makes it the only place the
        -- payout needs settling for this outcome.
        [PayoutStatus] = N'NO_PAYOUT',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[BookingId],
           CASE WHEN deleted.[CreatedAtUtc] <= @PendingCutoff THEN N'Pending' ELSE N'LeadTime' END
    INTO @ExpiredBookings ([BookingId], [Reason])
    WHERE [Status] = N'CREATED'
      AND ([CreatedAtUtc] <= @PendingCutoff
           OR DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), [StartTime]),
                      CAST([BookingDate] AS DATETIME2(7))) < @LeadTimeCutoff);

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [BookingId], N'CREATED', N'EXPIRED', N'System', NULL,
           CASE WHEN [Reason] = N'Pending' THEN @PendingNote ELSE @LeadTimeNote END
    FROM @ExpiredBookings;

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'EXPIRED',
        [PayoutStatus] = N'NO_PAYOUT',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[NightStayBookingId],
           CASE WHEN deleted.[CreatedAtUtc] <= @PendingCutoff THEN N'Pending' ELSE N'LeadTime' END
    INTO @ExpiredNightStays ([NightStayBookingId], [Reason])
    WHERE [Status] = N'CREATED'
      AND ([CreatedAtUtc] <= @PendingCutoff
           OR DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), [DropOffTime]),
                      CAST([CheckInDate] AS DATETIME2(7))) < @LeadTimeCutoff);

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [NightStayBookingId], N'CREATED', N'EXPIRED', N'System', NULL,
           CASE WHEN [Reason] = N'Pending' THEN @PendingNote ELSE @LeadTimeNote END
    FROM @ExpiredNightStays;

    -- ONE expiry event, TWO notifications (V3 cards P-S1 + V-S2) — the parent is
    -- told to re-book and lands on the booking; the provider is told they lost the
    -- job and lands on Payouts -> Ignored Jobs. Deliberately a single trigger with
    -- two recipients rather than two independent timers, so the two can never
    -- disagree about whether the booking expired.
    DECLARE @BookingId UNIQUEIDENTIFIER;
    DECLARE @IsNightStay BIT;

    DECLARE expired_bookings CURSOR LOCAL FAST_FORWARD FOR
        SELECT [BookingId], 0 FROM @ExpiredBookings
        UNION ALL
        SELECT [NightStayBookingId], 1 FROM @ExpiredNightStays;

    OPEN expired_bookings;
    FETCH NEXT FROM expired_bookings INTO @BookingId, @IsNightStay;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = N'PetParent',
            @NotificationType = N'BOOKING_EXPIRED_FOR_PARENT';

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = N'Provider',
            @NotificationType = N'BOOKING_EXPIRED_FOR_PROVIDER';

        FETCH NEXT FROM expired_bookings INTO @BookingId, @IsNightStay;
    END

    CLOSE expired_bookings;
    DEALLOCATE expired_bookings;

    COMMIT TRANSACTION;

    SELECT
        (SELECT COUNT(*) FROM @ExpiredBookings) AS [ExpiredBookings],
        (SELECT COUNT(*) FROM @ExpiredNightStays) AS [ExpiredNightStayBookings];
END;
