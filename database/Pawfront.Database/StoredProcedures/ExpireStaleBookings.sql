-- Periodic booking expiry sweep (single-day + night-stay). Two independent
-- kinds of expiry, both terminal states that free the slot capacity and get a
-- 'System' audit row per booking:
--
--   1. EXPIRED     — the booking sat in CREATED @PendingHours (default 24)
--                    after it was created: the provider never accepted. The
--                    status-engine sprocs also apply this flip lazily (THROW
--                    51129 / 51249) so an accept that lands between sweeps is
--                    still rejected.
--   2. JOB_EXPIRED — the provider accepted but the job never actually got
--                    underway: the booking is still confirmed-equivalent OR sitting
--                    in START_JOB (start-OTP issued, never verified) — i.e. it never
--                    reached IN_PROGRESS — and its whole scheduled window has
--                    elapsed (single-day: BookingDate+EndTime is past; night-stay:
--                    CheckOutDate < today — the checkout day is the pickup day, not
--                    a stayed night). Once IN_PROGRESS, the job started so it
--                    is never touched here.
--
-- Run periodically by the BookingExpirySweeper hosted service in both API
-- hosts. Idempotent and race-safe: the UPDATEs only touch rows in the matching
-- source state, and a concurrent engine transition on the same row serialises
-- on the row lock (e.g. a party reporting a no-show before the window ends
-- moves the row out of the confirmed-equivalent set first).
-- Returns one row: (ExpiredBookings, ExpiredNightStayBookings,
--                   JobExpiredBookings, JobExpiredNightStayBookings).
CREATE OR ALTER PROCEDURE [Booking].[ExpireStaleBookings]
    @PendingHours INT = 24
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Today DATE = CONVERT(date, @Now);
    DECLARE @NowTime TIME(0) = CONVERT(time(0), @Now);
    DECLARE @Cutoff DATETIME2(7) = DATEADD(HOUR, -@PendingHours, @Now);
    DECLARE @PendingNote NVARCHAR(500) =
        N'Automatically expired after ' + CAST(@PendingHours AS NVARCHAR(8))
        + N' hours awaiting provider acceptance.';
    DECLARE @JobNote NVARCHAR(500) =
        N'Automatically expired: accepted but never started before the scheduled window elapsed.';

    DECLARE @ExpiredBookings TABLE ([BookingId] UNIQUEIDENTIFIER NOT NULL);
    DECLARE @ExpiredNightStays TABLE ([NightStayBookingId] UNIQUEIDENTIFIER NOT NULL);
    DECLARE @JobExpiredBookings TABLE ([BookingId] UNIQUEIDENTIFIER NOT NULL, [FromStatus] NVARCHAR(48) NOT NULL);
    DECLARE @JobExpiredNightStays TABLE ([NightStayBookingId] UNIQUEIDENTIFIER NOT NULL, [FromStatus] NVARCHAR(48) NOT NULL);

    BEGIN TRANSACTION;

    -- 1. Stale CREATED -> EXPIRED (provider never accepted). ------------------
    UPDATE [Booking].[Bookings]
    SET [Status] = N'EXPIRED',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[BookingId] INTO @ExpiredBookings
    WHERE [Status] = N'CREATED'
      AND [CreatedAtUtc] <= @Cutoff;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [BookingId], N'CREATED', N'EXPIRED', N'System', NULL, @PendingNote
    FROM @ExpiredBookings;

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'EXPIRED',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[NightStayBookingId] INTO @ExpiredNightStays
    WHERE [Status] = N'CREATED'
      AND [CreatedAtUtc] <= @Cutoff;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [NightStayBookingId], N'CREATED', N'EXPIRED', N'System', NULL, @PendingNote
    FROM @ExpiredNightStays;

    -- 2. Accepted-but-never-started, window elapsed -> JOB_EXPIRED. -----------
    UPDATE [Booking].[Bookings]
    SET [Status] = N'JOB_EXPIRED',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[BookingId], deleted.[Status] INTO @JobExpiredBookings
    WHERE [Status] IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION', N'PARENT_ACCEPTED_MODIFICATION',
                       N'PROVIDER_DECLINED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION', N'START_JOB')
      AND ([BookingDate] < @Today OR ([BookingDate] = @Today AND [EndTime] <= @NowTime));

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [BookingId], [FromStatus], N'JOB_EXPIRED', N'System', NULL, @JobNote
    FROM @JobExpiredBookings;

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = N'JOB_EXPIRED',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[NightStayBookingId], deleted.[Status] INTO @JobExpiredNightStays
    WHERE [Status] IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION', N'PARENT_ACCEPTED_MODIFICATION',
                       N'PROVIDER_DECLINED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION', N'START_JOB')
      AND [CheckOutDate] < @Today;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [NightStayBookingId], [FromStatus], N'JOB_EXPIRED', N'System', NULL, @JobNote
    FROM @JobExpiredNightStays;

    COMMIT TRANSACTION;

    SELECT
        (SELECT COUNT(*) FROM @ExpiredBookings) AS [ExpiredBookings],
        (SELECT COUNT(*) FROM @ExpiredNightStays) AS [ExpiredNightStayBookings],
        (SELECT COUNT(*) FROM @JobExpiredBookings) AS [JobExpiredBookings],
        (SELECT COUNT(*) FROM @JobExpiredNightStays) AS [JobExpiredNightStayBookings];
END;
