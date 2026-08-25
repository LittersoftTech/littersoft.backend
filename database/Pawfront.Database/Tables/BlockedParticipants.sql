-- Who has blocked whom.
--
-- This table began as a CHAT remedy, because chat is OPEN: any pet parent can
-- message any provider with no booking between them, which is the right product
-- call for pre-sales questions but means unsolicited contact is possible by
-- design. It is no longer only that. A block now severs the pair across the whole
-- product, which is why it lives in its own [Block] schema rather than under
-- [Chat] -- the old name and its old header comment both claimed the opposite
-- ("a block is a CHAT remedy and stops there"), and that claim is now false.
--
-- What a block stops, in both directions, from ONE row:
--   * messages     -- [Chat].[GetOrCreateConversation] and
--                     [Chat].[ReserveMessageSequence] refuse, as they always did.
--   * new bookings -- [Booking].[CreateBooking] and
--                     [Booking].[CreateNightStayBooking] refuse.
--   * events       -- neither party sees an event the other organised, and
--                     neither can buy a ticket for one.
--   * discovery    -- the provider drops out of browse and all five searches.
--
-- What a block deliberately does NOT do:
--   * delete existing chat history. What was already said is part of both
--     parties' record, and removing it would also destroy what a blocked user
--     might need in order to report the exchange.
--   * hide a provider behind an existing booking. A parent must still be able to
--     see who a job they already have is with, so the point read
--     ([IProviderDiscoveryService].GetSummaryAsync) stays unfiltered -- only
--     browse and search are filtered. Same carve-out the IsActive filter makes.
--   * take away a ticket already paid for. An event the blocked party already
--     holds tickets for stays in their own event-bookings list; there is no
--     refund leg built, so vanishing it would silently cost them money.
--
-- PLACING A BLOCK CANCELS THE PAIR'S UNFINISHED JOBS. Every non-terminal booking
-- of either kind between the two -- a request nobody has answered, a confirmed
-- job still to come, one with a modification pending -- is cancelled as part of
-- the block, in the blocker's name. The one exception is a job already
-- IN_PROGRESS: the pet is physically in someone's care, [Booking].[UpdateBookingStatus]
-- refuses a cancel from that state (THROW 51149), and that guard is deliberately
-- NOT bypassed here -- such a job runs to completion carrying the blocked flag.
-- The block row is written BEFORE the cancellations so a booking cannot land
-- while they run, and the block always succeeds even if a cancellation fails:
-- it is the user's safety remedy and must not be defeated by a booking that
-- would not cancel.
--
-- Reporting somebody does NOT write here. Raising a support ticket is a report to
-- support, not a sanction the reporter applies themselves, so the two parties stay
-- able to message, book and find each other while the case is looked at. Every row
-- in this table was put here by a user tapping Block, and is theirs alone to lift
-- -- [Block].[UnblockParticipant] refuses nobody. Unblocking restores contact; it
-- does NOT resurrect the bookings the block cancelled, which keep their audit
-- trail and their freed capacity.
--
-- There is therefore no [Source] discriminator and no [TicketId] here: every row
-- has one origin and one owner. If support ever needs to sever a pair from the
-- admin panel, that is a different thing from a user's block and wants its own
-- shape rather than a flag on this one.
--
-- A block placed AGAINST somebody is never disclosed to them -- not by the list,
-- which returns only blocks the caller placed, and not by a refusal, which reads
-- as a neutral "not available" when it was caused by the other party's block.
-- Telling someone they have been blocked confirms the other party acted, which is
-- the thing a block is meant to end.
--
-- NO FK to either party, same reasoning as [Chat].[Conversations]: [BlockerId]
-- and [BlockedId] are polymorphic and an anonymised account must not cascade its
-- blocks away.
CREATE TABLE [Block].[BlockedParticipants]
(
    [BlockId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_BlockedParticipants_BlockId] DEFAULT NEWSEQUENTIALID(),

    [BlockerType] NVARCHAR(16) NOT NULL,
    [BlockerId] UNIQUEIDENTIFIER NOT NULL,
    [BlockedType] NVARCHAR(16) NOT NULL,
    [BlockedId] UNIQUEIDENTIFIER NOT NULL,

    -- Free text the blocker may supply. Never shown to the blocked party.
    [Reason] NVARCHAR(500) NULL,

    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_BlockedParticipants_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_BlockedParticipants] PRIMARY KEY CLUSTERED ([BlockId] ASC),
    -- Blocking twice is a no-op, not a second row.
    CONSTRAINT [UQ_BlockedParticipants_Pair]
        UNIQUE ([BlockerType], [BlockerId], [BlockedType], [BlockedId]),
    CONSTRAINT [CK_BlockedParticipants_BlockerType]
        CHECK ([BlockerType] IN (N'Provider', N'PetParent')),
    CONSTRAINT [CK_BlockedParticipants_BlockedType]
        CHECK ([BlockedType] IN (N'Provider', N'PetParent')),
    -- A blockable relationship only ever runs provider <-> parent, so a block
    -- within one side is meaningless and would silently never be consulted.
    CONSTRAINT [CK_BlockedParticipants_OppositeSides]
        CHECK ([BlockerType] <> [BlockedType])
);

GO

-- Every enforcement point asks the same question -- "did either of us block the
-- other" -- so the reverse lookup needs its own index; the UNIQUE above only
-- serves the blocker-first direction. That was true when chat was the only
-- caller and matters more now that the booking creates, the event reads and the
-- discovery filter all ask it too.
CREATE INDEX [IX_BlockedParticipants_Blocked]
    ON [Block].[BlockedParticipants] ([BlockedType], [BlockedId])
    INCLUDE ([BlockerType], [BlockerId]);
