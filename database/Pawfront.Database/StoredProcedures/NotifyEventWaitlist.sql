CREATE OR ALTER PROCEDURE [Event].[NotifyEventWaitlist]
    @EventId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult = sys.sp_getapplock @Resource=N'EventLifecycle', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
    IF @LockResult < 0 THROW 51468, 'Event operation is busy. Retry the request.', 1;

    DELETE w FROM [Event].[Waitlist] w JOIN [Event].[Events] e ON e.EventId=w.EventId
    WHERE w.EventId=@EventId AND [Event].[EventStartUtc](e.StartDate,e.StartTime)<=SYSUTCDATETIME();

    IF EXISTS (SELECT 1 FROM [Event].[Events] e WHERE e.EventId=@EventId AND e.EventStatus=N'Active'
        AND DATEADD(hour,-1,[Event].[EventStartUtc](e.StartDate,e.StartTime))>SYSUTCDATETIME()
        AND (e.MaximumCapacity IS NULL OR e.MaximumCapacity >
            (SELECT ISNULL(SUM(TicketCount),0) FROM [Event].[EventBookings]
             WHERE EventId=@EventId AND Status IN (N'Confirmed',N'Attended'))))
    BEGIN
        DECLARE @Recipients TABLE (PetParentId UNIQUEIDENTIFIER);
        UPDATE [Event].[Waitlist] SET NotifiedAtUtc=SYSUTCDATETIME()
        OUTPUT inserted.PetParentId INTO @Recipients
        WHERE EventId=@EventId AND NotifiedAtUtc IS NULL;
        DECLARE @Id UNIQUEIDENTIFIER, @Key NVARCHAR(200);
        DECLARE recipients CURSOR LOCAL FAST_FORWARD FOR SELECT PetParentId FROM @Recipients;
        OPEN recipients; FETCH NEXT FROM recipients INTO @Id;
        WHILE @@FETCH_STATUS=0
        BEGIN
            -- The row is claimed in the same transaction as its outbox entry.
            -- A fresh registration after leaving is a new reminder.
            SET @Key=NULL;
            EXEC [Event].[NotifyEventAccount] @EventId,@Id,N'PetParent',N'EVENT_SEATS_AVAILABLE',@Key;
            FETCH NEXT FROM recipients INTO @Id;
        END
        CLOSE recipients; DEALLOCATE recipients;
    END
    COMMIT TRANSACTION;
END;
