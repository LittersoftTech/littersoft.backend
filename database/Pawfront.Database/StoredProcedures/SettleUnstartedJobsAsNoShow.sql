-- BR-38 (revised 2026-08-02): an accepted job that is still unstarted once the
-- PROVIDER'S WORKING DAY ends settles itself as a no-show. Blame is read from
-- the only evidence the system has, not guessed:
--   * sitting in START_JOB      -> PARENT_NO_SHOW  (the provider was there and
--     had the start code issued to the parent, who never handed it back)
--   * still confirmed-equivalent -> PROVIDER_NO_SHOW (the provider never so
--     much as tapped Start)
--
-- Single-day cutoff = the LATER of (a) the provider's closing time on the
-- booking date, from Provider.ProviderWeeklyAvailability for that weekday, and
-- (b) the booking's own EndTime. Keying off closing time (not the booking's own
-- end) is the point: a provider running badly late has not no-showed just
-- because a slot came and went — they still have the rest of their day to serve
-- it. Taking the LATER of the two guards against a provider who has narrowed
-- their hours since the booking was made: a booking is never settled while its
-- own window is still running. No weekly-availability row for that weekday, or
-- one marked closed, falls back to midnight UTC at the end of the booking date
-- (the calendar day is the only "day" there is to know about). The break window
-- is not consulted, matching the working-hours gate on /start-job.
--
-- Night-stay cutoff is UNCHANGED: the check-in day ends (midnight UTC). A stay
-- is date-granular (BR-06) and the weekly time grid never governs it, so there
-- is no "working day" to key off — midnight already is the end of the day.
--
-- 1970-01-04 was a Sunday, so DATEDIFF(DAY, '19700104', <date>) % 7 gives
-- 0 = Sunday, matching System.DayOfWeek / the [DayOfWeek] column, independently
-- of the server's DATEFIRST setting (same trick used in StartBooking.sql /
-- StartNightStayBooking.sql).
--
-- Run periodically by the scheduled external job, AFTER
-- Booking.RevertExpiredModificationRequests in the same tick, so a
-- booking whose proposal expires AND whose provider's working day has also
-- ended settles as a no-show in the same pass. No no-show equivalent of the
-- 51128/51248 grace-window guard exists in the status engine for this
-- auto-settlement path — reporting manually stays the fast path (30+ minutes /
-- 2+ hours after the scheduled start); this sproc is purely the backstop for
-- when neither party bothers.
-- Returns one row: (ProviderNoShowBookings, ParentNoShowBookings,
--                   ProviderNoShowNightStayBookings, ParentNoShowNightStayBookings).
CREATE OR ALTER PROCEDURE [Booking].[SettleUnstartedJobsAsNoShow]
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Today DATE = CAST(@Now AS DATE);
    DECLARE @ProviderNoShowJobNote NVARCHAR(500) =
        N'Automatically marked: the provider never started the job before their working day ended.';
    DECLARE @ParentNoShowJobNote NVARCHAR(500) =
        N'Automatically marked: the start code was issued but never verified before the provider''s working day ended.';
    DECLARE @ProviderNoShowStayNote NVARCHAR(500) =
        N'Automatically marked: the provider never started the stay on the check-in day.';
    DECLARE @ParentNoShowStayNote NVARCHAR(500) =
        N'Automatically marked: the start code was issued on the check-in day but never verified.';

    DECLARE @NoShowBookings TABLE (
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [FromStatus] NVARCHAR(48) NOT NULL,
        [ToStatus] NVARCHAR(48) NOT NULL);
    DECLARE @NoShowNightStays TABLE (
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
        [FromStatus] NVARCHAR(48) NOT NULL,
        [ToStatus] NVARCHAR(48) NOT NULL);

    BEGIN TRANSACTION;

    -- Single-day: cutoff = the later of the provider's closing time on
    -- BookingDate and the booking's own EndTime.
    UPDATE b
    SET [Status] = CASE WHEN b.[Status] = N'START_JOB' THEN N'PARENT_NO_SHOW' ELSE N'PROVIDER_NO_SHOW' END,
        -- Nobody performed and nobody owes, so the payout is settled terminally
        -- rather than left reading 'Pending'. Same value the manual report writes
        -- in Booking.UpdateBookingStatus — a settled no-show must look identical
        -- whether a party tapped it or this job derived it.
        [PayoutStatus] = N'NO_PAYOUT',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[BookingId], deleted.[Status], inserted.[Status] INTO @NoShowBookings
    FROM [Booking].[Bookings] b
    LEFT JOIN [Provider].[ProviderWeeklyAvailability] wa
        ON wa.[ProviderId] = b.[ProviderId]
       AND wa.[DayOfWeek] = CAST(DATEDIFF(DAY, '19700104', b.[BookingDate]) % 7 AS TINYINT)
    CROSS APPLY (
        SELECT
            [BookingEndsAtUtc] =
                DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), b.[EndTime]),
                        CAST(b.[BookingDate] AS DATETIME2(7))),
            [ProviderClosesAtUtc] = CASE
                WHEN wa.[IsOpen] = 1
                    THEN DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), wa.[EndTime]),
                                 CAST(b.[BookingDate] AS DATETIME2(7)))
                ELSE DATEADD(DAY, 1, CAST(b.[BookingDate] AS DATETIME2(7)))
            END
    ) AS [Cutoffs]
    WHERE b.[Status] IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION', N'PARENT_ACCEPTED_MODIFICATION',
                         N'PROVIDER_DECLINED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION', N'START_JOB')
      -- A Custom walk-in is NEVER settled here (2026-08-24). A no-show is a
      -- statement that one PARTY failed to appear, and a walk-in has only one
      -- party: the provider recording their own job. Settling it marked the
      -- provider a no-show — and stamped NO_PAYOUT — for work they had actually
      -- done, purely because the walk-in had no way to be started. (It now has
      -- one: [Booking].[StartBooking] takes it straight to IN_PROGRESS.) A walk-in
      -- the provider simply never finishes now rests at CONFIRMED, which is honest
      -- — nobody was stood up — and stays out of every earnings figure until they
      -- complete it.
      AND b.[Source] <> N'Custom'
      AND @Now >= (CASE WHEN [Cutoffs].[BookingEndsAtUtc] >= [Cutoffs].[ProviderClosesAtUtc]
                        THEN [Cutoffs].[BookingEndsAtUtc] ELSE [Cutoffs].[ProviderClosesAtUtc] END);

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [BookingId], [FromStatus], [ToStatus], N'System', NULL,
           CASE WHEN [ToStatus] = N'PARENT_NO_SHOW' THEN @ParentNoShowJobNote ELSE @ProviderNoShowJobNote END
    FROM @NoShowBookings;

    -- Night stay: unchanged — check-in day ended (midnight UTC), no working-
    -- hours join, mirroring the retired sweep exactly.
    UPDATE [Booking].[NightStayBookings]
    SET [Status] = CASE
            WHEN [Status] = N'START_JOB' THEN N'PARENT_NO_SHOW'
            ELSE N'PROVIDER_NO_SHOW'
        END,
        [PayoutStatus] = N'NO_PAYOUT',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[NightStayBookingId], deleted.[Status], inserted.[Status] INTO @NoShowNightStays
    WHERE [Status] IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION', N'PARENT_ACCEPTED_MODIFICATION',
                       N'PROVIDER_DECLINED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION', N'START_JOB')
      AND [CheckInDate] < @Today;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [NightStayBookingId], [FromStatus], [ToStatus], N'System', NULL,
           CASE WHEN [ToStatus] = N'PARENT_NO_SHOW' THEN @ParentNoShowStayNote ELSE @ProviderNoShowStayNote END
    FROM @NoShowNightStays;

    -- BOTH parties are told, because nobody reported it — the system derived it
    -- from the job never starting (V3 cards P-S8/V-S9 and P-S12/V-S11). Contrast
    -- Booking.UpdateBookingStatus, where a party REPORTS the no-show and only the
    -- counterparty hears about it.
    DECLARE @BookingId UNIQUEIDENTIFIER;
    DECLARE @SettledStatus NVARCHAR(48);
    DECLARE @IsNightStay BIT;
    DECLARE @AbsentParty NVARCHAR(32);

    DECLARE settled_no_shows CURSOR LOCAL FAST_FORWARD FOR
        SELECT [BookingId], [ToStatus], 0 FROM @NoShowBookings
        UNION ALL
        SELECT [NightStayBookingId], [ToStatus], 1 FROM @NoShowNightStays;

    OPEN settled_no_shows;
    FETCH NEXT FROM settled_no_shows INTO @BookingId, @SettledStatus, @IsNightStay;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        -- Naming the absent party is what lets one template read correctly on
        -- both apps and in either direction.
        SET @AbsentParty = CASE WHEN @SettledStatus = N'PARENT_NO_SHOW'
                                THEN N'the customer' ELSE N'the provider' END;

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = N'PetParent',
            @NotificationType = N'BOOKING_NO_SHOW_AUTO_SETTLED',
            @AbsentParty = @AbsentParty;

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = N'Provider',
            @NotificationType = N'BOOKING_NO_SHOW_AUTO_SETTLED',
            @AbsentParty = @AbsentParty;

        FETCH NEXT FROM settled_no_shows INTO @BookingId, @SettledStatus, @IsNightStay;
    END

    CLOSE settled_no_shows;
    DEALLOCATE settled_no_shows;

    COMMIT TRANSACTION;

    SELECT
        (SELECT COUNT(*) FROM @NoShowBookings WHERE [ToStatus] = N'PROVIDER_NO_SHOW')
            AS [ProviderNoShowBookings],
        (SELECT COUNT(*) FROM @NoShowBookings WHERE [ToStatus] = N'PARENT_NO_SHOW')
            AS [ParentNoShowBookings],
        (SELECT COUNT(*) FROM @NoShowNightStays WHERE [ToStatus] = N'PROVIDER_NO_SHOW')
            AS [ProviderNoShowNightStayBookings],
        (SELECT COUNT(*) FROM @NoShowNightStays WHERE [ToStatus] = N'PARENT_NO_SHOW')
            AS [ParentNoShowNightStayBookings];
END;
