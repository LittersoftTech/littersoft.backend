CREATE OR ALTER PROCEDURE [Event].[RemoveHostedEvent]
    @EventId UNIQUEIDENTIFIER, @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult = sys.sp_getapplock @Resource=N'EventLifecycle', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
    IF @LockResult < 0 THROW 51468, 'Event operation is busy. Retry the request.', 1;

    IF NOT EXISTS (SELECT 1 FROM [Event].[Events] WHERE EventId=@EventId AND PetParentId=@PetParentId)
        THROW 51460, 'Event was not found for this host.', 1;
    IF EXISTS (SELECT 1 FROM [Event].[Events] WHERE EventId=@EventId AND EventStatus<>N'Cancelled')
        THROW 51466, 'Cancel the event before removing it.', 1;
    UPDATE [Event].[Events] SET RemovedByHost=1, UpdatedAtUtc=SYSUTCDATETIME() WHERE EventId=@EventId;

    COMMIT TRANSACTION;
END;
