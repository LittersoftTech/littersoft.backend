-- The sweep's backstop: which paid bookings still have an invoice waiting?
--
-- This is what closes the one hole in the design. The queue message that triggers
-- rendering is sent from C# AFTER [Booking].[MarkBookingPaid] commits, so a host
-- crash in that window loses it — and nothing else would ever notice, because the
-- booking is legitimately PAID. The 'Pending' rows written inside that same
-- transaction cannot be lost, so scanning for them recovers exactly the work the
-- queue dropped.
--
-- It also recovers a renderer that died holding a lease ('Generating' past its
-- [NextAttemptAtUtc]) and a message that failed transiently and backed off.
--
-- Returns DISTINCT BOOKINGS, not invoices, because the queue message addresses a
-- booking — re-enqueuing one message per invoice would render each document twice.
--
-- A grace period keeps the sweep out of the fast path's way: a booking paid
-- seconds ago is almost certainly mid-render, and re-enqueuing it would just
-- collide on the claim. Only work the queue has demonstrably not picked up is
-- swept.
CREATE OR ALTER PROCEDURE [Billing].[ListUnrenderedInvoiceBookings]
    @GraceMinutes INT = 5,
    @MaxAttempts INT = 5,
    @BatchSize INT = 100
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Cutoff DATETIME2(7) = DATEADD(MINUTE, -@GraceMinutes, @Now);

    SELECT TOP (@BatchSize)
           [BookingType],
           [BookingId],
           [WaitingSince] = MIN([CreatedAtUtc]),
           [Attempts] = MAX([AttemptCount])
    FROM [Billing].[Invoices]
    WHERE [Status] IN (N'Pending', N'Generating')
      AND [NextAttemptAtUtc] <= @Now
      AND [CreatedAtUtc] <= @Cutoff
      AND [AttemptCount] < @MaxAttempts
    GROUP BY [BookingType], [BookingId]
    ORDER BY MIN([CreatedAtUtc]) ASC;
END;
