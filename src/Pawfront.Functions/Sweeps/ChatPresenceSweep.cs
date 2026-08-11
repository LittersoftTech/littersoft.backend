using System.Data;
using Microsoft.Data.SqlClient;

namespace Pawfront.Functions.Sweeps;

/// <summary>How many dead connection rows one pass removed.</summary>
internal sealed record ChatPresenceSweepResult(int PurgedCount)
{
    public bool DidWork => PurgedCount > 0;
}

/// <summary>
/// Removes chat connections whose heartbeat has gone quiet.
///
/// The hub deletes its own row in OnDisconnectedAsync, but that is best-effort:
/// a crashed host, a killed process or a silently dropped socket never fires it.
/// A leftover row is worse than clutter — <c>Chat.CommitMessageAppend</c> reads presence
/// to decide whether a message earns a push, so a stale row makes its owner look
/// permanently present and silences their notifications indefinitely.
///
/// Changes no chat state beyond presence, so like the reminder sweep it carries
/// no lifecycle risk.
/// </summary>
internal static class ChatPresenceSweep
{
    /// <summary>
    /// Must stay comfortably above the client heartbeat interval (~60s), or live
    /// connections get purged out from under themselves. That self-heals — the
    /// next heartbeat re-registers, since Chat.SaveChatConnection is an upsert —
    /// but it costs the user a push for a thread they are reading in the meantime.
    /// </summary>
    private const int StaleMinutes = 3;

    public static async Task<ChatPresenceSweepResult> RunAsync(
        SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("Chat.PurgeStaleConnections", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@StaleMinutes", StaleMinutes);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await reader.ReadAsync(cancellationToken)
            ? new ChatPresenceSweepResult(reader.GetInt32(0))
            : new ChatPresenceSweepResult(0);
    }
}
