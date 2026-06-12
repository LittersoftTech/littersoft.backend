using Microsoft.Data.SqlClient;
using Pawfront.Application.Bookings;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Sql.Bookings;

internal sealed class SqlProviderBookingStatsReader(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderBookingStatsReader
{
    public async Task<IReadOnlyDictionary<Guid, int>> GetCompletedBookingCountsAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken)
    {
        if (providerIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        // "Completed" = the booking window has already ended and the booking
        // wasn't cancelled or a no-show. Counts both app and provider-added
        // custom bookings — the number reads as overall provider experience.
        await using var command = new SqlCommand(
            "DECLARE @Today DATE = CONVERT(date, SYSUTCDATETIME()); " +
            "DECLARE @Now TIME(0) = CONVERT(time(0), SYSUTCDATETIME()); " +
            "SELECT b.[ProviderId], COUNT(*) " +
            "FROM [Booking].[Bookings] b " +
            "INNER JOIN STRING_SPLIT(@ProviderIds, ',') ids " +
            "    ON b.[ProviderId] = TRY_CONVERT(UNIQUEIDENTIFIER, ids.[value]) " +
            "WHERE b.[Status] IN (N'Confirmed', N'Completed') " +
            "  AND (b.[BookingDate] < @Today " +
            "       OR (b.[BookingDate] = @Today AND b.[EndTime] <= @Now)) " +
            "GROUP BY b.[ProviderId];",
            connection);
        command.Parameters.AddWithValue(
            "@ProviderIds", string.Join(',', providerIds.Select(id => id.ToString("D"))));

        var counts = new Dictionary<Guid, int>(providerIds.Count);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            counts[reader.GetGuid(0)] = reader.GetInt32(1);
        }
        return counts;
    }

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
