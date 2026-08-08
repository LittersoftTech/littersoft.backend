-- Time-driven booking REMINDERS and NUDGES (V3 spec cards P-S4..P-S7, P-S9,
-- P-S10, P-S13, P-S14, V-S5..V-S8, V-S10, V-S12).
--
-- Purely additive: unlike the three settlement sweeps
-- (ExpireStaleCreatedBookings / RevertExpiredModificationRequests /
-- SettleUnstartedJobsAsNoShow) this one changes NO booking status and writes no
-- audit rows. It only enqueues notifications. That is deliberate — a reminder is
-- not a state change, and keeping it status-free means it can run on a much
-- tighter cadence (every minute, for the T-5min reminder) without any risk to
-- the booking lifecycle.
--
-- IDEMPOTENCY IS THE WHOLE DESIGN. The predicates below are "is it now past
-- moment X", which stays true on every subsequent tick, so at a 1-minute cadence
-- each one would re-fire ~endlessly. What stops that is the filtered UNIQUE
-- DedupeKey on Notification.NotificationOutbox: every enqueue here is keyed
-- {type}:{bookingId}:{audience}, so the second and later attempts collapse onto
-- the existing row. There is intentionally NO "already sent?" bookkeeping column
-- on the booking — the outbox already holds that fact, and a second copy of it
-- would be one more thing to keep in step.
--
-- All comparisons are UTC, like everything else in this schema.
-- Returns one row of per-arm counts for the function's log line.
CREATE OR ALTER PROCEDURE [Booking].[SendBookingReminders]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Today DATE = CAST(@Now AS DATE);

    -- Every rule below concerns a booking within a couple of days of now: the
    -- earliest is the T-24h reminder (tomorrow) and the latest the closing-time
    -- nudges (today). Bounding the scan keeps this off a full-history seek on a
    -- job that runs every minute; without it, any long-abandoned live booking
    -- would be re-examined 1,440 times a day.
    DECLARE @WindowFrom DATE = DATEADD(DAY, -2, @Today);
    DECLARE @WindowTo DATE = DATEADD(DAY, 2, @Today);

    -- Confirmed-equivalent: the five statuses a live, accepted booking can rest
    -- in. Kept as a table so every arm below tests the same set — the same list
    -- BookingStatuses.ConfirmedEquivalent holds in C#.
    DECLARE @Live TABLE ([Status] NVARCHAR(48) NOT NULL PRIMARY KEY);
    INSERT INTO @Live ([Status]) VALUES
        (N'CONFIRMED'),
        (N'PROVIDER_ACCEPTED_MODIFICATION'), (N'PARENT_ACCEPTED_MODIFICATION'),
        (N'PROVIDER_DECLINED_MODIFICATION'), (N'PARENT_DECLINED_MODIFICATION');

    -- One work list for the whole sproc: (booking, isNightStay, type, audience).
    -- Building it first and enqueuing once at the end keeps the cursor to a
    -- single pass, and keeps each rule readable as one INSERT..SELECT.
    DECLARE @Due TABLE (
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [IsNightStay] BIT NOT NULL,
        [NotificationType] NVARCHAR(64) NOT NULL,
        [Audience] NVARCHAR(16) NOT NULL,
        -- The provider's closing INSTANT, not a bare clock time: the notification
        -- is rendered in the recipient's timezone, and converting a time-of-day
        -- needs the date it falls on.
        [ClosingAtUtc] DATETIME2(0) NULL,
        PRIMARY KEY ([BookingId], [NotificationType], [Audience]));

    -- Single-day bookings with their derived instants. Night-stay is handled
    -- separately per arm, since a stay has no hourly window: only the day-before
    -- and starting-soon reminders apply to it, both measured from drop-off.
    DECLARE @SingleDay TABLE (
        [BookingId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        [Status] NVARCHAR(48) NOT NULL,
        [StartsAtUtc] DATETIME2(7) NOT NULL,
        [EndsAtUtc] DATETIME2(7) NOT NULL,
        -- The provider's closing time on the booking date, when they have saved
        -- weekly hours for that weekday. NULL means "no hours on file", which is
        -- treated as "never closes" — the same posture the start-job gate takes.
        -- It doubles as the {closingTime} the pick-up nudges quote, which is why
        -- no separate display column is kept.
        [ClosesAtUtc] DATETIME2(7) NULL);

    INSERT INTO @SingleDay ([BookingId], [Status], [StartsAtUtc], [EndsAtUtc], [ClosesAtUtc])
    SELECT b.[BookingId],
           b.[Status],
           DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), b.[StartTime]),
                   CAST(b.[BookingDate] AS DATETIME2(7))),
           DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), b.[EndTime]),
                   CAST(b.[BookingDate] AS DATETIME2(7))),
           CASE WHEN w.[EndTime] IS NOT NULL
                THEN DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), w.[EndTime]),
                             CAST(b.[BookingDate] AS DATETIME2(7))) END
    FROM [Booking].[Bookings] b
    LEFT JOIN [Provider].[ProviderWeeklyAvailability] w
        ON w.[ProviderId] = b.[ProviderId]
       -- DATEPART(WEEKDAY) is @@DATEFIRST-dependent; this arithmetic is not.
       -- 0 = Sunday, matching how the availability rows are stored.
       AND w.[DayOfWeek] = (DATEDIFF(DAY, '19000107', b.[BookingDate]) % 7)
       AND w.[IsOpen] = 1
    WHERE b.[BookingDate] BETWEEN @WindowFrom AND @WindowTo
      AND (b.[Status] IN (SELECT [Status] FROM @Live)
           OR b.[Status] IN (N'START_JOB', N'IN_PROGRESS'));

    -- ================================================================
    -- 1. T-24h "service tomorrow"  (P-S4 / V-S5) — both parties
    -- Fires from 24h before the start until the start itself. The window is open
    -- rather than instantaneous because a tick can be missed; the dedupe key is
    -- what keeps it to one send.
    -- ================================================================
    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId], 0, N'BOOKING_REMINDER_DAY_BEFORE', a.[Audience]
    FROM @SingleDay s
    CROSS JOIN (VALUES (N'PetParent'), (N'Provider')) AS a([Audience])
    WHERE s.[Status] IN (SELECT [Status] FROM @Live)
      AND @Now >= DATEADD(HOUR, -24, s.[StartsAtUtc])
      AND @Now < s.[StartsAtUtc];

    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT n.[NightStayBookingId], 1, N'BOOKING_REMINDER_DAY_BEFORE', a.[Audience]
    FROM [Booking].[NightStayBookings] n
    CROSS JOIN (VALUES (N'PetParent'), (N'Provider')) AS a([Audience])
    WHERE n.[CheckInDate] BETWEEN @WindowFrom AND @WindowTo
      AND n.[Status] IN (SELECT [Status] FROM @Live)
      AND @Now >= DATEADD(HOUR, -24,
              DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                      CAST(n.[CheckInDate] AS DATETIME2(7))))
      AND @Now < DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                         CAST(n.[CheckInDate] AS DATETIME2(7)));

    -- ================================================================
    -- 2. T-5min "starts soon"  (P-S5 / V-S6) — both parties
    -- This arm is why the reminder job runs every minute rather than every five:
    -- on a 5-minute cadence "starts in 5 minutes" could arrive anywhere from 0 to
    -- 5 minutes out, which is exactly the message it must not get wrong.
    -- ================================================================
    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId], 0, N'BOOKING_REMINDER_STARTING_SOON', a.[Audience]
    FROM @SingleDay s
    CROSS JOIN (VALUES (N'PetParent'), (N'Provider')) AS a([Audience])
    WHERE s.[Status] IN (SELECT [Status] FROM @Live)
      AND @Now >= DATEADD(MINUTE, -5, s.[StartsAtUtc])
      AND @Now < s.[StartsAtUtc];

    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT n.[NightStayBookingId], 1, N'BOOKING_REMINDER_STARTING_SOON', a.[Audience]
    FROM [Booking].[NightStayBookings] n
    CROSS JOIN (VALUES (N'PetParent'), (N'Provider')) AS a([Audience])
    WHERE n.[CheckInDate] BETWEEN @WindowFrom AND @WindowTo
      AND n.[Status] IN (SELECT [Status] FROM @Live)
      AND @Now >= DATEADD(MINUTE, -5,
              DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                      CAST(n.[CheckInDate] AS DATETIME2(7))))
      AND @Now < DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                         CAST(n.[CheckInDate] AS DATETIME2(7)));

    -- ================================================================
    -- 3. Half-way through the window, still not started  (P-S6 / V-S7) — both
    -- Single-day only: a stay has no hourly window to be half-way through.
    -- ================================================================
    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId], 0, N'BOOKING_NOT_STARTED_HALFWAY', a.[Audience]
    FROM @SingleDay s
    CROSS JOIN (VALUES (N'PetParent'), (N'Provider')) AS a([Audience])
    WHERE s.[Status] IN (SELECT [Status] FROM @Live)
      AND @Now >= DATEADD(SECOND, DATEDIFF(SECOND, s.[StartsAtUtc], s.[EndsAtUtc]) / 2, s.[StartsAtUtc])
      AND @Now < s.[EndsAtUtc];

    -- ================================================================
    -- 4. The whole window elapsed, still not started  (P-S7 / V-S8) — both
    -- Bounded by the provider's closing time, after which BR-38's no-show settle
    -- takes over and nagging would be wrong. No hours on file => unbounded, since
    -- there is no closing time to have passed.
    -- ================================================================
    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId], 0, N'BOOKING_NOT_STARTED_WINDOW_ENDED', a.[Audience]
    FROM @SingleDay s
    CROSS JOIN (VALUES (N'PetParent'), (N'Provider')) AS a([Audience])
    WHERE s.[Status] IN (SELECT [Status] FROM @Live)
      AND @Now >= s.[EndsAtUtc]
      AND (s.[ClosesAtUtc] IS NULL OR @Now < s.[ClosesAtUtc]);

    -- ================================================================
    -- 5. Start-code nudges  (P-S9 / P-S10 / V-S10)
    -- The booking sits in START_JOB: the provider tapped Start and the code was
    -- issued, but it has not been entered. Which message the PARENT gets depends
    -- on whether they have actually opened the code — [SeenAtUtc], stamped by
    -- Booking.IssueBookingStartOtp when their booking-detail read surfaces it.
    --   not seen  -> "You're late for your appointment"  (P-S9)
    --   seen      -> "Share your OTP now, or you'll be marked as a no-show" (P-S10)
    -- The PROVIDER always gets the one nudge (V-S10): from their side there is
    -- only one situation — they haven't entered a code.
    -- ================================================================
    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId],
           0,
           CASE WHEN o.[SeenAtUtc] IS NULL
                THEN N'BOOKING_START_OTP_NOT_SEEN'
                ELSE N'BOOKING_START_OTP_NOT_SHARED' END,
           N'PetParent'
    FROM @SingleDay s
    OUTER APPLY (
        SELECT TOP (1) [SeenAtUtc]
        FROM [Booking].[BookingStartOtps]
        WHERE [BookingId] = s.[BookingId]
        ORDER BY [IssuedAtUtc] DESC) o
    WHERE s.[Status] = N'START_JOB'
      AND @Now >= s.[EndsAtUtc]
      AND (s.[ClosesAtUtc] IS NULL OR @Now < s.[ClosesAtUtc]);

    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId], 0, N'BOOKING_START_OTP_NOT_ENTERED', N'Provider'
    FROM @SingleDay s
    WHERE s.[Status] = N'START_JOB'
      AND @Now >= s.[EndsAtUtc]
      AND (s.[ClosesAtUtc] IS NULL OR @Now < s.[ClosesAtUtc]);

    -- ================================================================
    -- 6. Pick-up and closure  (P-S13 / P-S14 / V-S12)
    -- The job IS under way (IN_PROGRESS), so these are about collecting the pet.
    --   completion time passed        -> parent  (P-S13)
    --   provider closing, pet still in -> parent (P-S14) + provider (V-S12)
    -- ================================================================
    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience])
    SELECT s.[BookingId], 0, N'BOOKING_COMPLETION_TIME_PASSED', N'PetParent'
    FROM @SingleDay s
    WHERE s.[Status] = N'IN_PROGRESS'
      AND @Now >= s.[EndsAtUtc]
      AND (s.[ClosesAtUtc] IS NULL OR @Now < s.[ClosesAtUtc]);

    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience], [ClosingAtUtc])
    SELECT s.[BookingId], 0, N'BOOKING_PICKUP_OVERDUE', N'PetParent', s.[ClosesAtUtc]
    FROM @SingleDay s
    WHERE s.[Status] = N'IN_PROGRESS'
      AND s.[ClosesAtUtc] IS NOT NULL
      AND @Now >= s.[ClosesAtUtc];

    INSERT INTO @Due ([BookingId], [IsNightStay], [NotificationType], [Audience], [ClosingAtUtc])
    SELECT s.[BookingId], 0, N'BOOKING_NOT_MARKED_COMPLETE', N'Provider', s.[ClosesAtUtc]
    FROM @SingleDay s
    WHERE s.[Status] = N'IN_PROGRESS'
      AND s.[ClosesAtUtc] IS NOT NULL
      AND @Now >= s.[ClosesAtUtc];

    -- ================================================================
    -- Enqueue. Every row here relies on the outbox's DedupeKey to collapse
    -- repeats across ticks — see the header.
    -- ================================================================
    DECLARE @BookingId UNIQUEIDENTIFIER;
    DECLARE @IsNightStay BIT;
    DECLARE @Type NVARCHAR(64);
    DECLARE @Audience NVARCHAR(16);
    DECLARE @ClosingAtUtc DATETIME2(0);

    DECLARE due_reminders CURSOR LOCAL FAST_FORWARD FOR
        SELECT [BookingId], [IsNightStay], [NotificationType], [Audience], [ClosingAtUtc] FROM @Due;

    OPEN due_reminders;
    FETCH NEXT FROM due_reminders INTO @BookingId, @IsNightStay, @Type, @Audience, @ClosingAtUtc;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = @Audience,
            @NotificationType = @Type,
            @ClosingAtUtc = @ClosingAtUtc;

        FETCH NEXT FROM due_reminders INTO @BookingId, @IsNightStay, @Type, @Audience, @ClosingAtUtc;
    END

    CLOSE due_reminders;
    DEALLOCATE due_reminders;

    SELECT
        (SELECT COUNT(*) FROM @Due WHERE [NotificationType] = N'BOOKING_REMINDER_DAY_BEFORE')
            AS [DayBeforeReminders],
        (SELECT COUNT(*) FROM @Due WHERE [NotificationType] = N'BOOKING_REMINDER_STARTING_SOON')
            AS [StartingSoonReminders],
        (SELECT COUNT(*) FROM @Due WHERE [NotificationType] IN
            (N'BOOKING_NOT_STARTED_HALFWAY', N'BOOKING_NOT_STARTED_WINDOW_ENDED'))
            AS [NotStartedNudges],
        (SELECT COUNT(*) FROM @Due WHERE [NotificationType] IN
            (N'BOOKING_START_OTP_NOT_SEEN', N'BOOKING_START_OTP_NOT_SHARED',
             N'BOOKING_START_OTP_NOT_ENTERED'))
            AS [StartOtpNudges],
        (SELECT COUNT(*) FROM @Due WHERE [NotificationType] IN
            (N'BOOKING_COMPLETION_TIME_PASSED', N'BOOKING_PICKUP_OVERDUE',
             N'BOOKING_NOT_MARKED_COMPLETE'))
            AS [PickupNudges];
END;
