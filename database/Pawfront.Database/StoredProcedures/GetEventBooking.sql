-- Returns a single event booking plus all of its ticket rows. The mobile
-- client renders one entry per ticket from result set 2 — if 4 tickets were
-- bought, 4 rows come back here.
CREATE OR ALTER PROCEDURE [Event].[GetEventBooking]
    @BookingId UNIQUEIDENTIFIER, @BookerAccountId UNIQUEIDENTIFIER=NULL, @BookerType NVARCHAR(16)=NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Result set 1: booking row (zero or one).
    SELECT [BookingId],
           [EventId],
           [BookerName],
           [BookerEmail],
           [BookerMobile],
           [TicketCount],
           [PaymentMethod],
           [PaymentStatus],
           [PaymentReference],
           [TotalAmount],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           ISNULL((SELECT CASE WHEN LastNegativeAtUtc>=DATEADD(day,-30,SYSUTCDATETIME()) AND (RedFlagExpiresAt IS NULL OR RedFlagExpiresAt>SYSUTCDATETIME()) THEN ConsecutiveNegativeMarks ELSE 0 END
             FROM [Event].[Reliability] r WHERE r.AccountId=b.BookerAccountId),0) AS ConsecutiveNegativeMarks,
           CAST(CASE WHEN EXISTS (SELECT 1 FROM [Support].[Tickets] t WHERE t.EventBookingId=b.BookingId) THEN 1 ELSE 0 END AS BIT) AS HasReported
    FROM [Event].[EventBookings] b
    WHERE [BookingId] = @BookingId
      AND (@BookerAccountId IS NULL OR EXISTS (SELECT 1 FROM [Event].[EventBookings] b
          WHERE b.BookingId=@BookingId AND b.BookerAccountId=@BookerAccountId AND b.BookerType=@BookerType));

    -- Result set 2: tickets (zero or more, ordered by TicketNumber).
    SELECT [TicketId], [BookingId], [EventId], [TicketNumber], [AttendeeName], [CreatedAtUtc], PaymentStatus,
        CAST((SELECT TotalAmount/TicketCount FROM [Event].[EventBookings] WHERE BookingId=@BookingId) AS DECIMAL(18,2)) AS Amount
    FROM [Event].[EventBookingTickets]
    WHERE [BookingId] = @BookingId
      AND (@BookerAccountId IS NULL OR EXISTS (SELECT 1 FROM [Event].[EventBookings] b
          WHERE b.BookingId=@BookingId AND b.BookerAccountId=@BookerAccountId AND b.BookerType=@BookerType))
    ORDER BY [TicketNumber];
END;
