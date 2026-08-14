-- Support tickets raised by one party against the other. ONE table holds both
-- kinds, discriminated by [TicketType]:
--   'BookingIncident' -> "Report Incident" on a booking. [BookingId] +
--                        [BookingType] name the job; [PetId] is denormalised from
--                        it (see below).
--   'ChatIncident'    -> "Report Chat" on a conversation. [ConversationId] names
--                        the thread, which is then under legal hold.
--
-- This row is the INDEX. The narrative — the reporter's comment and the
-- clarification thread, which can grow without bound as support asks and the
-- creator answers — lives in the Cosmos "SupportTickets" container, partitioned
-- by /ticketId. Same SQL-owns-relationships / Cosmos-owns-volume split as
-- [Chat].[Conversations] + the ChatMessages container.
--
-- [Status] lives HERE and not in the document, which is the one thing that split
-- forces. Three rules read it and all three are T-SQL predicates that cannot
-- reach Cosmos: the open-ticket uniqueness below, the account/pet delete
-- refusals, and the chat legal hold in [Chat].[DeleteConversationForParticipant].
--
-- A ticket is raised against a SUBJECT — one booking, or one conversation — and
-- NOT against a person. That is what the uniqueness indexes below key on: a
-- parent with five bookings from the same provider can report each of them
-- separately, because each is a different incident with a different account of
-- what happened. Reporting somebody does NOT sever the pair: they stay able to
-- message, book and find each other, and nothing is written to
-- [Chat].[BlockedParticipants]. Blocking remains the users' own, separate remedy.
--
-- NO FK to [Provider].[Providers] or [Parent].[PetParents], and none to either
-- booking table — the same posture as [Booking].[BookingPayments] and
-- [Review].[BookingReviews]. One column cannot reference two tables, and an
-- anonymised account must keep its tickets: an open ticket is precisely what
-- stops that account being deleted in the first place.
--
-- Party names are NOT denormalised here. The admin panel joins them live, so a
-- deleted account reads "Deleted User" rather than leaving a real name frozen in
-- a support record.
CREATE TABLE [Support].[Tickets]
(
    [TicketId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_Tickets_TicketId] DEFAULT NEWSEQUENTIALID(),

    -- Friendly reference shown to both parties and to support: TK-000123.
    -- An IDENTITY rather than a SEQUENCE (cf. [Booking].[PayoutNumberSequence]),
    -- because unlike payouts there is exactly ONE table minting these.
    [TicketNumber] INT IDENTITY(1, 1) NOT NULL,

    [TicketType] NVARCHAR(24) NOT NULL,

    -- BOTH parties are always stored, whichever direction the report runs in, so
    -- every read path — the two "my tickets" lists, the delete guards, the block
    -- write — is a plain equality test with no CASE on who raised it.
    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
    [PetParentId] UNIQUEIDENTIFIER NOT NULL,
    [RaisedByType] NVARCHAR(16) NOT NULL,

    -- Booking incidents only.
    [BookingType] NVARCHAR(16) NULL,
    [BookingId] UNIQUEIDENTIFIER NULL,
    -- Denormalised from the booking at creation, purely so the pet-delete guard
    -- in [Parent].[DeletePetParentPet] is a single indexed read rather than a
    -- UNION across both booking tables. Safe to copy: a booking's pet is fixed at
    -- creation — a modification changes its schedule, never its animal.
    [PetId] UNIQUEIDENTIFIER NULL,

    -- Chat incidents only.
    [ConversationId] UNIQUEIDENTIFIER NULL,

    -- How the reporter classified the incident, alongside the free-text account
    -- that goes to the Cosmos narrative: [Category] is what kind of problem it is,
    -- [Reason] the one-line summary of this particular one. Both optional, both
    -- plain strings — the vocabulary is the app's picker, deliberately NOT a CHECK
    -- constraint, so adding a category is a mobile release and not a migration.
    --
    -- [Reason] used to travel onto the severance row in [Chat].[BlockedParticipants];
    -- reporting no longer writes there, so it lives on the ticket it describes.
    -- Both are returned on every ticket read and are never shown to the reported
    -- party — only to its two parties' own "my tickets" screens and to support.
    [Category] NVARCHAR(100) NULL,
    [Reason] NVARCHAR(500) NULL,

    [Status] NVARCHAR(48) NOT NULL
        CONSTRAINT [DF_Tickets_Status] DEFAULT N'OPENED',

    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Tickets_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Tickets_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [ClosedAtUtc] DATETIME2(7) NULL,

    CONSTRAINT [PK_Tickets] PRIMARY KEY CLUSTERED ([TicketId] ASC),
    CONSTRAINT [UQ_Tickets_TicketNumber] UNIQUE ([TicketNumber]),

    CONSTRAINT [CK_Tickets_TicketType]
        CHECK ([TicketType] IN (N'BookingIncident', N'ChatIncident')),
    CONSTRAINT [CK_Tickets_RaisedByType]
        CHECK ([RaisedByType] IN (N'Provider', N'PetParent')),
    CONSTRAINT [CK_Tickets_BookingType]
        CHECK ([BookingType] IS NULL OR [BookingType] IN (N'SingleDay', N'NightStay')),
    CONSTRAINT [CK_Tickets_Status]
        CHECK ([Status] IN (
            N'OPENED',
            N'IN_REVIEW',
            N'CLARIFICATION_ASKED_TO_CREATOR',
            N'CLARIFICATION_RECEIVED_FROM_CREATOR',
            N'PENDING_WITH_LEGAL_TEAM',
            N'CLOSED')),

    -- Exactly one subject, matching its type. Same shape as
    -- [Event].[Events]'s ProviderId / PetParentId pair: the discriminator and the
    -- columns it governs are kept honest by the constraint rather than by
    -- whichever procedure happened to write the row.
    CONSTRAINT [CK_Tickets_SubjectMatchesType]
        CHECK (
            ([TicketType] = N'BookingIncident'
                AND [BookingId] IS NOT NULL
                AND [BookingType] IS NOT NULL
                AND [ConversationId] IS NULL)
         OR ([TicketType] = N'ChatIncident'
                AND [ConversationId] IS NOT NULL
                AND [BookingId] IS NULL
                AND [BookingType] IS NULL
                AND [PetId] IS NULL)),

    -- CLOSED is the only status that stamps a closure time, and it always does.
    CONSTRAINT [CK_Tickets_ClosedAtUtc]
        CHECK (([Status] = N'CLOSED' AND [ClosedAtUtc] IS NOT NULL)
            OR ([Status] <> N'CLOSED' AND [ClosedAtUtc] IS NULL))
);

