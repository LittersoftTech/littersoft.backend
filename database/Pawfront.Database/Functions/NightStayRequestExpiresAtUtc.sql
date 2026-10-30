CREATE OR ALTER FUNCTION [Booking].[NightStayRequestExpiresAtUtc]
(
    @RequestedAtUtc DATETIME2(7),
    @CheckInDate DATE,
    @DropOffTime TIME(0) = NULL
)
RETURNS DATETIME2(7)
AS
BEGIN
    DECLARE @PendingExpiry DATETIME2(7) = DATEADD(HOUR, 24, @RequestedAtUtc);
    DECLARE @StartExpiry DATETIME2(7) = DATEADD(HOUR,-2,DATEADD(SECOND,
        DATEDIFF(SECOND,CAST('00:00' AS TIME),COALESCE(@DropOffTime,CAST('00:00' AS TIME))),CAST(@CheckInDate AS DATETIME2(7))));
    RETURN CASE WHEN @PendingExpiry < @StartExpiry THEN @PendingExpiry ELSE @StartExpiry END;
END;
