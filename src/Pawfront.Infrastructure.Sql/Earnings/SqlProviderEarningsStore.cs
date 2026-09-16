using Microsoft.Data.SqlClient;
using Pawfront.Application.Analytics;
using Pawfront.Application.Bookings;
using Pawfront.Application.Configuration;
using Pawfront.Application.Earnings;

namespace Pawfront.Infrastructure.Sql.Earnings;

/// <summary>
/// Reads provider earnings aggregates from <c>Booking.GetProviderEarningsSummary</c>,
/// <c>Booking.ListProviderEarningsBookings</c> and
/// <c>Booking.GetProviderBookingsByService</c>. All three sit on the shared
/// <c>Booking.BookingAmounts</c> function, so the summary, the per-service
/// breakdown and the booking list always reconcile.
/// </summary>
/// <remarks>
/// Serves <see cref="IProviderServiceBreakdownStore"/> as well: the per-service
/// breakdown is one more aggregate over the same function, and giving it its own
/// class would only duplicate the connection plumbing. Same reasoning as
/// <c>SqlBlockStore</c> serving two interfaces.
/// </remarks>
internal sealed class SqlProviderEarningsStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderEarningsStore, IProviderServiceBreakdownStore
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
            PrivateJobAmount: reader.GetDecimal(11),
            CancelledJobCount: reader.GetInt32(12),
            CancelledJobAmount: reader.GetDecimal(13),
            NoShowJobCount: reader.GetInt32(14),
            NoShowJobAmount: reader.GetDecimal(15),
            ExpiredJobCount: reader.GetInt32(16),
            ExpiredJobAmount: reader.GetDecimal(17))
        {
            // Appended LAST to the sproc's projection on purpose, so none of the
            // ordinals above moved. Set through an initialiser rather than the
            // positional constructor for the same reason: every other call site
            // (the Empty totals, the in-memory fallbacks) stays untouched.
            PendingBookings = reader.GetInt32(18),
            AcceptedBookings = reader.GetInt32(19),
            PrivateAcceptedJobs = reader.GetInt32(20)
        };
    }

    public async Task<(IReadOnlyList<ProviderEarningsBookingRow> Items, int TotalCount)> ListBookingsAsync(
        Guid providerId,
        DateOnly? fromDate,
        DateOnly? toDate,
        IReadOnlyList<string> statuses,
        decimal feePercentage,
        EarningsSortBy sortBy,
        EarningsSortDirection sortDirection,
        int skip,
        int take,
        Guid? serviceId,
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
        // Empty list => DBNull, which the sproc reads as "earned rows only" — the
        // behaviour callers had before the filter existed. Statuses are already
        // validated against BookingStatuses by BookingStatusFilter.Expand, so no
        // caller-supplied text reaches the CSV.
        command.Parameters.AddWithValue(
            "@Statuses",
            statuses.Count == 0 ? (object)DBNull.Value : string.Join(',', statuses));
        // The analytics drill-down: null is every service, exactly as before.
        command.Parameters.AddWithValue("@ServiceId", (object?)serviceId ?? DBNull.Value);

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
            // Appended LAST to the sproc's projection on purpose, so none of the
            // ordinals above moved.
            IsEarned: reader.GetBoolean(23),
            GrossAmount: reader.IsDBNull(19) ? null : reader.GetDecimal(19),
            PawfrontFee: reader.IsDBNull(20) ? null : reader.GetDecimal(20),
            PaidAtUtc: reader.IsDBNull(21)
                ? null
                : new DateTimeOffset(reader.GetDateTime(21), TimeSpan.Zero),
            PaymentMethod: reader.IsDBNull(22) ? null : reader.GetString(22),
            // Appended after [IsEarned] in the sproc's projection, so none of
            // the ordinals above moved.
            Breed: reader.IsDBNull(24) ? null : reader.GetString(24),
            PetGender: reader.IsDBNull(25) ? null : reader.GetString(25),
            CustomerPhotoUrl: reader.IsDBNull(26) ? null : reader.GetString(26));
    }

    public async Task<(ProviderBookingFigures Totals, IReadOnlyList<ProviderServiceBookingBreakdown> Services)>
        GetByServiceAsync(
            Guid providerId,
            DateOnly? fromDate,
            DateOnly? toDate,
            decimal feePercentage,
            CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Booking].[GetProviderBookingsByService]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        AddNullableDate(command, "@FromDate", fromDate);
        AddNullableDate(command, "@ToDate", toDate);
        command.Parameters.AddWithValue("@FeePercentage", feePercentage);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1: provider-wide totals. Degrade to zeros rather than throwing
        // if the sproc changes shape - same posture as GetSummaryAsync above.
        var totals = ProviderBookingFigures.Empty;
        if (await reader.ReadAsync(cancellationToken))
        {
            totals = ReadFigures(reader, offset: 0);
        }

        // Result set 2: the same 23 figures per service, behind five service
        // columns - hence the offset, and hence ONE reader for both result sets.
        var services = new List<ProviderServiceBookingBreakdown>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                services.Add(new ProviderServiceBookingBreakdown(
                    ServiceId: reader.GetGuid(0),
                    ServiceCategory: reader.GetString(1),
                    SubCategory: reader.GetString(2),
                    ServiceType: reader.GetString(3),
                    IsActive: reader.GetBoolean(4),
                    Figures: ReadFigures(reader, offset: 5)));
            }
        }

        return (totals, services);
    }

    /// <summary>
    /// The 23 figure columns both of <c>GetProviderBookingsByService</c>'s result
    /// sets project, in the same order. Reading both through one method is what
    /// guarantees the totals tile and a per-service tile are read identically.
    /// </summary>
    private static ProviderBookingFigures ReadFigures(SqlDataReader reader, int offset) =>
        new(TotalBookings: reader.GetInt32(offset),
            CompletedBookings: reader.GetInt32(offset + 1),
            PaidBookings: reader.GetInt32(offset + 2),
            AwaitingPaymentBookings: reader.GetInt32(offset + 3),
            UnpricedBookings: reader.GetInt32(offset + 4),
            UpcomingBookings: reader.GetInt32(offset + 5),
            GrossAmount: reader.GetDecimal(offset + 6),
            PawfrontFee: reader.GetDecimal(offset + 7),
            ReceivedGross: reader.GetDecimal(offset + 8),
            ReceivedFee: reader.GetDecimal(offset + 9),
            AwaitingGross: reader.GetDecimal(offset + 10),
            AwaitingFee: reader.GetDecimal(offset + 11),
            PrivateJobCount: reader.GetInt32(offset + 12),
            PrivateJobAmount: reader.GetDecimal(offset + 13),
            CancelledJobCount: reader.GetInt32(offset + 14),
            CancelledJobAmount: reader.GetDecimal(offset + 15),
            NoShowJobCount: reader.GetInt32(offset + 16),
            NoShowJobAmount: reader.GetDecimal(offset + 17),
            ExpiredJobCount: reader.GetInt32(offset + 18),
            ExpiredJobAmount: reader.GetDecimal(offset + 19))
        {
            // Appended LAST to BOTH of the sproc's result sets, in the same order,
            // so this one reader still serves them at their two offsets.
            PendingBookings = reader.GetInt32(offset + 20),
            AcceptedBookings = reader.GetInt32(offset + 21),
            PrivateAcceptedJobs = reader.GetInt32(offset + 22)
        };

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