GO

-- ONE open ticket per BOOKING, in either direction: while a report on a job is
-- open, that job cannot be reported again. Reporting a DIFFERENT booking with the
-- same provider is unaffected, which is the whole point — a ticket is about an
-- incident, and two jobs are two incidents.
--
-- Still "either direction": both parties reporting the same job is one incident
-- seen from two sides, and support works it as one case. The second reporter is
-- handed the open ticket rather than opening a duplicate.
--
-- A filtered UNIQUE index rather than a check in the procedure, so the rule is
-- race-safe for free — two reports landing together cannot both find "no open
-- ticket" and both insert. [Support].[CreateTicket] still reads first, but only
-- so it can answer 409 with the id of the ticket already open rather than
-- surfacing a constraint violation.
--
-- [BookingType] leads the key only to keep single-day and night-stay ids in
-- separate ranges; the two tables share no id space in practice, so the pair is
-- belt and braces.
CREATE UNIQUE INDEX [UX_Tickets_OpenBooking]
    ON [Support].[Tickets] ([BookingType], [BookingId])
    WHERE [BookingId] IS NOT NULL AND [Status] <> N'CLOSED';

GO

-- The provider's "my tickets" list: their whole history, newest activity first,
-- with the columns the list filters and sorts on covered.
CREATE INDEX [IX_Tickets_Provider]
    ON [Support].[Tickets] ([ProviderId], [UpdatedAtUtc] DESC)
    INCLUDE ([TicketType], [Status], [CreatedAtUtc], [PetParentId], [RaisedByType]);

GO

-- The parent's mirror of it.
CREATE INDEX [IX_Tickets_PetParent]
    ON [Support].[Tickets] ([PetParentId], [UpdatedAtUtc] DESC)
    INCLUDE ([TicketType], [Status], [CreatedAtUtc], [ProviderId], [RaisedByType]);

GO

-- Two jobs in one index.
--
--   1. ONE open ticket per CONVERSATION — the chat-incident mirror of
--      [UX_Tickets_OpenBooking] above. A parent may report one thread and still
--      report a booking with the same provider; what they cannot do is report the
--      same thread twice while the first report is open.
--   2. The legal hold. [Chat].[DeleteConversationForParticipant] and the message
--      delete both ask "is there an open ticket on this conversation" on every
--      call, so it must be a point read and not a scan — hence the INCLUDE.
--
-- Filtered to open tickets because a closed one neither holds a thread nor blocks
-- a fresh report.
CREATE UNIQUE INDEX [UX_Tickets_OpenConversation]
    ON [Support].[Tickets] ([ConversationId])
    INCLUDE ([TicketNumber], [Status])
    WHERE [ConversationId] IS NOT NULL AND [Status] <> N'CLOSED';

GO

-- The pet-delete guard, same reasoning as the hold above.
CREATE INDEX [IX_Tickets_OpenPet]
    ON [Support].[Tickets] ([PetId])
    INCLUDE ([TicketNumber], [Status])
    WHERE [PetId] IS NOT NULL AND [Status] <> N'CLOSED';
