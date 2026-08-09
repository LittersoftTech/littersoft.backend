-- Records a live SignalR connection, or refreshes its heartbeat.
--
-- Called from OnConnectedAsync and again on every client heartbeat, so it is an
-- upsert rather than an insert: a heartbeat for a connection the stale sweep
-- already purged (a long GC pause, a slow tick) re-registers it instead of
-- failing, which is the behaviour that keeps a live user reachable.
--
-- Presence is what decides whether a message earns a push, so a missing row costs
-- the user an unwanted buzz rather than a lost message — the safe direction to
-- fail in.
--
-- Never THROWs. Returns nothing.
CREATE OR ALTER PROCEDURE [Chat].[SaveChatConnection]
    @ConnectionId NVARCHAR(128),
    @ParticipantType NVARCHAR(16),
    @ParticipantId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    UPDATE [Chat].[ChatConnections]
    SET [LastHeartbeatAtUtc] = @Now,
        -- Re-asserted on every heartbeat: a connection id is unique per socket,
        -- so if it somehow resolves to a different participant the newer claim is
        -- the true one.
        [ParticipantType] = @ParticipantType,
        [ParticipantId] = @ParticipantId
    WHERE [ConnectionId] = @ConnectionId;

    IF @@ROWCOUNT = 0
    BEGIN
        BEGIN TRY
            INSERT INTO [Chat].[ChatConnections]
                ([ConnectionId], [ParticipantType], [ParticipantId],
                 [ConnectedAtUtc], [LastHeartbeatAtUtc])
            VALUES
                (@ConnectionId, @ParticipantType, @ParticipantId, @Now, @Now);
        END TRY
        BEGIN CATCH
            -- 2601/2627: a concurrent connect for the same id won the race. Its
            -- row is as good as the one we were about to write, so let it stand.
            IF ERROR_NUMBER() NOT IN (2601, 2627)
            BEGIN
                THROW;
            END
        END CATCH
    END
END;
