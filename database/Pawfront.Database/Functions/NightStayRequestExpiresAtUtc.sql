CREATE OR ALTER FUNCTION [Booking].[NightStayRequestExpiresAtUtc]
(
    @RequestedAtUtc DATETIME2(7),
    @CheckInDate DATE
)
RETURNS DATETIME2(7)
AS
BEGIN
    DECLARE @PendingExpiry DATETIME2(7) = DATEADD(HOUR, 24, @RequestedAtUtc);
    DECLARE @DateExpiry DATETIME2(7) = DATEADD(DAY, 1, CAST(@CheckInDate AS DATETIME2(7)));
    RETURN CASE WHEN @PendingExpiry < @DateExpiry THEN @PendingExpiry ELSE @DateExpiry END;
END;
