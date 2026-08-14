-- The creator has answered support's request for clarification: moves the ticket
-- CLARIFICATION_ASKED_TO_CREATOR -> CLARIFICATION_RECEIVED_FROM_CREATOR.
--
-- This is the ONE transition either app can drive. Every other status belongs to
-- the admin panel, which is why there is no general "set status" route on the two
-- hosts — only this, and it can move the ticket to exactly one place.
--
-- The reply TEXT is not here. It is appended to the ticket's Cosmos document
-- BEFORE this runs, and the ordering is deliberate, for the reason the chat send
-- learned the hard way: if the document write fails after the status has already
-- moved, the ticket claims an answer that was never recorded. This way a failure
-- leaves the ticket sitting in ASKED with the reply stored — visibly unfinished,
-- and fixed by retrying. The caller authorises through [Support].[GetTicket]
-- first; this re-checks anyway, since by now the Cosmos write has happened and
-- the check is nearly free.
--
-- Returns ONE result set: the updated ticket row.
--
-- THROWs: 51344 ticket not found for this creator, 51347 ticket is closed,
-- 51349 no clarification was asked for.
CREATE OR ALTER PROCEDURE [Support].[RecordTicketClarification]
    @TicketId UNIQUEIDENTIFIER,
    @ActorType NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Status NVARCHAR(48);

    BEGIN TRANSACTION;

    -- Scoped to the CREATOR: support asks the creator and receives from the
    -- creator, so the counterparty is never in this loop.
    SELECT @Status = [Status]
    FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
    WHERE [TicketId] = @TicketId
      AND [RaisedByType] = @ActorType
      AND ((@ActorType = N'Provider'  AND [ProviderId]  = @ActorId)
        OR (@ActorType = N'PetParent' AND [PetParentId] = @ActorId));

    IF @Status IS NULL
    BEGIN
        THROW 51344, 'Ticket was not found.', 1;
    END

    IF @Status = N'CLOSED'
    BEGIN
        THROW 51347, 'This ticket is closed.', 1;
    END

    IF @Status <> N'CLARIFICATION_ASKED_TO_CREATOR'
    BEGIN
        -- Nothing was asked, so there is nothing to answer. Rejecting rather than
        -- silently accepting keeps the status honest: support reads
        -- CLARIFICATION_RECEIVED as "the question I asked has been answered".
        THROW 51349, 'No clarification has been requested on this ticket.', 1;
    END

    UPDATE [Support].[Tickets]
    SET [Status] = N'CLARIFICATION_RECEIVED_FROM_CREATOR',
        [UpdatedAtUtc] = @Now
    WHERE [TicketId] = @TicketId;

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
           [ClosedAtUtc],
           -- Appended LAST, here and in every other procedure projecting this row,
           -- so adding them shifted no existing reader ordinal.
           [Category],
           [Reason]
    FROM [Support].[Tickets]
    WHERE [TicketId] = @TicketId;

    COMMIT TRANSACTION;
END;
