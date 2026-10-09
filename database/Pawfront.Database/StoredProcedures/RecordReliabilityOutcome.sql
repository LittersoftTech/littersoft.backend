CREATE OR ALTER PROCEDURE [Event].[RecordReliabilityOutcome]
    @BookingId UNIQUEIDENTIFIER, @Outcome NVARCHAR(16)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult = sys.sp_getapplock @Resource=N'EventLifecycle', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
    IF @LockResult < 0 THROW 51468, 'Event operation is busy. Retry the request.', 1;

    DECLARE @AccountId UNIQUEIDENTIFIER, @Now DATETIME2(7)=SYSUTCDATETIME();
    SELECT @AccountId=BookerAccountId FROM [Event].[EventBookings] WHERE BookingId=@BookingId;
    IF @AccountId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM [Event].[ReliabilityOutcomes] WHERE BookingId=@BookingId)
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM [Event].[Reliability] WHERE AccountId=@AccountId)
            INSERT [Event].[Reliability](AccountId) VALUES (@AccountId);
        UPDATE [Event].[Reliability] SET RedFlagExpiresAt=NULL, ConsecutivePaidEventsAttended=0,
            ConsecutiveNegativeMarks=0, LastNegativeAtUtc=NULL
        WHERE AccountId=@AccountId AND RedFlagExpiresAt <= @Now;
        INSERT [Event].[ReliabilityOutcomes](BookingId,AccountId,Outcome) VALUES (@BookingId,@AccountId,@Outcome);
        IF @Outcome=N'Negative'
        BEGIN
            UPDATE [Event].[Reliability] SET
                RedFlagExpiresAt=CASE WHEN RedFlagExpiresAt>@Now THEN RedFlagExpiresAt
                    WHEN ConsecutiveNegativeMarks=1 AND LastNegativeAtUtc>=DATEADD(day,-30,@Now) THEN DATEADD(day,30,@Now) ELSE NULL END,
                ConsecutiveNegativeMarks=CASE WHEN ConsecutiveNegativeMarks=1 AND LastNegativeAtUtc>=DATEADD(day,-30,@Now) THEN 0 ELSE 1 END,
                LastNegativeAtUtc=@Now, ConsecutivePaidEventsAttended=0
            WHERE AccountId=@AccountId;
        END
        ELSE
        BEGIN
            UPDATE [Event].[Reliability] SET
                RedFlagExpiresAt=CASE WHEN @Outcome=N'PaidAttendance' AND ConsecutivePaidEventsAttended=1 THEN NULL ELSE RedFlagExpiresAt END,
                ConsecutivePaidEventsAttended=CASE WHEN @Outcome=N'PaidAttendance' THEN CASE WHEN ConsecutivePaidEventsAttended<2 THEN ConsecutivePaidEventsAttended+1 ELSE 2 END ELSE 0 END,
                ConsecutiveNegativeMarks=0, LastNegativeAtUtc=NULL
            WHERE AccountId=@AccountId;
        END
    END

    COMMIT TRANSACTION;
END;
