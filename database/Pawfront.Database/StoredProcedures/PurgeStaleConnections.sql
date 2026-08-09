-- Deletes connection rows whose heartbeat has gone quiet.
--
-- Run on a timer by ChatPresenceSweepFunction, and it is what makes presence
-- trustworthy: OnDisconnectedAsync is best-effort — a crashed host, a killed
-- process or a silently dropped socket never fires it — so without this sweep
-- dead rows would accumulate and permanently suppress a user's pushes. That is
-- the failure mode worth engineering against: a stale row does not merely waste
-- space, it makes the recipient look present forever.
--
-- @StaleMinutes must stay comfortably longer than the client heartbeat interval,
-- or live connections get purged out from under themselves. A purged-but-live
-- connection self-heals on its next heartbeat ([Chat].[SaveChatConnection] is an
-- upsert), but it earns the user a spurious push in the meantime.
--
-- Returns ONE row: how many were removed, for the function's log line.
-- Never THROWs.
CREATE OR ALTER PROCEDURE [Chat].[PurgeStaleConnections]
    @StaleMinutes INT = 3
AS
BEGIN
    SET NOCOUNT ON;

    IF @StaleMinutes IS NULL OR @StaleMinutes < 1
    BEGIN
        SET @StaleMinutes = 3;
    END

    DECLARE @Cutoff DATETIME2(7) = DATEADD(MINUTE, -@StaleMinutes, SYSUTCDATETIME());

    DELETE FROM [Chat].[ChatConnections]
    WHERE [LastHeartbeatAtUtc] < @Cutoff;

    SELECT @@ROWCOUNT AS [PurgedCount];
END;
