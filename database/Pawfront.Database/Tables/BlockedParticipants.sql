-- Who has blocked whom.
--
-- This table is a direct consequence of chat being OPEN: any pet parent can
-- message any provider without a booking between them, which is the right
-- product call for pre-sales questions but means unsolicited contact is possible
-- by design. A block is the user's own remedy for that, and it is the one piece
-- of anti-abuse the feature cannot ship without.
--
-- A block stops NEW messages in both directions and stops a conversation being
-- opened. It deliberately does NOT hide or delete existing history: what was
-- already said is part of both parties' record, and removing it would also remove
-- the evidence a blocked user might later need to report.
--
-- Reporting is NOT modelled here. A report has to terminate in a support
-- workflow, and this backend has no Helpline / ticket module (see the
-- DISPUTE_RESOLVED notification type, which has copy and route but no trigger for
-- exactly the same reason). It belongs with that module, not ahead of it.
--
-- NO FK to either party, same reasoning as [Chat].[Conversations]: [BlockerId]
-- and [BlockedId] are polymorphic and an anonymised account must not cascade its
-- blocks away.
CREATE TABLE [Chat].[BlockedParticipants]
(
    [ChatBlockId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_BlockedParticipants_ChatBlockId] DEFAULT NEWSEQUENTIALID(),

    [BlockerType] NVARCHAR(16) NOT NULL,
    [BlockerId] UNIQUEIDENTIFIER NOT NULL,
    [BlockedType] NVARCHAR(16) NOT NULL,
    [BlockedId] UNIQUEIDENTIFIER NOT NULL,

    -- Free text the blocker may supply. Kept for a future report flow to quote;
    -- never shown to the blocked party.
    [Reason] NVARCHAR(500) NULL,

    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_BlockedParticipants_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_BlockedParticipants] PRIMARY KEY CLUSTERED ([ChatBlockId] ASC),
    -- Blocking twice is a no-op, not a second row.
    CONSTRAINT [UQ_BlockedParticipants_Pair]
        UNIQUE ([BlockerType], [BlockerId], [BlockedType], [BlockedId]),
    CONSTRAINT [CK_BlockedParticipants_BlockerType]
        CHECK ([BlockerType] IN (N'Provider', N'PetParent')),
    CONSTRAINT [CK_BlockedParticipants_BlockedType]
        CHECK ([BlockedType] IN (N'Provider', N'PetParent')),
    -- A conversation only ever runs provider <-> parent, so a block within one
    -- side is meaningless and would silently never be consulted.
    CONSTRAINT [CK_BlockedParticipants_OppositeSides]
        CHECK ([BlockerType] <> [BlockedType])
);

GO

-- The send path checks BOTH directions in one go ("did either of us block the
-- other"), so the reverse lookup needs its own index — the UNIQUE above only
-- serves the blocker-first direction.
CREATE INDEX [IX_BlockedParticipants_Blocked]
    ON [Chat].[BlockedParticipants] ([BlockedType], [BlockedId])
    INCLUDE ([BlockerType], [BlockerId]);
