CREATE OR ALTER PROCEDURE [Event].[CancelHostedEvent]
    @EventId UNIQUEIDENTIFIER, @PetParentId UNIQUEIDENTIFIER = NULL, @ProviderId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult = sys.sp_getapplock @Resource=N'EventLifecycle', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
    IF @LockResult < 0 THROW 51468, 'Event operation is busy. Retry the request.', 1;

    IF NOT EXISTS (SELECT 1 FROM [Event].[Events] WHERE EventId=@EventId
        AND ((@ProviderId IS NULL AND PetParentId=@PetParentId)
          OR (@PetParentId IS NULL AND ProviderId=@ProviderId)))
        THROW 51460, 'Event was not found for this host.', 1;
    IF EXISTS (SELECT 1 FROM [Event].[Events] WHERE EventId=@EventId AND EventStatus=N'Active')
    BEGIN
        IF EXISTS (SELECT 1 FROM [Event].[Events] WHERE EventId=@EventId AND [Event].[EventStartUtc](StartDate,StartTime)<=SYSUTCDATETIME())
            THROW 51468, 'An event that has started cannot be cancelled.', 1;
        UPDATE [Event].[Events] SET EventStatus=N'Cancelled', UpdatedAtUtc=SYSUTCDATETIME(), CancelledAtUtc=SYSUTCDATETIME(),
            CancelledWithBookings=CASE WHEN EXISTS (SELECT 1 FROM [Event].[EventBookings] WHERE EventId=@EventId) THEN 1 ELSE 0 END
        WHERE EventId=@EventId;
        DECLARE @Recipients TABLE (BookingId UNIQUEIDENTIFIER, AccountId UNIQUEIDENTIFIER, Audience NVARCHAR(16));
        UPDATE [Event].[EventBookings] SET Status=N'EventCancelled', CancelledAtUtc=SYSUTCDATETIME(), UpdatedAtUtc=SYSUTCDATETIME()
        OUTPUT inserted.BookingId,inserted.BookerAccountId,inserted.BookerType INTO @Recipients
        WHERE EventId=@EventId AND Status IN (N'Confirmed',N'Attended');
        DECLARE @BookingId UNIQUEIDENTIFIER, @AccountId UNIQUEIDENTIFIER, @Audience NVARCHAR(16), @Key NVARCHAR(200);
        DECLARE recipients CURSOR LOCAL FAST_FORWARD FOR SELECT BookingId,AccountId,Audience FROM @Recipients WHERE AccountId IS NOT NULL;
        OPEN recipients; FETCH NEXT FROM recipients INTO @BookingId,@AccountId,@Audience;
        WHILE @@FETCH_STATUS=0
        BEGIN
            SET @Key=CONCAT(N'EVENT_CANCELLED:',@BookingId);
            EXEC [Event].[NotifyEventAccount] @EventId,@AccountId,@Audience,N'EVENT_CANCELLED',@Key,@BookingId;
            FETCH NEXT FROM recipients INTO @BookingId,@AccountId,@Audience;
        END
        CLOSE recipients; DEALLOCATE recipients;
        DELETE FROM [Event].[Waitlist] WHERE EventId=@EventId;
    END

    COMMIT TRANSACTION;
END;
