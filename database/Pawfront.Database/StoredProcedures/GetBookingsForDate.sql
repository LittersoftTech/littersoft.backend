CREATE OR ALTER PROCEDURE [Booking].[GetBookingsForDate]
    @ServiceId UNIQUEIDENTIFIER,
    @BookingDate DATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Used by the slot service to subtract overlapping active bookings against
    -- the service's capacity. Scoped by ServiceId so DayCare and NightStay slot
    -- grids on the same provider are computed independently. A booking holds its
    -- slot in every status except the two cancelled ones and PROVIDER_DECLINED.
    SELECT [StartTime], [EndTime]
    FROM [Booking].[Bookings]
    WHERE [ServiceId] = @ServiceId
      AND [BookingDate] = @BookingDate
      AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED')
    ORDER BY [StartTime];
END;
