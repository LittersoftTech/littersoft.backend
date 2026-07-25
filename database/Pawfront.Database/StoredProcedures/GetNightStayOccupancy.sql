-- Per-night occupancy for a NightStay service: how many active stays (not
-- cancelled/declined) cover each night in [@FromNight, @ToNight] (inclusive —
-- each row is a STAYED night; a stay covers night n when
-- CheckInDate <= n < CheckOutDate). Backs the NightStay availability surface,
-- which is date-granular: capacity is per night, not per time window. Every
-- night in the range is returned, zero occupancy included.
CREATE OR ALTER PROCEDURE [Booking].[GetNightStayOccupancy]
    @ServiceId UNIQUEIDENTIFIER,
    @FromNight DATE,
    @ToNight   DATE
AS
BEGIN
    SET NOCOUNT ON;

    ;WITH [Nights] AS
    (
        SELECT @FromNight AS [Night]
        UNION ALL
        SELECT DATEADD(DAY, 1, [Night])
        FROM [Nights]
        WHERE [Night] < @ToNight
    )
    SELECT n.[Night],
           COUNT(b.[NightStayBookingId]) AS [ActiveStays]
    FROM [Nights] n
    LEFT JOIN [Booking].[NightStayBookings] b
        ON b.[ServiceId] = @ServiceId
       AND b.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_ATTEMPTS_EXCEEDED')
       AND b.[CheckInDate] <= n.[Night]
       AND b.[CheckOutDate] > n.[Night]
    GROUP BY n.[Night]
    ORDER BY n.[Night]
    OPTION (MAXRECURSION 366);
END;
