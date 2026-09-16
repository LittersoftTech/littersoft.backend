-- Removes a connection on disconnect.
--
-- Best-effort by design: OnDisconnectedAsync does not run if the host crashes or
-- the socket dies unnoticed, which is exactly why
-- [Chat].[PurgeStaleConnections] exists. This is the tidy path, not the
-- guaranteed one.
--
-- Never THROWs. Returns nothing.
CREATE OR ALTER PROCEDURE [Chat].[DeleteChatConnection]
    @ConnectionId NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM [Chat].[ChatConnections]
    WHERE [ConnectionId] = @ConnectionId;
END;
