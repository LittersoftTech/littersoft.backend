CREATE OR ALTER PROCEDURE [Event].[ListEventBookingsByBookerEmail]
    @BookerEmail NVARCHAR(320)
AS
BEGIN
    SET NOCOUNT ON;

    -- Single result set: booking row joined with its event so the mobile
    -- "my bookings" screen can render the card without a follow-up fetch.
    -- Ordered most-recent first. Cancelled bookings are intentionally
    -- included — the parent should see their full history.
    SELECT b.[BookingId],
           b.[EventId],
           e.[Title]            AS [EventTitle],
           e.[EventCategory],
           e.[StartDate]        AS [EventStartDate],
           e.[StartTime]        AS [EventStartTime],
           e.[BannerImageUrl]   AS [EventBannerImageUrl],
           b.[BookerName],
           b.[BookerEmail],
           b.[BookerMobile],
           b.[TicketCount],
           b.[PaymentMethod],
           b.[PaymentStatus],
           b.[PaymentReference],
           b.[TotalAmount],
           b.[Status],
           b.[CreatedAtUtc],
           b.[UpdatedAtUtc],
           b.[CancelledAtUtc]
    FROM [Event].[EventBookings] AS b
    INNER JOIN [Event].[Events] AS e
        ON e.[EventId] = b.[EventId]
    WHERE b.[BookerEmail] = @BookerEmail
    ORDER BY b.[CreatedAtUtc] DESC;
END;
