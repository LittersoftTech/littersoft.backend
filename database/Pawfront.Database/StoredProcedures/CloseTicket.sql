-- Closes a ticket.
--
-- NO endpoint on either app host calls this — closing is the admin panel's job,
-- and neither party may close a ticket raised against them (or, for that matter,
-- their own). It ships now, ahead of the panel, so that closing a ticket is a
-- documented single call rather than hand-edited rows.
--
-- Nothing but the ticket's own status changes. Raising a ticket does not block
-- anybody, so closing one has no severance to lift: whatever is in
-- [Block].[BlockedParticipants] was put there by a user tapping Block, and is
-- theirs alone to remove.
--
-- What closing DOES release are the three holds keyed off "is there an open
-- ticket": the same booking or conversation can be reported again, the reported
-- conversation can be cleared, and the account and pet deletes stop refusing.
-- All three read [Status], so none of them needs anything written here.
--
-- Returns ONE result set: the closed ticket row. Idempotent — closing an
-- already-closed ticket returns it with its original [ClosedAtUtc].
--
-- THROWs: 51344 ticket not found.
CREATE OR ALTER PROCEDURE [Support].[CloseTicket]
    @TicketId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Status NVARCHAR(48);

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK: the [UX_Tickets_OpenBooking] /
    -- [UX_Tickets_OpenConversation] range this row occupies is what a concurrent
    -- report of the same subject is waiting on, so closing and re-reporting
    -- serialise rather than briefly allowing two open tickets on one booking.
    SELECT @Status = [Status]
    FROM [Support].[Tickets] WITH (UPDLOCK, HOLDLOCK)
    WHERE [TicketId] = @TicketId;

    IF @Status IS NULL
    BEGIN
        THROW 51344, 'Ticket was not found.', 1;
    END

    IF @Status <> N'CLOSED'
    BEGIN
        UPDATE [Support].[Tickets]
        SET [Status] = N'CLOSED',
            [ClosedAtUtc] = @Now,
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
           [ClosedAtUtc],
           -- Appended LAST, here and in every other procedure projecting this row,
           -- so adding them shifted no existing reader ordinal.
           [Category],
           [Reason],
           [EventId]
    FROM [Support].[Tickets]
    WHERE [TicketId] = @TicketId;

    COMMIT TRANSACTION;
END;
