-- Live SignalR connections, and which thread each one is currently looking at.
--
-- This exists for exactly one decision: whether to send a push. A message always
-- goes out over the socket; the FCM push is sent only when the recipient has no
-- connection whose [ActiveConversationId] is this conversation — i.e. nobody is
-- actually looking at the thread. Without this table the choice would be "always
-- push" (a buzz for a message you are reading) or "never push" (silence for an
-- offline recipient).
--
-- OPERATIONAL DATA, not history. Rows are written on connect and deleted on
-- disconnect. [Chat].[PurgeStaleConnections] is what makes that reliable:
-- OnDisconnectedAsync does not run if the host crashes or a network drop is never
-- noticed, so a row whose [LastHeartbeatAtUtc] has gone quiet is presumed gone.
-- Clients heartbeat on a timer; the sweep interval must stay comfortably longer
-- than that or live connections get purged out from under themselves.
--
-- No FK on [ActiveConversationId] on purpose. It is a transient pointer, and a
-- FK here would make deleting a conversation depend on nobody currently viewing
-- it. A dangling id is harmless: it simply matches nothing.
CREATE TABLE [Chat].[ChatConnections]
(
    -- SignalR's own connection id. Used as the PK because it is already unique
    -- and is the only handle OnDisconnectedAsync is given.
    [ConnectionId] NVARCHAR(128) NOT NULL,

    [ParticipantType] NVARCHAR(16) NOT NULL,
    [ParticipantId] UNIQUEIDENTIFIER NOT NULL,

    -- The thread this connection has open, or NULL when the client is connected
    -- but somewhere else in the app. NULL is the common case and the one that
    -- still earns a push.
    [ActiveConversationId] UNIQUEIDENTIFIER NULL,

    [ConnectedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ChatConnections_ConnectedAtUtc] DEFAULT SYSUTCDATETIME(),
    [LastHeartbeatAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ChatConnections_LastHeartbeatAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_ChatConnections] PRIMARY KEY CLUSTERED ([ConnectionId] ASC),
    CONSTRAINT [CK_ChatConnections_ParticipantType]
        CHECK ([ParticipantType] IN (N'Provider', N'PetParent'))
);

GO

-- The presence check on the send path: does this recipient have any connection,
-- and is any of them on this thread?
CREATE INDEX [IX_ChatConnections_Participant]
    ON [Chat].[ChatConnections] ([ParticipantType], [ParticipantId])
    INCLUDE ([ActiveConversationId]);

GO

-- The stale sweep's predicate.
CREATE INDEX [IX_ChatConnections_LastHeartbeat]
    ON [Chat].[ChatConnections] ([LastHeartbeatAtUtc]);
