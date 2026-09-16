using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Earnings;

namespace Pawfront.Infrastructure.Sql.Earnings;

/// <summary>
/// Reads a pet parent's booking counts, spend, and paginated history from
/// <c>Booking.GetPetParentBookingSummary</c> and
/// <c>Booking.ListPetParentBookingHistory</c> — both over the same
/// <c>Booking.BookingAmounts</c> function the provider earnings store uses.
/// </summary>
internal sealed class SqlParentSpendStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IParentSpendStore
{
    public async Task<ParentBookingSummary> GetSummaryAsync(
        Guid petParentId,
        DateOnly? fromDate,
        DateOnly? toDate,
        Guid? petId,
        IReadOnlyList<string> statuses,
        decimal feePercentage,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Booking].[GetPetParentBookingSummary]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        AddFilterParameters(command, petParentId, fromDate, toDate, petId, statuses, feePercentage);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return ParentBookingSummary.Empty;
        }

        return new ParentBookingSummary(
            TotalBookings: reader.GetInt32(0),
            SingleDayBookings: reader.GetInt32(1),
            NightStayBookings: reader.GetInt32(2),
            CompletedBookings: reader.GetInt32(3),
            CancelledBookings: reader.GetInt32(4),
            UpcomingBookings: reader.GetInt32(5),
            PaidBookings: reader.GetInt32(6),
            AwaitingPaymentBookings: reader.GetInt32(7),
            UnpricedBookings: reader.GetInt32(8),
            AmountSpent: reader.GetDecimal(9),
            UpcomingAmount: reader.GetDecimal(10));
    }

    public async Task<(IReadOnlyList<ParentBookingHistoryRow> Items, int TotalCount)> ListAsync(
        Guid petParentId,
        DateOnly? fromDate,
        DateOnly? toDate,
        Guid? petId,
        IReadOnlyList<string> statuses,
        decimal feePercentage,
        ParentHistorySortBy sortBy,
        EarningsSortDirection sortDirection,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Booking].[ListPetParentBookingHistory]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        AddFilterParameters(command, petParentId, fromDate, toDate, petId, statuses, feePercentage);
        command.Parameters.AddWithValue(
            "@SortBy", sortBy == ParentHistorySortBy.Amount ? "Amount" : "Date");
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
        var items = new List<ParentBookingHistoryRow>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadRow(reader));
            }
        }

        return (items, totalCount);
    }

    private static ParentBookingHistoryRow ReadRow(SqlDataReader reader)
    {
        var jobNumber = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);

        return new ParentBookingHistoryRow(
            BookingType: reader.GetString(0),
            BookingId: reader.GetGuid(1),
            JobId: $"PF-{jobNumber:D6}",
            Status: reader.GetString(3),
            ServiceId: reader.GetGuid(4),
            ServiceCategory: reader.GetString(5),
            SubCategory: reader.GetString(6),
            ServiceItemCode: reader.IsDBNull(7) ? null : reader.GetString(7),
            ServiceDate: DateOnly.FromDateTime(reader.GetDateTime(8)),
            BookingDate: reader.IsDBNull(9) ? null : DateOnly.FromDateTime(reader.GetDateTime(9)),
            StartTime: reader.IsDBNull(10) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(10)),
            EndTime: reader.IsDBNull(11) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(11)),
            CheckInDate: reader.IsDBNull(12) ? null : DateOnly.FromDateTime(reader.GetDateTime(12)),
            CheckOutDate: reader.IsDBNull(13) ? null : DateOnly.FromDateTime(reader.GetDateTime(13)),
            Nights: reader.IsDBNull(14) ? null : reader.GetInt32(14),
            ProviderId: reader.GetGuid(15),
            ProviderName: reader.IsDBNull(16) ? null : reader.GetString(16),
            PetId: reader.IsDBNull(17) ? null : reader.GetGuid(17),
            PetName: reader.IsDBNull(18) ? null : reader.GetString(18),
            PetProfilePhotoUrl: reader.IsDBNull(19) ? null : reader.GetString(19),
            IsCompleted: reader.GetBoolean(20),
            IsPaid: reader.GetBoolean(21),
            Amount: reader.IsDBNull(22) ? null : reader.GetDecimal(22),
            PaidAtUtc: reader.IsDBNull(23)
                ? null
                : new DateTimeOffset(reader.GetDateTime(23), TimeSpan.Zero),
            PaymentMethod: reader.IsDBNull(24) ? null : reader.GetString(24));
    }

    private static void AddFilterParameters(
        SqlCommand command,
        Guid petParentId,
        DateOnly? fromDate,
        DateOnly? toDate,
        Guid? petId,
        IReadOnlyList<string> statuses,
        decimal feePercentage)
    {
        command.Parameters.AddWithValue("@PetParentId", petParentId);
        command.Parameters.AddWithValue(
            "@FromDate",
            fromDate is null ? DBNull.Value : fromDate.Value.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue(
            "@ToDate",
            toDate is null ? DBNull.Value : toDate.Value.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@PetId", petId is null ? DBNull.Value : petId.Value);
        // The sprocs STRING_SPLIT this; an empty list means "no status filter".
        command.Parameters.AddWithValue(
            "@Statuses",
            statuses.Count == 0 ? DBNull.Value : string.Join(',', statuses));
        command.Parameters.AddWithValue("@FeePercentage", feePercentage);
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
