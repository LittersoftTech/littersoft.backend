CREATE OR ALTER PROCEDURE [Booking].[GetBookingsForDate]
    @ProviderId UNIQUEIDENTIFIER,
    @BookingDate DATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Used by the slot service to subtract overlapping windows against capacity.
    SELECT [StartTime], [EndTime]
    FROM [Booking].[Bookings]
    WHERE [ProviderId] = @ProviderId
      AND [BookingDate] = @BookingDate
      AND [Status] = N'Confirmed'
    ORDER BY [StartTime];
END;
