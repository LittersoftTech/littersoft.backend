CREATE OR ALTER PROCEDURE [Booking].[GetBookingsForDate]
    @ServiceId UNIQUEIDENTIFIER,
    @BookingDate DATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Used by the slot service to subtract overlapping confirmed bookings against
    -- the service's capacity. Scoped by ServiceId so DayCare and NightStay slot
    -- grids on the same provider are computed independently.
    SELECT [StartTime], [EndTime]
    FROM [Booking].[Bookings]
    WHERE [ServiceId] = @ServiceId
      AND [BookingDate] = @BookingDate
      AND [Status] = N'Confirmed'
    ORDER BY [StartTime];
END;
