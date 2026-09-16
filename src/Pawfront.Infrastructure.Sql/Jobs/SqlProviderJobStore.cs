using Microsoft.Data.SqlClient;
using Pawfront.Application.Bookings;
using Pawfront.Application.Configuration;
using Pawfront.Application.Earnings;
using Pawfront.Application.Jobs;

namespace Pawfront.Infrastructure.Sql.Jobs;

/// <summary>
/// Reads the provider's job list from <c>Booking.ListProviderJobs</c>. That
/// procedure sits on the shared <c>Booking.BookingAmounts</c> function, so the
/// amount shown against a job here is the same number the earnings screen reports
/// for it.
/// </summary>
internal sealed class SqlProviderJobStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderJobStore
{
    public async Task<ProviderJobPage> ListAsync(
        ProviderJobQuery query,
        decimal feePercentage,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Booking].[ListProviderJobs]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", query.ProviderId);
        command.Parameters.AddWithValue("@FeePercentage", feePercentage);
        // Every list filter is a CSV of values already validated in C# — statuses
        // by BookingStatusFilter.Expand, the other two by
        // ProviderJobQueryParsing — so no caller-supplied text reaches the string.
        // Empty means "no filter on that dimension", which for this screen is the
        // whole job history rather than the earnings list's earned-rows default.
        AddCsv(command, "@Statuses", query.Statuses);
        AddCsv(command, "@ServiceTypes", query.ServiceTypes);
        AddCsv(command, "@AnimalTypes", query.AnimalTypes);
        command.Parameters.AddWithValue("@ServiceId", (object?)query.ServiceId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@LocationType", (object?)query.LocationType ?? DBNull.Value);
        // Free text: the sproc escapes LIKE metacharacters and does a
        // case-insensitive "contains" match.
        command.Parameters.AddWithValue("@Breed", (object?)query.Breed ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@MinEarnings", (object?)query.MinEarnings ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@MaxEarnings", (object?)query.MaxEarnings ?? DBNull.Value);
        AddNullableDate(command, "@FromDate", query.From);
        AddNullableDate(command, "@ToDate", query.To);
        command.Parameters.AddWithValue(
            "@SortBy", query.SortBy == ProviderJobSortBy.Earnings ? "Earnings" : "Date");
        command.Parameters.AddWithValue(
            "@SortDirection",
            query.SortDirection == EarningsSortDirection.Ascending ? "Asc" : "Desc");
        command.Parameters.AddWithValue("@Skip", query.Skip);
        command.Parameters.AddWithValue("@Take", query.Take);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1: the unpaged total.
        var totalCount = 0;
        if (await reader.ReadAsync(cancellationToken))
        {
            totalCount = reader.GetInt32(0);
        }

        // Result set 2: the page.
        var items = new List<ProviderJobRow>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(ReadRow(reader));
            }
        }

        return new ProviderJobPage(items, totalCount, query.Skip, query.Take);
    }

    private static ProviderJobRow ReadRow(SqlDataReader reader)
    {
        var jobNumber = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);

        return new ProviderJobRow(
            BookingType: reader.GetString(0),
            BookingId: reader.GetGuid(1),
            // Same 'PF-000123' formatting the booking-detail read and the earnings
            // list use, so a provider sees one id for a job across every screen.
            JobId: $"PF-{jobNumber:D6}",
            Status: reader.GetString(5),
            IsEarned: reader.GetBoolean(6),
            IsPaid: reader.GetBoolean(7),
            IsPrivate: reader.GetBoolean(8),
            ServiceId: reader.GetGuid(13),
            ServiceType: reader.IsDBNull(14) ? null : reader.GetString(14),
            ServiceCategory: reader.GetString(15),
            SubCategory: reader.GetString(16),
            ServiceItemCode: reader.IsDBNull(17) ? null : reader.GetString(17),
            JobDate: DateOnly.FromDateTime(reader.GetDateTime(18)),
            CheckOutDate: reader.IsDBNull(19) ? null : DateOnly.FromDateTime(reader.GetDateTime(19)),
            Nights: reader.IsDBNull(20) ? null : reader.GetInt32(20),
            StartTime: reader.IsDBNull(21) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(21)),
            EndTime: reader.IsDBNull(22) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(22)),
            LocationType: reader.IsDBNull(23) ? null : reader.GetString(23),
            AddressLine: reader.IsDBNull(24) ? null : reader.GetString(24),
            City: reader.IsDBNull(25) ? null : reader.GetString(25),
            ZipCode: reader.IsDBNull(26) ? null : reader.GetString(26),
            JobNotes: reader.IsDBNull(27) ? null : reader.GetString(27),
            PetParentId: reader.IsDBNull(28) ? null : reader.GetGuid(28),
            CustomerName: reader.IsDBNull(29) ? null : reader.GetString(29),
            CustomerPhotoUrl: reader.IsDBNull(30) ? null : reader.GetString(30),
            PetId: reader.IsDBNull(31) ? null : reader.GetGuid(31),
            PetName: reader.IsDBNull(32) ? null : reader.GetString(32),
            AnimalType: reader.IsDBNull(33) ? null : reader.GetString(33),
            Breed: reader.IsDBNull(34) ? null : reader.GetString(34),
            PetGender: reader.IsDBNull(35) ? null : reader.GetString(35),
            Amount: reader.IsDBNull(9) ? null : reader.GetDecimal(9),
            Fee: reader.IsDBNull(10) ? null : reader.GetDecimal(10),
            PayoutId: reader.IsDBNull(3) ? null : reader.GetString(3),
            PayoutStatus: reader.IsDBNull(4) ? PayoutStatuses.Pending : reader.GetString(4),
            PaidAtUtc: reader.IsDBNull(11)
                ? null
                : new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero),
            PaymentMethod: reader.IsDBNull(12) ? null : reader.GetString(12),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(36), TimeSpan.Zero));
    }

    private static void AddCsv(
        SqlCommand command,
        string name,
        IReadOnlyCollection<string>? values)
        => command.Parameters.AddWithValue(
            name,
            values is null || values.Count == 0
                ? (object)DBNull.Value
                : string.Join(',', values));

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
