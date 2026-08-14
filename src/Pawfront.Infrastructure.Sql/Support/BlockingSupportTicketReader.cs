using Microsoft.Data.SqlClient;
using Pawfront.Application.Support;

namespace Pawfront.Infrastructure.Sql.Support;

/// <summary>
/// Reads one open-ticket row from a refused delete. Shared by the three deletes an open
/// ticket can block — <c>Parent.DeletePetParent</c> (result set 4),
/// <c>Parent.DeletePetParentPet</c> (result set 3) and <c>Provider.DeleteProvider</c>
/// (result set 3) — which deliberately project the SAME columns in the SAME order so all
/// three refusals hand the caller an identical payload.
/// </summary>
/// <remarks>
/// Change the column list in one procedure and you must change it in the other two and
/// here. Same arrangement, and the same warning, as <c>PendingJobReader</c>.
/// </remarks>
internal static class BlockingSupportTicketReader
{
    public static BlockingSupportTicket Read(SqlDataReader reader)
        => new(
            TicketId: reader.GetGuid(0),
            TicketNumber: reader.GetInt32(1),
            TicketType: reader.GetString(2),
            RaisedByType: reader.GetString(3),
            Status: reader.GetString(4),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero));

    /// <summary>
    /// Drains a result set of open tickets. Always call this before throwing the refusal,
    /// so the reader is not abandoned mid-stream.
    /// </summary>
    public static async Task<IReadOnlyList<BlockingSupportTicket>> ReadAllAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var tickets = new List<BlockingSupportTicket>();

        while (await reader.ReadAsync(cancellationToken))
        {
            tickets.Add(Read(reader));
        }

        return tickets;
    }
}
