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
    --
    -- Night-stay bookings live in [Booking].[NightStayBookings], not [Bookings];
    -- a stay covering this date (CheckInDate <= date < CheckOutDate) occupies its
    -- NightStay bucket for the WHOLE night, so it is surfaced as a full-day
    -- window. Rows only match a NightStay ServiceId, so DayCare & co. see none.
    SELECT [StartTime], [EndTime]
    FROM [Booking].[Bookings]
    WHERE [ServiceId] = @ServiceId
      AND [BookingDate] = @BookingDate
      AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')

    UNION ALL

    SELECT CAST(N'00:00:00' AS TIME(0)) AS [StartTime],
           CAST(N'23:59:59' AS TIME(0)) AS [EndTime]
    FROM [Booking].[NightStayBookings]
    WHERE [ServiceId] = @ServiceId
      AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
      AND [CheckInDate] <= @BookingDate
      AND [CheckOutDate] > @BookingDate

    ORDER BY [StartTime];
END;
