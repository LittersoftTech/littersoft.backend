using Microsoft.Data.SqlClient;
using Pawfront.Application.Bookings;
using Pawfront.Application.Configuration;
using Pawfront.Application.Earnings;

namespace Pawfront.Infrastructure.Sql.Earnings;

/// <summary>
/// Reads provider earnings aggregates from <c>Booking.GetProviderEarningsSummary</c>
/// and <c>Booking.ListProviderEarningsBookings</c>. Both sprocs sit on the shared
/// <c>Booking.BookingAmounts</c> function, so the summary and the breakdown always
/// reconcile.
/// </summary>
internal sealed class SqlProviderEarningsStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderEarningsStore
{
    public async Task<ProviderEarningsTotals> GetSummaryAsync(
        Guid providerId,
        DateOnly? fromDate,
        DateOnly? toDate,
        decimal feePercentage,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Booking].[GetProviderEarningsSummary]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        AddNullableDate(command, "@FromDate", fromDate);
        AddNullableDate(command, "@ToDate", toDate);
        command.Parameters.AddWithValue("@FeePercentage", feePercentage);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            // The aggregate always returns one row, so this only happens if the
            // sproc changes shape. Degrade to zeros rather than throwing on a
            // read-only reporting screen.
            return ProviderEarningsTotals.Empty;
        }

        return new ProviderEarningsTotals(
            CompletedBookings: reader.GetInt32(0),
            PaidBookings: reader.GetInt32(1),
            AwaitingPaymentBookings: reader.GetInt32(2),
            UnpricedBookings: reader.GetInt32(3),
            GrossAmount: reader.GetDecimal(4),
            PawfrontFee: reader.GetDecimal(5),
            ReceivedGross: reader.GetDecimal(6),
            ReceivedFee: reader.GetDecimal(7),
            AwaitingGross: reader.GetDecimal(8),
            AwaitingFee: reader.GetDecimal(9),
            PrivateJobCount: reader.GetInt32(10),
            PrivateJobAmount: reader.GetDecimal(11));
    }

    public async Task<(IReadOnlyList<ProviderEarningsBookingRow> Items, int TotalCount)> ListBookingsAsync(
        Guid providerId,
        DateOnly? fromDate,
        DateOnly? toDate,
        decimal feePercentage,
        EarningsSortBy sortBy,
        EarningsSortDirection sortDirection,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Booking].[ListProviderEarningsBookings]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        AddNullableDate(command, "@FromDate", fromDate);
        AddNullableDate(command, "@ToDate", toDate);
        command.Parameters.AddWithValue("@FeePercentage", feePercentage);
        command.Parameters.AddWithValue(
            "@SortBy", sortBy == EarningsSortBy.Earnings ? "Earnings" : "Date");
        command.Parameters.AddWithValue(
            "@SortDirection", sortDirection == EarningsSortDirection.Ascending ? "Asc" : "Desc");
        command.Parameters.AddWithValue("@Skip", skip);
        command.Parameters.AddWithValue("@Take", take);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1: the unpaged total.
        var totalCount = 0;
        if (await reader.ReadAsync(cancellationToken))
        {
            totalCount = reader.GetInt32(0);
        }

        // Result set 2: the page.
        var items = new List<ProviderEarningsBookingRow>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadRow(reader));
            }
        }

        return (items, totalCount);
    }

    private static ProviderEarningsBookingRow ReadRow(SqlDataReader reader)
    {
        var jobNumber = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);

        return new ProviderEarningsBookingRow(
            BookingType: reader.GetString(0),
            BookingId: reader.GetGuid(1),
            // Same 'PF-000123' formatting the booking-detail read uses, so a
            // provider sees one id for a job across both screens.
            JobId: $"PF-{jobNumber:D6}",
            PayoutId: reader.IsDBNull(3) ? null : reader.GetString(3),
            PayoutStatus: reader.IsDBNull(4) ? PayoutStatuses.Pending : reader.GetString(4),
            Status: reader.GetString(5),
            ServiceCategory: reader.GetString(6),
            SubCategory: reader.GetString(7),
            ServiceItemCode: reader.IsDBNull(8) ? null : reader.GetString(8),
            ServiceDate: DateOnly.FromDateTime(reader.GetDateTime(9)),
            StartTime: reader.IsDBNull(10) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(10)),
            EndTime: reader.IsDBNull(11) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(11)),
            CheckInDate: reader.IsDBNull(12) ? null : DateOnly.FromDateTime(reader.GetDateTime(12)),
            CheckOutDate: reader.IsDBNull(13) ? null : DateOnly.FromDateTime(reader.GetDateTime(13)),
            Nights: reader.IsDBNull(14) ? null : reader.GetInt32(14),
            CustomerName: reader.IsDBNull(15) ? null : reader.GetString(15),
            PetName: reader.IsDBNull(16) ? null : reader.GetString(16),
            IsPaid: reader.GetBoolean(17),
            IsPrivate: reader.GetBoolean(18),
            GrossAmount: reader.IsDBNull(19) ? null : reader.GetDecimal(19),
            PawfrontFee: reader.IsDBNull(20) ? null : reader.GetDecimal(20),
            PaidAtUtc: reader.IsDBNull(21)
                ? null
                : new DateTimeOffset(reader.GetDateTime(21), TimeSpan.Zero),
            PaymentMethod: reader.IsDBNull(22) ? null : reader.GetString(22));
    }

    private static void AddNullableDate(SqlCommand command, string name, DateOnly? value)
        => command.Parameters.AddWithValue(
            name,
            value is null ? DBNull.Value : value.Value.ToDateTime(TimeOnly.MinValue));

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
