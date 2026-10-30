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
    -- Free events produce no mark and do not break either sequence.
    IF @AccountId IS NOT NULL AND @Outcome IN (N'Negative', N'PaidAttendance')
       AND EXISTS (SELECT 1 FROM [Event].[EventBookings] b JOIN [Event].[Events] e ON e.EventId=b.EventId
           WHERE b.BookingId=@BookingId AND e.IsPaid=1 AND e.EventStatus=N'Active')
       AND NOT EXISTS (SELECT 1 FROM [Event].[ReliabilityOutcomes] WHERE BookingId=@BookingId)
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM [Event].[Reliability] WHERE AccountId=@AccountId)
            INSERT [Event].[Reliability](AccountId) VALUES (@AccountId);
        DECLARE @OldExpiry DATETIME2(7)=(SELECT RedFlagExpiresAt FROM [Event].[Reliability] WHERE AccountId=@AccountId);
        UPDATE [Event].[Reliability] SET RedFlagExpiresAt=NULL, ConsecutivePaidEventsAttended=0,
            ConsecutiveNegativeMarks=0, LastNegativeAtUtc=NULL
        WHERE AccountId=@AccountId AND RedFlagExpiresAt <= @Now;
        INSERT [Event].[ReliabilityOutcomes](BookingId,AccountId,Outcome) VALUES (@BookingId,@AccountId,@Outcome);
        IF @Outcome=N'Negative'
        BEGIN
            UPDATE [Event].[Reliability] SET
                RedFlagExpiresAt=CASE WHEN RedFlagExpiresAt>@Now THEN DATEADD(day,30,@Now)
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
        -- Expiry and two consecutive paid attendances are automatic removals,
        -- even if this procedure was reached while handling another action.
        IF @OldExpiry IS NOT NULL AND (@OldExpiry<=@Now OR NOT EXISTS (
            SELECT 1 FROM [Event].[Reliability] WHERE AccountId=@AccountId AND RedFlagExpiresAt IS NOT NULL))
        BEGIN
            DECLARE @EventId UNIQUEIDENTIFIER,@Audience NVARCHAR(16),@Key NVARCHAR(200);
            SELECT @EventId=EventId,@Audience=BookerType FROM [Event].[EventBookings] WHERE BookingId=@BookingId;
            SET @Key=CONCAT(N'EVENT_PROFILE_FLAG_REMOVED:',@AccountId,N':',CONVERT(NVARCHAR(33),@OldExpiry,126));
            EXEC [Event].[NotifyEventAccount] @EventId,@AccountId,@Audience,N'EVENT_PROFILE_FLAG_REMOVED',@Key;
        END
    END

    COMMIT TRANSACTION;
END;
