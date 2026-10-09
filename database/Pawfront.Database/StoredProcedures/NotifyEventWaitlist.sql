CREATE OR ALTER PROCEDURE [Event].[NotifyEventWaitlist]
    @EventId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Id UNIQUEIDENTIFIER, @Key NVARCHAR(200), @ReleaseId UNIQUEIDENTIFIER=NEWID();
    DECLARE recipients CURSOR LOCAL FAST_FORWARD FOR SELECT PetParentId FROM [Event].[Waitlist] WHERE EventId=@EventId;
    OPEN recipients; FETCH NEXT FROM recipients INTO @Id;
    WHILE @@FETCH_STATUS=0
    BEGIN
        SET @Key=CONCAT(N'EVENT_SEATS_AVAILABLE:',@EventId,N':',@Id,N':',@ReleaseId);
        EXEC [Event].[NotifyEventAccount] @EventId,@Id,N'PetParent',N'EVENT_SEATS_AVAILABLE',@Key;
        FETCH NEXT FROM recipients INTO @Id;
    END
    CLOSE recipients; DEALLOCATE recipients;

END;
