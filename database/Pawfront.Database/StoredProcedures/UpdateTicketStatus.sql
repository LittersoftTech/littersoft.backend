-- Moves a ticket between the working statuses. The admin panel's procedure.
--
-- NO endpoint on either app host calls this, and none should: every status here
-- is support's own reading of the case. The one transition a user drives —
-- answering a clarification request — has its own procedure
-- ([Support].[RecordTicketClarification]), guarded to a single from-state and
-- scoped to the creator.
--
-- CLOSED is deliberately NOT settable here. Closing releases the three holds an
-- open ticket carries — the subject can be reported again, the reported chat can
-- be cleared, and the account and pet deletes stop refusing — so it goes through
-- [Support].[CloseTicket], where that is stated and stamps [ClosedAtUtc]. Routing
-- it here instead would make closure look like any other status move.
--
-- Returns ONE result set: the updated ticket row.
--
-- THROWs: 51344 ticket not found, 51347 ticket is closed (terminal — reopening is
-- not modelled; raise a fresh ticket), 51350 invalid or unsettable status.
CREATE OR ALTER PROCEDURE [Support].[UpdateTicketStatus]
    @TicketId UNIQUEIDENTIFIER,
    @Status NVARCHAR(48)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Status NOT IN (
        N'OPENED',
        N'IN_REVIEW',
        N'CLARIFICATION_ASKED_TO_CREATOR',
        N'CLARIFICATION_RECEIVED_FROM_CREATOR',
        N'PENDING_WITH_LEGAL_TEAM')
    BEGIN
        THROW 51350, 'That status cannot be set here.', 1;
    END

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Current NVARCHAR(48);

    BEGIN TRANSACTION;

    SELECT @Current = [Status]
    FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
    WHERE [TicketId] = @TicketId;

    IF @Current IS NULL
    BEGIN
        THROW 51344, 'Ticket was not found.', 1;
    END

    IF @Current = N'CLOSED'
    BEGIN
        THROW 51347, 'This ticket is closed.', 1;
    END

    -- Setting the status it already holds is a no-op rather than an error: the
    -- panel may well re-send it, and there is nothing to protect here (unlike a
    -- booking transition, no side effect hangs off the write).
    IF @Current <> @Status
    BEGIN
        UPDATE [Support].[Tickets]
        SET [Status] = @Status,
            [UpdatedAtUtc] = @Now
        WHERE [TicketId] = @TicketId;
    END

    SELECT [TicketId],
           [TicketNumber],
           [TicketType],
           [ProviderId],
           [PetParentId],
           [RaisedByType],
           [BookingType],
           [BookingId],
           [PetId],
           [ConversationId],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [ClosedAtUtc]
    FROM [Support].[Tickets]
    WHERE [TicketId] = @TicketId;

    COMMIT TRANSACTION;
END;
