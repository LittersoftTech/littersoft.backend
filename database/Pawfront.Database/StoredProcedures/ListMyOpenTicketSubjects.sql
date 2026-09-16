-- "Which subjects do I already have an open ticket on?"
--
-- Feeds the [isTicketRaisedByMe] / [ticketId] pair on every surface that offers a
-- Report button: the booking detail and both booking lists, the event list and
-- detail, and the chat inbox and thread — on all three hosts. Without it those
-- screens offer "Report" on something the caller already reported, and the
-- attempt comes back 409.
--
-- Scoped by the CALLER as the REPORTER, deliberately — the flag is
-- "raised by me", not "raised by anybody". The counterparty's ticket on the same
-- booking is not the caller's to open, and its existence is not theirs to be told
-- about. (One consequence worth knowing: because the one-open-ticket rule runs in
-- either direction, a caller whose counterparty has already reported the booking
-- reads FALSE here and is still refused with 409 TicketAlreadyOpen.)
--
-- OPEN tickets only. A closed ticket is exactly the state in which a fresh report
-- is allowed again, so counting one would leave the app permanently offering to
-- open a ticket the user can no longer reach. It also keeps the answer
-- unambiguous: at most one open ticket exists per subject, so there is exactly
-- one id to return.
--
-- Returns ONE result set — one row per open ticket the caller raised, whatever
-- its kind. The caller keys them by subject in memory. Scoped to the actor rather
-- than taking a list of subject ids on purpose: a booking list page then costs
-- ONE read instead of one per card, the same reasoning behind
-- [Review].[ListPetParentBookingReviews].
--
-- Served by [IX_Tickets_OpenRaisedByProvider] / [IX_Tickets_OpenRaisedByPetParent],
-- which are filtered to exactly this predicate and INCLUDE every column below.
--
-- Never THROWs: "no open tickets" is the overwhelmingly common answer and is an
-- empty result set, not an error.
CREATE OR ALTER PROCEDURE [Support].[ListMyOpenTicketSubjects]
    @RaisedByType NVARCHAR(16),                 -- 'Provider' | 'PetParent'
    @ActorId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [TicketId],
           [TicketNumber],
           [TicketType],
           [BookingType],
           [BookingId],
           [ConversationId],
           [EventId]
    FROM [Support].[Tickets]
    WHERE [RaisedByType] = @RaisedByType
      AND [Status] <> N'CLOSED'
      AND ((@RaisedByType = N'Provider'  AND [ProviderId]  = @ActorId)
        OR (@RaisedByType = N'PetParent' AND [PetParentId] = @ActorId))
    -- An app issue has no subject and can never match a card, but it is returned
    -- anyway rather than filtered here: the shape stays "my open tickets", and a
    -- future surface that wants to show one needs no procedure change.
    ORDER BY [CreatedAtUtc] ASC, [TicketId] ASC;
END;
