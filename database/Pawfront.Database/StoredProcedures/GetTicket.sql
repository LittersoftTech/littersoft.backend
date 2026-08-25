-- One ticket, scoped to a party to it.
--
-- Returns TWO result sets — the ticket row, then its photos (oldest-first) —
-- or NOTHING when the ticket is unknown OR the caller is not a party. Those two
-- are deliberately the same answer, as everywhere else in this codebase, so a
-- ticket id cannot be probed for existence. Never THROWs.
--
-- The narrative — the reporter's comment and the clarification thread — is NOT
-- here. It lives in the Cosmos "SupportTickets" document, which the caller point
-- reads by [TicketId]; this is the row that says whether they may.
CREATE OR ALTER PROCEDURE [Support].[GetTicket]
    @TicketId UNIQUEIDENTIFIER,
    @ActorType NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (
        SELECT 1
        FROM [Support].[Tickets]
        WHERE [TicketId] = @TicketId
          AND ((@ActorType = N'Provider'  AND [ProviderId]  = @ActorId)
            OR (@ActorType = N'PetParent' AND [PetParentId] = @ActorId)))
    BEGIN
        RETURN;
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

    SELECT [TicketPhotoId],
           [TicketId],
           [PhotoUrl],
           [CreatedAtUtc]
    FROM [Support].[TicketPhotos]
    WHERE [TicketId] = @TicketId
    ORDER BY [CreatedAtUtc] ASC, [TicketPhotoId] ASC;
END;
