-- Replaces an event's payout-method set (Cash and/or Digital).
--
-- Payout methods only apply to PAID events — for a free event the sproc
-- throws 51099 (the API maps it to 400 FreeEventNoPayout). A missing event
-- throws 51098 (mapped to 404 EventNotFound).
CREATE OR ALTER PROCEDURE [Event].[SaveEventPayoutMethods]
    @EventId UNIQUEIDENTIFIER,
    @AcceptsCash BIT,
    @AcceptsDigital BIT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @IsPaid BIT;
    SELECT @IsPaid = [IsPaid]
    FROM [Event].[Events]
    WHERE [EventId] = @EventId;

    IF @IsPaid IS NULL
    BEGIN
        THROW 51098, 'Event was not found.', 1;
    END

    IF @IsPaid = 0
    BEGIN
        THROW 51099, 'Payout methods only apply to paid events; this event is free.', 1;
    END

    DELETE FROM [Event].[EventPayoutMethods]
    WHERE [EventId] = @EventId;

    IF @AcceptsCash = 1
    BEGIN
        INSERT INTO [Event].[EventPayoutMethods] ([EventId], [PayoutMethod])
        VALUES (@EventId, N'Cash');
    END

    IF @AcceptsDigital = 1
    BEGIN
        INSERT INTO [Event].[EventPayoutMethods] ([EventId], [PayoutMethod])
        VALUES (@EventId, N'Digital');
    END

    SELECT [PayoutMethod]
    FROM [Event].[EventPayoutMethods]
    WHERE [EventId] = @EventId
    ORDER BY [PayoutMethod];

    COMMIT TRANSACTION;
END;
