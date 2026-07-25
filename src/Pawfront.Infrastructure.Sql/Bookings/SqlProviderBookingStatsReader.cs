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

        // "Completed" = the booking was explicitly marked COMPLETED by the
        // provider, OR its window has already ended (served in practice even if
        // never tapped Complete) — and in either case it wasn't cancelled by
        // either party nor recorded as a no-show. Without the explicit COMPLETED
        // arm, a booking finished ahead of / independently of its scheduled window
        // (e.g. a future-dated appointment the provider already completed) was
        // undercounted, which showed up as completedBookings = 0. Counts both app
        // and provider-added custom bookings — the number reads as overall
        // provider experience — across ALL of a provider's services regardless of
        // category or freelance/business sub-category.
        //
        // Two sources are UNIONed and summed per provider: single-day
        // [Booking].[Bookings] (window = BookingDate/EndTime) and multi-night
        // [Booking].[NightStayBookings] (window "elapsed" = the checkout date has
        // passed — CheckOutDate is the pickup day, NOT a stayed night, so the stay
        // is over once CheckOutDate < today). Without the night-stay arm a PetSitter
        // whose only completed jobs are boarding stays showed completedBookings = 0.
        await using var command = new SqlCommand(
            "DECLARE @Today DATE = CONVERT(date, SYSUTCDATETIME()); " +
            "DECLARE @Now TIME(0) = CONVERT(time(0), SYSUTCDATETIME()); " +
            "SELECT src.[ProviderId], SUM(src.[Cnt]) " +
            "FROM ( " +
            "    SELECT b.[ProviderId], COUNT(*) AS [Cnt] " +
            "    FROM [Booking].[Bookings] b " +
            "    INNER JOIN STRING_SPLIT(@ProviderIds, ',') ids " +
            "        ON b.[ProviderId] = TRY_CONVERT(UNIQUEIDENTIFIER, ids.[value]) " +
            "    WHERE b.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_ATTEMPTS_EXCEEDED') " +
            "      AND (b.[Status] IN (N'COMPLETED', N'PAID') " +
            "           OR b.[BookingDate] < @Today " +
            "           OR (b.[BookingDate] = @Today AND b.[EndTime] <= @Now)) " +
            "    GROUP BY b.[ProviderId] " +
            "    UNION ALL " +
            "    SELECT n.[ProviderId], COUNT(*) AS [Cnt] " +
            "    FROM [Booking].[NightStayBookings] n " +
            "    INNER JOIN STRING_SPLIT(@ProviderIds, ',') ids " +
            "        ON n.[ProviderId] = TRY_CONVERT(UNIQUEIDENTIFIER, ids.[value]) " +
            "    WHERE n.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_ATTEMPTS_EXCEEDED') " +
            "      AND (n.[Status] IN (N'COMPLETED', N'PAID') " +
            "           OR n.[CheckOutDate] < @Today) " +
            "    GROUP BY n.[ProviderId] " +
            ") src " +
            "GROUP BY src.[ProviderId];",
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
