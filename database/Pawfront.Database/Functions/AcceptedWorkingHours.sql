-- Freeze the outer working day as UTC instants, preserving the local date and
-- DST offset that applied to the accepted slot. Breaks do not limit an
-- already accepted job. Providers without saved hours retain a full UTC day.
CREATE OR ALTER FUNCTION [Booking].[AcceptedWorkingHours]
(
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceDate DATE,
    @StartTime TIME(0)
)
RETURNS TABLE
AS
RETURN
(
    SELECT [OpensAtUtc] = COALESCE(w.[OpensAtUtc], CAST(@ServiceDate AS DATETIME2(7))),
           [ClosesAtUtc] = COALESCE(w.[ClosesAtUtc], DATEADD(DAY, 1, CAST(@ServiceDate AS DATETIME2(7))))
    FROM (SELECT DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00' AS TIME), COALESCE(@StartTime, CAST('00:00' AS TIME))),
                         CAST(@ServiceDate AS DATETIME2(7))) AS [StartsAtUtc]) slot
    OUTER APPLY [Provider].[WorkingHoursAtUtc](@ProviderId, slot.[StartsAtUtc]) w
);
