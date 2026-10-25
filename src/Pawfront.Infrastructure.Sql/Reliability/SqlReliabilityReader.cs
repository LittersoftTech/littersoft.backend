using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Reliability;
using Pawfront.Contracts.Reliability;

namespace Pawfront.Infrastructure.Sql.Reliability;

internal sealed class SqlReliabilityReader(string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IReliabilityReader
{
    public Task<ReliabilityResponse> GetParentAsync(Guid petParentId, CancellationToken ct) => ReadAsync(petParentId, "PetParent", ct);
    public Task<ReliabilityResponse> GetProviderAsync(Guid providerId, CancellationToken ct) => ReadAsync(providerId, "Provider", ct);

    private async Task<ReliabilityResponse> ReadAsync(Guid accountId, string accountType, CancellationToken ct)
    {
        await using var connection = new SqlConnection(!string.IsNullOrWhiteSpace(configuredConnectionString)
            ? configuredConnectionString : await secretProvider!.GetSqlConnectionStringAsync(ct));
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand("Booking.GetAccountReliability", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@AccountId", accountId);
        command.Parameters.AddWithValue("@AccountType", accountType);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var now = Utc(reader.GetDateTime(0));
        var hosting = new HostHistoryResponse(reader.GetBoolean(1), reader.GetInt32(2), reader.GetInt32(3), now.AddMonths(-12));

        await reader.NextResultAsync(ct);
        await reader.ReadAsync(ct);
        var flagged = reader.GetBoolean(0);
        var attended = reader.GetInt32(2);
        var active = reader.GetInt32(3);
        var limit = flagged ? 1 : 5;
        var community = new CommunityReliabilityResponse(flagged, reader.IsDBNull(1) ? null : Utc(reader.GetDateTime(1)),
            reader.GetInt32(4), attended, flagged ? Math.Max(0, 2 - attended) : 0, !flagged,
            limit, active, Math.Max(0, limit - (flagged ? reader.GetInt32(5) : active)),
            ActivePaidBookingCount: reader.GetInt32(5));

        await reader.NextResultAsync(ct);
        var marks = new List<ServiceReliabilityMark>();
        while (await reader.ReadAsync(ct))
            marks.Add(new(reader.GetString(0), reader.GetGuid(1), reader.GetString(2), Utc(reader.GetDateTime(3))));
        return new(accountType == "PetParent" ? ServiceReliabilityCalculator.Calculate(marks, now)
            : ServiceReliabilityCalculator.NotApplicable(), community, hosting, now);
    }

    private static DateTimeOffset Utc(DateTime value) => new(value, TimeSpan.Zero);
}
