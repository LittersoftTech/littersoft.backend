-- Support tickets. ONE table holds every kind, discriminated by [TicketType]:
--   'BookingIncident' -> "Report Incident" on a booking. [BookingId] +
--                        [BookingType] name the job; [PetId] is denormalised from
--                        it (see below).
--   'ChatIncident'    -> "Report Chat" on a conversation. [ConversationId] names
--                        the thread, which is then under legal hold.
--   'EventIncident'   -> a problem with an event. [EventId] names it.
--   'AppIssue'        -> something wrong with the app itself. No subject at all.
--
-- The first two are raised BY one party AGAINST the other, and store both party
-- ids. The last two are not: they have a reporter and nobody else, so only the
-- reporter's own party column is set and the other is NULL — which is why both
-- are nullable. Every read path stays a plain equality test, since the reporter's
-- column is populated exactly as it always was.
--
-- An event report deliberately records NO counterparty. The organiser is one join
-- away through [EventId], and an event organised by a pet parent and reported by
-- another pet parent cannot be represented by this table's one-Provider /
-- one-PetParent shape at all. Support resolves the organiser when they work the
-- case; the organiser is not a party to the ticket and never sees it in their own
-- "my tickets" list.
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
-- A ticket is raised against a SUBJECT — one booking, one conversation, one event
-- — and NOT against a person. That is what the uniqueness indexes below key on: a
-- parent with five bookings from the same provider can report each of them
-- separately, because each is a different incident with a different account of
-- what happened. Reporting somebody does NOT sever the pair: they stay able to
-- message, book and find each other, and nothing is written to
-- [Block].[BlockedParticipants]. Blocking remains the users' own, separate remedy.
--
-- An 'AppIssue' has no subject and therefore no uniqueness rule at all: each bug
-- report is a different bug, and there is nothing to key a "one open" rule on.
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

    -- For the two COUNTERPARTY kinds ('BookingIncident' / 'ChatIncident') BOTH
    -- parties are stored, whichever direction the report runs in, so every read
    -- path — the two "my tickets" lists, the delete guards — is a plain equality
    -- test with no CASE on who raised it.
    --
    -- For 'AppIssue' and 'EventIncident' there IS no counterparty: only the
    -- reporter's own column is set, matching [RaisedByType], and the other is
    -- NULL. Nullable purely for those two; CK_Tickets_SubjectMatchesType below is
    -- what keeps each kind honest about which columns it may populate.
    [ProviderId] UNIQUEIDENTIFIER NULL,
    [PetParentId] UNIQUEIDENTIFIER NULL,
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

    -- Event incidents only. No FK, same posture as every other subject column
    -- here — and the event outlives the ticket's usefulness either way.
    [EventId] UNIQUEIDENTIFIER NULL,

    -- How the reporter classified the incident, alongside the free-text account
    -- that goes to the Cosmos narrative: [Category] is what kind of problem it is,
    -- [Reason] the one-line summary of this particular one. Both optional, both
    -- plain strings — the vocabulary is the app's picker, deliberately NOT a CHECK
    -- constraint, so adding a category is a mobile release and not a migration.
    --
    -- [Reason] used to travel onto the severance row in [Block].[BlockedParticipants];
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
        CHECK ([TicketType] IN (
            N'BookingIncident', N'ChatIncident', N'EventIncident', N'AppIssue')),
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

    -- Exactly one subject, matching its type — and, for the two kinds that have
    -- no counterparty, exactly one party column, matching [RaisedByType]. Same
    -- shape as [Event].[Events]'s ProviderId / PetParentId pair: the discriminator
    -- and the columns it governs are kept honest by the constraint rather than by
    -- whichever procedure happened to write the row.
    CONSTRAINT [CK_Tickets_SubjectMatchesType]
        CHECK (
            ([TicketType] = N'BookingIncident'
                AND [BookingId] IS NOT NULL
                AND [BookingType] IS NOT NULL
                AND [ConversationId] IS NULL
                AND [EventId] IS NULL
                AND [ProviderId] IS NOT NULL
                AND [PetParentId] IS NOT NULL)
         OR ([TicketType] = N'ChatIncident'
                AND [ConversationId] IS NOT NULL
                AND [BookingId] IS NULL
                AND [BookingType] IS NULL
                AND [PetId] IS NULL
                AND [EventId] IS NULL
                AND [ProviderId] IS NOT NULL
                AND [PetParentId] IS NOT NULL)
         OR ([TicketType] = N'EventIncident'
                AND [EventId] IS NOT NULL
                AND [BookingId] IS NULL
                AND [BookingType] IS NULL
                AND [PetId] IS NULL
                AND [ConversationId] IS NULL
                AND (([RaisedByType] = N'Provider'
                        AND [ProviderId] IS NOT NULL AND [PetParentId] IS NULL)
                  OR ([RaisedByType] = N'PetParent'
                        AND [PetParentId] IS NOT NULL AND [ProviderId] IS NULL)))
         OR ([TicketType] = N'AppIssue'
                AND [EventId] IS NULL
                AND [BookingId] IS NULL
                AND [BookingType] IS NULL
                AND [PetId] IS NULL
                AND [ConversationId] IS NULL
                AND (([RaisedByType] = N'Provider'
                        AND [ProviderId] IS NOT NULL AND [PetParentId] IS NULL)
                  OR ([RaisedByType] = N'PetParent'
                        AND [PetParentId] IS NOT NULL AND [ProviderId] IS NULL)))),

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

GO

-- ONE open ticket per EVENT per REPORTER — deliberately NOT per event. An event
-- has many attendees and each of them reporting it is a separate account of a
-- separate experience, unlike a booking or a thread, which have exactly two
-- parties and therefore one incident between them. What is refused is the same
-- person reporting the same event twice while their first report is open.
--
-- The key works because NULLs compare EQUAL for uniqueness: a parent-raised row
-- is (E, NULL, parentX) and another parent's is (E, NULL, parentY) — distinct —
-- while the same parent twice collides, which is the rule wanted. A
-- provider-raised row is (E, providerX, NULL) and can never collide with a
-- parent's.
CREATE UNIQUE INDEX [UX_Tickets_OpenEventReporter]
    ON [Support].[Tickets] ([EventId], [ProviderId], [PetParentId])
    WHERE [EventId] IS NOT NULL AND [Status] <> N'CLOSED';

GO

-- "Which of MY subjects do I already have an open ticket on?" —
-- [Support].[ListMyOpenTicketSubjects], read once per request by every surface
-- that offers a Report button (booking detail + lists, event list + detail, the
-- chat inbox and thread). One index per side, since the reporter's id lives in a
-- different column depending on which app they are.
--
-- Filtered to the caller's own OPEN tickets, which is what makes it tiny: the
-- flag exists to say "you already reported this", and a closed ticket means they
-- may report it again.
CREATE INDEX [IX_Tickets_OpenRaisedByProvider]
    ON [Support].[Tickets] ([ProviderId])
    INCLUDE ([TicketNumber], [TicketType], [BookingType], [BookingId],
             [ConversationId], [EventId], [Status])
    WHERE [RaisedByType] = N'Provider' AND [Status] <> N'CLOSED';

GO

CREATE INDEX [IX_Tickets_OpenRaisedByPetParent]
    ON [Support].[Tickets] ([PetParentId])
    INCLUDE ([TicketNumber], [TicketType], [BookingType], [BookingId],
             [ConversationId], [EventId], [Status])
    WHERE [RaisedByType] = N'PetParent' AND [Status] <> N'CLOSED';
