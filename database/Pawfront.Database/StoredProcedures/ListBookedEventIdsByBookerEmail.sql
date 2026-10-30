CREATE OR ALTER PROCEDURE [Event].[ListBookedEventIdsByBookerEmail]
    @BookerEmail NVARCHAR(320), @BookerAccountId UNIQUEIDENTIFIER=NULL, @BookerType NVARCHAR(16)=NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Distinct events the caller currently holds tickets for. Cancelled
    -- bookings are excluded (cancelling frees the seat, so the event becomes
    -- bookable again). Powers the IsBookable flag on event list / detail reads.
    SELECT DISTINCT b.[EventId]
    FROM [Event].[EventBookings] AS b
    WHERE b.[BookerEmail] = @BookerEmail
      AND (@BookerAccountId IS NULL OR (b.BookerAccountId=@BookerAccountId AND b.BookerType=@BookerType))
      AND b.[Status] IN (N'Confirmed',N'Attended');
END;
