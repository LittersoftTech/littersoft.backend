CREATE OR ALTER FUNCTION [Booking].[RequestExpiresAtUtc]
(
    @RequestedAtUtc DATETIME2(7),
    @OriginalDate DATE,
    @OriginalTime TIME(0),
    @ProposedDate DATE,
    @ProposedTime TIME(0)
)
RETURNS DATETIME2(7)
AS
BEGIN
    DECLARE @Deadline DATETIME2(7) = DATEADD(HOUR, 24, @RequestedAtUtc);
    DECLARE @OriginalCutoff DATETIME2(7) = DATEADD(HOUR, -2,
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00' AS TIME), @OriginalTime), CAST(@OriginalDate AS DATETIME2(7))));
    DECLARE @ProposedCutoff DATETIME2(7) = DATEADD(HOUR, -2,
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00' AS TIME), @ProposedTime), CAST(@ProposedDate AS DATETIME2(7))));
    IF @OriginalCutoff < @Deadline SET @Deadline = @OriginalCutoff;
    IF @ProposedCutoff < @Deadline SET @Deadline = @ProposedCutoff;
    RETURN @Deadline;
END;
