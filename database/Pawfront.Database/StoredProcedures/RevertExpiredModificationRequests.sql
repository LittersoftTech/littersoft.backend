-- BR-30 (widened 2026-08-02 to cover BOTH proposal directions — previously
-- PARENT-only, see the retired [[BR-31]] note in docs/booking-rules.md): an
-- unanswered modification proposal, from EITHER party, expires — the staging row
-- is discarded and the booking REVERTS to CONFIRMED (NOT terminal: it goes back
-- into a startable state so neither party is left blocked by a stale proposal).
--
-- TWO DEADLINES, whichever arrives FIRST (2026-08-04):
--   * the 24-hour review window     — [BookingModifications].[CreatedAtUtc] + 24h
--   * the 2-hour pre-service cutoff — serviceStart - 2h
--     (BookingDate + StartTime for a single-day booking, CheckInDate +
--     DropOffTime for a stay; all UTC)
--
-- The second is the one that governs SHORT-NOTICE bookings, where a full 24-hour
-- review window does not fit before the service. The first governs everything
-- else: a proposal on a booking three weeks out would otherwise sit unanswered
-- for weeks, blocking /start-job, because MODIFICATION_REQUEST_BY_* is not a
-- confirmed-equivalent status.
--
-- Which arm fired is recorded in the audit note AND decides the notification
-- type, because the two read differently to a user: "timed out" (nobody replied
-- in a day) versus "expired — modifications close 2 hours before the service".
--
-- Run periodically by the scheduled external job, BEFORE
-- Booking.SettleUnstartedJobsAsNoShow in the same tick — a booking whose
-- proposal expires AND whose provider's working day has also ended should
-- settle as a no-show in that same pass rather than waiting for the next one.
--
-- The status-engine's respond sprocs (RespondBookingModification /
-- RespondNightStayBookingModification) REJECT a response landing after either
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

    DECLARE @TimedOutNote NVARCHAR(500) =
        N'Modification request timed out: 24 hours passed with no response.';
    DECLARE @CutoffNote NVARCHAR(500) =
        N'Modification request expired unanswered 2 hours before the service start time.';

    -- FromStatus is captured per row (not a hardcoded literal) since a reverted
    -- booking may have come from either MODIFICATION_REQUEST_BY_PARENT or
    -- MODIFICATION_REQUEST_BY_PROVIDER. [Arm] records WHICH deadline fired.
    DECLARE @RevertedBookings TABLE (
        [BookingId] UNIQUEIDENTIFIER NOT NULL,
        [FromStatus] NVARCHAR(48) NOT NULL,
        [Arm] NVARCHAR(16) NOT NULL);
    DECLARE @RevertedNightStays TABLE (
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
        [FromStatus] NVARCHAR(48) NOT NULL,
        [Arm] NVARCHAR(16) NOT NULL);

    BEGIN TRANSACTION;

    -- --- Single-day -------------------------------------------------------
    -- The arm is decided BEFORE the update, from the staging row's age, because
    -- the staging row is deleted below and OUTPUT cannot see the joined table.
    -- A booking whose 24h lapsed AND whose service is under 2h away reports
    -- 'Cutoff': it is the more specific explanation, and the one the app's own
    -- "modifications close 2 hours before" copy already tells the user about.
    DECLARE @ExpiredSingleDay TABLE (
        [BookingId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        [Arm] NVARCHAR(16) NOT NULL);

    INSERT INTO @ExpiredSingleDay ([BookingId], [Arm])
    SELECT b.[BookingId],
           CASE
               WHEN @Now >= DATEADD(HOUR, -2,
                        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), b.[StartTime]),
                                CAST(b.[BookingDate] AS DATETIME2(7))))
               THEN N'Cutoff'
               ELSE N'TimedOut'
           END
    FROM [Booking].[Bookings] b
    INNER JOIN [Booking].[BookingModifications] m ON m.[BookingId] = b.[BookingId]
    WHERE b.[Status] IN (N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER')
      AND (
            -- 2-hour pre-service cutoff
            @Now >= DATEADD(HOUR, -2,
                DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), b.[StartTime]),
                        CAST(b.[BookingDate] AS DATETIME2(7))))
            -- 24-hour review window
            OR @Now >= DATEADD(HOUR, 24, m.[CreatedAtUtc])
          );

    UPDATE b
    SET [Status] = N'CONFIRMED',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[BookingId], deleted.[Status], e.[Arm] INTO @RevertedBookings
    FROM [Booking].[Bookings] b
    INNER JOIN @ExpiredSingleDay e ON e.[BookingId] = b.[BookingId];

    DELETE m
    FROM [Booking].[BookingModifications] m
    INNER JOIN @RevertedBookings r ON r.[BookingId] = m.[BookingId];

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [BookingId], [FromStatus], N'CONFIRMED', N'System', NULL,
           CASE WHEN [Arm] = N'Cutoff' THEN @CutoffNote ELSE @TimedOutNote END
    FROM @RevertedBookings;

    -- --- Night stay -------------------------------------------------------
    DECLARE @ExpiredNightStay TABLE (
        [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        [Arm] NVARCHAR(16) NOT NULL);

    INSERT INTO @ExpiredNightStay ([NightStayBookingId], [Arm])
    SELECT n.[NightStayBookingId],
           CASE
               WHEN @Now >= DATEADD(HOUR, -2,
                        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                                CAST(n.[CheckInDate] AS DATETIME2(7))))
               THEN N'Cutoff'
               ELSE N'TimedOut'
           END
    FROM [Booking].[NightStayBookings] n
    INNER JOIN [Booking].[NightStayBookingModifications] m
        ON m.[NightStayBookingId] = n.[NightStayBookingId]
    WHERE n.[Status] IN (N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER')
      AND (
            @Now >= DATEADD(HOUR, -2,
                DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), n.[DropOffTime]),
                        CAST(n.[CheckInDate] AS DATETIME2(7))))
            OR @Now >= DATEADD(HOUR, 24, m.[CreatedAtUtc])
          );

    UPDATE n
    SET [Status] = N'CONFIRMED',
        [UpdatedAtUtc] = @Now
    OUTPUT inserted.[NightStayBookingId], deleted.[Status], e.[Arm] INTO @RevertedNightStays
    FROM [Booking].[NightStayBookings] n
    INNER JOIN @ExpiredNightStay e ON e.[NightStayBookingId] = n.[NightStayBookingId];

    DELETE m
    FROM [Booking].[NightStayBookingModifications] m
    INNER JOIN @RevertedNightStays r ON r.[NightStayBookingId] = m.[NightStayBookingId];

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    SELECT [NightStayBookingId], [FromStatus], N'CONFIRMED', N'System', NULL,
           CASE WHEN [Arm] = N'Cutoff' THEN @CutoffNote ELSE @TimedOutNote END
    FROM @RevertedNightStays;

    -- --- Notify BOTH parties ----------------------------------------------
    -- Nobody tapped anything here — the system decided it — so the relevance rule
    -- puts it on both apps (V3 cards P-S2/V-S3 for the 24-hour arm, P-S3/V-S4 for
    -- the 2-hour cutoff). Cursor rather than a set-based insert because
    -- EnqueueBookingNotification is a sproc: it builds the whole data object per
    -- booking, and duplicating that JSON here is exactly the drift the helper
    -- exists to prevent. Volumes are single-digit per tick.
    DECLARE @BookingId UNIQUEIDENTIFIER;
    DECLARE @Arm NVARCHAR(16);
    DECLARE @Type NVARCHAR(64);
    DECLARE @IsNightStay BIT;

    DECLARE expired_modifications CURSOR LOCAL FAST_FORWARD FOR
        SELECT [BookingId], [Arm], 0 FROM @RevertedBookings
        UNION ALL
        SELECT [NightStayBookingId], [Arm], 1 FROM @RevertedNightStays;

    OPEN expired_modifications;
    FETCH NEXT FROM expired_modifications INTO @BookingId, @Arm, @IsNightStay;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @Type = CASE WHEN @Arm = N'Cutoff'
                         THEN N'BOOKING_MODIFICATION_EXPIRED'
                         ELSE N'BOOKING_MODIFICATION_TIMED_OUT' END;

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = N'PetParent',
            @NotificationType = @Type;

        EXEC [Notification].[EnqueueBookingNotification]
            @BookingId = @BookingId,
            @IsNightStay = @IsNightStay,
            @Audience = N'Provider',
            @NotificationType = @Type;

        FETCH NEXT FROM expired_modifications INTO @BookingId, @Arm, @IsNightStay;
    END

    CLOSE expired_modifications;
    DEALLOCATE expired_modifications;

    COMMIT TRANSACTION;

    SELECT
        (SELECT COUNT(*) FROM @RevertedBookings) AS [RevertedBookings],
        (SELECT COUNT(*) FROM @RevertedNightStays) AS [RevertedNightStayBookings];
END;
