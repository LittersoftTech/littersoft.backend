-- Per-night occupancy for a NightStay service: how many active stays (not
-- cancelled/declined) cover each night in [@FromNight, @ToNight] (inclusive —
-- each row is a STAYED night; a stay covers night n when
-- CheckInDate <= n < effective checkout). Backs the NightStay availability
-- surface, which is date-granular: capacity is per night, not per time window.
-- Every night in the range is returned, zero occupancy included.
--
-- The effective checkout is COALESCE([ActualCheckOutDate], [CheckOutDate]): a
-- stay that ENDED EARLY stops holding a place from the day the pet actually
-- went home, so a 3-night booking collected a day early offers that last night
-- back to other parents. [ActualCheckOutDate] is NULL unless the stay completed
-- early, so nothing else is affected — and the BOOKED [CheckOutDate] is
-- untouched, still what the stay is priced and read against.
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
       AND b.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
       AND b.[CheckInDate] <= n.[Night]
       AND COALESCE(b.[ActualCheckOutDate], b.[CheckOutDate]) > n.[Night]
    GROUP BY n.[Night]
    ORDER BY n.[Night]
    OPTION (MAXRECURSION 366);
END;
