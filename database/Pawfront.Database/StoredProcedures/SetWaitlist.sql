CREATE OR ALTER PROCEDURE [Event].[SetWaitlist]
    @EventId UNIQUEIDENTIFIER, @PetParentId UNIQUEIDENTIFIER, @Join BIT, @MaximumCapacity INT=NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult = sys.sp_getapplock @Resource=N'EventLifecycle', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
    IF @LockResult < 0 THROW 51468, 'Event operation is busy. Retry the request.', 1;

    IF @Join=0 DELETE FROM [Event].[Waitlist] WHERE EventId=@EventId AND PetParentId=@PetParentId;
    ELSE
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM [Event].[Events] WHERE EventId=@EventId AND EventStatus=N'Active'
            AND (PetParentId<>@PetParentId OR PetParentId IS NULL)
            AND [Event].[EventStartUtc](StartDate,StartTime)>SYSUTCDATETIME())
            THROW 51461, 'Event cannot be waitlisted.', 1;
        SELECT @MaximumCapacity=COALESCE(MaximumCapacity,@MaximumCapacity) FROM [Event].[Events] WHERE EventId=@EventId;
        IF @MaximumCapacity IS NULL OR @MaximumCapacity>(SELECT ISNULL(SUM(TicketCount),0) FROM [Event].[EventBookings] WHERE EventId=@EventId AND Status IN (N'Confirmed',N'Attended'))
            THROW 51468, 'Only sold-out events accept waitlist registrations.', 1;
        IF EXISTS (SELECT 1 FROM [Event].[EventBookings] WHERE EventId=@EventId AND BookerAccountId=@PetParentId AND Status IN (N'Confirmed',N'Attended'))
            THROW 51469, 'You already hold a booking for this event.', 1;
        IF NOT EXISTS (SELECT 1 FROM [Event].[Waitlist] WHERE EventId=@EventId AND PetParentId=@PetParentId)
            INSERT [Event].[Waitlist](EventId,PetParentId) VALUES (@EventId,@PetParentId);
    END

    COMMIT TRANSACTION;
END;
