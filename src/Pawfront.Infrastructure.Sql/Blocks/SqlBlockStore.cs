using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Blocks;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Sql.Blocks;

/// <summary>
/// Blocks, through the <c>Block</c> schema's procedures.
/// </summary>
/// <remarks>
/// <para>
/// Serves both <see cref="IBlockStore"/> (the write side and the blocked list)
/// and <see cref="IMyBlockLookup"/> (the per-request "who am I blocked from"
/// read), because they are two questions about one table and splitting them
/// across two classes would only duplicate the connection plumbing.
/// </para>
/// <para>
/// Every <c>DATETIME2</c> is pinned to <see cref="TimeSpan.Zero"/> on the way
/// out. The column carries no offset and this codebase stores UTC throughout, so
/// letting <see cref="DateTimeOffset"/> infer the server's local offset would
/// silently shift every timestamp.
/// </para>
/// </remarks>
internal sealed class SqlBlockStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IBlockStore, IMyBlockLookup
{
    public async Task<(ParticipantBlock Block, bool WasAlreadyBlocked, IReadOnlyList<BlockCancellableJob> Jobs)>
        BlockAsync(
            BlockParty blocker,
            BlockPartyType blockedType,
            Guid blockedId,
            string? reason,
            CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Block].[BlockParticipant]");
        command.Parameters.AddWithValue("@BlockerType", blocker.Type.ToSqlValue());
        command.Parameters.AddWithValue("@BlockerId", blocker.Id);
        command.Parameters.AddWithValue("@BlockedType", blockedType.ToSqlValue());
        command.Parameters.AddWithValue("@BlockedId", blockedId);
        command.Parameters.AddWithValue("@Reason", (object?)reason ?? DBNull.Value);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Block.BlockParticipant returned no row.");
            }

            var block = ReadBlock(reader);
            var wasAlreadyBlocked = ReadFlag(reader, 7);

            // Result set 2: the pair's unfinished jobs, captured by the same
            // transaction that wrote the block.
            var jobs = new List<BlockCancellableJob>();
            if (await reader.NextResultAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    jobs.Add(new BlockCancellableJob(
                        BookingType: reader.GetString(0),
                        BookingId: reader.GetGuid(1),
                        JobNumber: reader.GetInt32(2),
                        Status: reader.GetString(3),
                        ServiceDate: DateOnly.FromDateTime(reader.GetDateTime(4))));
                }
            }

            return (block, wasAlreadyBlocked, jobs);
        }
        catch (SqlException exception) when (exception.Number == 51327)
        {
            throw new InvalidBlockPairException("A block must run between a provider and a pet parent.");
        }
    }

    public async Task<ParticipantBlock?> UnblockAsync(
        Guid blockId,
        BlockParty blocker,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Block].[UnblockParticipant]");
        command.Parameters.AddWithValue("@BlockId", blockId);
        command.Parameters.AddWithValue("@BlockerType", blocker.Type.ToSqlValue());
        command.Parameters.AddWithValue("@BlockerId", blocker.Id);

        // No guard on the way in: a block is always the blocker's to lift. The
        // procedure is scoped to the caller as blocker, so an unknown id and
        // somebody else's block are the same empty result — which the caller maps
        // to 404, so an id cannot be probed for existence.
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadBlock(reader) : null;
    }

    public async Task<(IReadOnlyList<ParticipantBlockRow> Rows, int TotalCount)> ListAsync(
        BlockParty blocker,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Block].[ListBlockedParticipants]");
        command.Parameters.AddWithValue("@BlockerType", blocker.Type.ToSqlValue());
        command.Parameters.AddWithValue("@BlockerId", blocker.Id);
        command.Parameters.AddWithValue("@Skip", skip);
        command.Parameters.AddWithValue("@Take", take);

        var rows = new List<ParticipantBlockRow>();
        var totalCount = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var block = ReadBlock(reader) with
            {
                BlockedName = reader.IsDBNull(7) ? null : reader.GetString(7),
                BlockedPhotoUrl = reader.IsDBNull(8) ? null : reader.GetString(8)
            };

            rows.Add(new ParticipantBlockRow(
                block,
                BlockedServiceCategory: reader.IsDBNull(9) ? null : reader.GetString(9)));

            // COUNT(*) OVER() — the same on every row, so the page and its total
            // come back in one round trip.
            totalCount = reader.GetInt32(10);
        }

        return (rows, totalCount);
    }

    public async Task<IReadOnlyList<BlockedCounterparty>> ListMyBlockedCounterpartiesAsync(
        BlockParty participant,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Block].[ListMyBlockedCounterparties]");
        command.Parameters.AddWithValue("@ParticipantType", participant.Type.ToSqlValue());
        command.Parameters.AddWithValue("@ParticipantId", participant.Id);

        var counterparties = new List<BlockedCounterparty>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counterparties.Add(new BlockedCounterparty(
                CounterpartyType: BlockPartyTypes.FromSqlValue(reader.GetString(0)),
                CounterpartyId: reader.GetGuid(1),
                BlockedByMe: ReadFlag(reader, 2),
                BlockId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                BlockedAtUtc: new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero)));
        }

        return counterparties;
    }

    /// <summary>
    /// <see cref="IMyBlockLookup"/>. Swallows by contract: every caller is
    /// decorating something that already succeeded — a booking list, an event
    /// page, a conversation — so a blocked-table hiccup must cost a flag rather
    /// than the page. The consequence is a Report/Block button briefly offered
    /// when it should not be, which is recoverable; a 500 on the bookings list is
    /// not.
    /// </summary>
    async Task<MyBlockedCounterparties> IMyBlockLookup.GetAsync(
        BlockParty participant,
        CancellationToken cancellationToken)
    {
        try
        {
            var counterparties = await ListMyBlockedCounterpartiesAsync(participant, cancellationToken);
            return counterparties.Count == 0
                ? MyBlockedCounterparties.Empty
                : new MyBlockedCounterparties(counterparties);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return MyBlockedCounterparties.Empty;
        }
    }

    /// <summary>
    /// The seven columns every <c>Block.*</c> procedure projects first, in the
    /// same order — so one reader serves all of them. Anything extra (the
    /// already-blocked flag, the display columns, the total) is read by ordinal
    /// after this.
    /// </summary>
    private static ParticipantBlock ReadBlock(SqlDataReader reader) =>
        new(
            BlockId: reader.GetGuid(0),
            BlockerType: BlockPartyTypes.FromSqlValue(reader.GetString(1)),
            BlockerId: reader.GetGuid(2),
            BlockedType: BlockPartyTypes.FromSqlValue(reader.GetString(3)),
            BlockedId: reader.GetGuid(4),
            Reason: reader.IsDBNull(5) ? null : reader.GetString(5),
            BlockedName: null,
            BlockedBusinessName: null,
            BlockedPhotoUrl: null,
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero));

    /// <summary>
    /// Reads a flag without assuming it came back typed BIT. Every block
    /// procedure CASTs explicitly, but a CASE or COALESCE over an INT literal
    /// silently yields INT by data-type precedence and <c>GetBoolean</c> does not
    /// coerce — it throws, and in a write path it throws AFTER the transaction has
    /// committed. That exact defect took chat sending down; this is the cheap
    /// insurance against it happening here.
    /// </summary>
    private static bool ReadFlag(SqlDataReader reader, int ordinal) =>
        !reader.IsDBNull(ordinal) && Convert.ToBoolean(reader.GetValue(ordinal));

    private static SqlCommand StoredProcedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure };

    private async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString;
        }

        if (secretProvider is null)
        {
            throw new InvalidOperationException(
                "SQL Server connection string is not configured and no secret provider is registered.");
        }

        return await secretProvider.GetSqlConnectionStringAsync(cancellationToken);
    }
}
