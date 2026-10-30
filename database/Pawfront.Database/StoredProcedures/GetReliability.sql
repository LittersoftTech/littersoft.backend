CREATE OR ALTER PROCEDURE [Event].[GetReliability]
    @AccountId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7)=SYSUTCDATETIME();
    SELECT CAST(CASE WHEN r.RedFlagExpiresAt>@Now THEN 1 ELSE 0 END AS BIT) AS IsRedFlagged,
        CASE WHEN r.RedFlagExpiresAt>@Now THEN r.RedFlagExpiresAt END AS RedFlagExpiresAt,
        CASE WHEN r.RedFlagExpiresAt<=@Now THEN 0 ELSE ISNULL(r.ConsecutivePaidEventsAttended,0) END AS ConsecutivePaidEventsAttended,
        (SELECT COUNT(*) FROM [Event].[EventBookings] b JOIN [Event].[Events] e ON e.EventId=b.EventId
         WHERE b.BookerAccountId=@AccountId AND b.Status IN (N'Confirmed',N'Attended') AND e.EventStatus=N'Active'
           AND [Event].[EventStartUtc](DATEADD(day,1,e.EndDate),'00:00')>@Now) AS ActiveBookingCount,
        CASE WHEN r.LastNegativeAtUtc>=DATEADD(day,-30,@Now) AND (r.RedFlagExpiresAt IS NULL OR r.RedFlagExpiresAt>@Now)
            THEN ISNULL(r.ConsecutiveNegativeMarks,0) ELSE 0 END AS ConsecutiveNegativeMarks,
        (SELECT COUNT(*) FROM [Event].[EventBookings] b JOIN [Event].[Events] e ON e.EventId=b.EventId
         WHERE b.BookerAccountId=@AccountId AND b.Status IN (N'Confirmed',N'Attended') AND e.EventStatus=N'Active'
           AND e.IsPaid=1 AND [Event].[EventStartUtc](e.StartDate,e.StartTime)>@Now) AS ActivePaidBookingCount
    FROM (SELECT @AccountId AS Id) a LEFT JOIN [Event].[Reliability] r ON r.AccountId=a.Id;

END;
