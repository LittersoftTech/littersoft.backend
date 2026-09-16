using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Analytics;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Sql.Analytics;

/// <summary>
/// The provider view log, through the three <c>Provider</c> procedures that own it
/// — <c>RecordProviderServiceView</c>, <c>GetProviderViewSummary</c> and
/// <c>ListProviderServiceViewers</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every <c>DATETIME2</c> is pinned to <see cref="TimeSpan.Zero"/> on the way out.
/// The columns carry no offset and this codebase stores UTC throughout, so letting
/// <see cref="DateTimeOffset"/> infer the server's local offset would silently
/// shift every timestamp.
/// </para>
/// <para>
/// The view columns are <c>DATETIME2(3)</c> rather than the (7) used elsewhere:
/// this is the highest-volume write in the product and millisecond precision is
/// more than an analytics timestamp needs.
/// </para>
/// </remarks>
internal sealed class SqlProviderServiceViewStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderServiceViewStore
{
    public async Task<ProviderViewRecord> RecordAsync(
        RecordProviderViewCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var sqlCommand = StoredProcedure(connection, "[Provider].[RecordProviderServiceView]");
        sqlCommand.Parameters.AddWithValue("@ProviderId", command.ProviderId);
        sqlCommand.Parameters.AddWithValue("@ServiceId", (object?)command.ServiceId ?? DBNull.Value);
        sqlCommand.Parameters.AddWithValue("@PetParentId", (object?)command.PetParentId ?? DBNull.Value);
        sqlCommand.Parameters.AddWithValue("@PetId", (object?)command.PetId ?? DBNull.Value);
        sqlCommand.Parameters.AddWithValue("@Source", (object?)command.Source ?? DBNull.Value);

        try
        {
            await using var reader = await sqlCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Provider.RecordProviderServiceView returned no row.");
            }

            return new ProviderViewRecord(
                ProviderServiceViewId: reader.GetGuid(0),
                ProviderId: reader.GetGuid(1),
                ServiceId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
                PetParentId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                PetId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
                Source: reader.IsDBNull(5) ? null : reader.GetString(5),
                ViewedAtUtc: new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero));
        }
        catch (SqlException exception) when (exception.Number == 51410)
        {
            throw new ProviderViewProviderNotFoundException(command.ProviderId);
        }
        catch (SqlException exception) when (exception.Number == 51411)
        {
            throw new ProviderViewInvalidServiceException(command.ServiceId ?? Guid.Empty);
        }
        catch (SqlException exception) when (exception.Number == 51412)
        {
            throw new ProviderViewInvalidPetException(command.PetId ?? Guid.Empty);
        }
    }

    public async Task<(ProviderViewTotals Totals, IReadOnlyList<ProviderServiceViewBreakdown> Services)>
        GetSummaryAsync(
            Guid providerId,
            DateOnly? fromDate,
            DateOnly? toDate,
            CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Provider].[GetProviderViewSummary]");
        command.Parameters.AddWithValue("@ProviderId", providerId);
        AddNullableDate(command, "@FromDate", fromDate);
        AddNullableDate(command, "@ToDate", toDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1: the headline card. The aggregate always returns one row, so
        // an empty read means the procedure changed shape — degrade to zeros rather
        // than throwing on a read-only reporting screen, the same posture the
        // earnings summary reader takes.
        var totals = ProviderViewTotals.Empty;
        if (await reader.ReadAsync(cancellationToken))
        {
            totals = new ProviderViewTotals(
                TotalViews: reader.GetInt32(0),
                UniqueViewers: reader.GetInt32(1),
                UnattributedViews: reader.GetInt32(2),
                AnonymousViews: reader.GetInt32(3),
                LastViewedAtUtc: reader.IsDBNull(4)
                    ? null
                    : new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero));
        }

        // Result set 2: one row per service the provider has, zeros included.
        var services = new List<ProviderServiceViewBreakdown>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                services.Add(new ProviderServiceViewBreakdown(
                    ServiceId: reader.GetGuid(0),
                    ServiceCategory: reader.GetString(1),
                    SubCategory: reader.GetString(2),
                    ServiceType: reader.GetString(3),
                    IsActive: reader.GetBoolean(4),
                    Views: reader.GetInt32(5),
                    UniqueViewers: reader.GetInt32(6),
                    LastViewedAtUtc: reader.IsDBNull(7)
                        ? null
                        : new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero)));
            }
        }

        return (totals, services);
    }

    public async Task<(IReadOnlyList<ProviderServiceViewerRow> Items, int TotalCount)> ListViewersAsync(
        Guid providerId,
        Guid? serviceId,
        DateOnly? fromDate,
        DateOnly? toDate,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = StoredProcedure(connection, "[Provider].[ListProviderServiceViewers]");
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@ServiceId", (object?)serviceId ?? DBNull.Value);
        AddNullableDate(command, "@FromDate", fromDate);
        AddNullableDate(command, "@ToDate", toDate);
        command.Parameters.AddWithValue("@Skip", skip);
        command.Parameters.AddWithValue("@Take", take);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1: the unpaged total (distinct parents).
        var totalCount = 0;
        if (await reader.ReadAsync(cancellationToken))
        {
            totalCount = reader.GetInt32(0);
        }

        // Result set 2: the page.
        var items = new List<ProviderServiceViewerRow>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new ProviderServiceViewerRow(
                    PetParentId: reader.GetGuid(0),
                    // Live-joined, so an anonymised account reads "Deleted User".
                    // Still nullable: the join is LEFT, and this is an analytics log
                    // with no FK to the parent table.
                    ParentName: reader.IsDBNull(1) ? null : reader.GetString(1),
                    ParentPhotoUrl: reader.IsDBNull(2) ? null : reader.GetString(2),
                    PetId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    PetName: reader.IsDBNull(4) ? null : reader.GetString(4),
                    PetType: reader.IsDBNull(5) ? null : reader.GetString(5),
                    Breed: reader.IsDBNull(6) ? null : reader.GetString(6),
                    PetGender: reader.IsDBNull(7) ? null : reader.GetString(7),
                    ViewCount: reader.GetInt32(8),
                    FirstViewedAtUtc: new DateTimeOffset(reader.GetDateTime(9), TimeSpan.Zero),
                    LastViewedAtUtc: new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero)));
            }
        }

        return (items, totalCount);
    }

    private static SqlCommand StoredProcedure(SqlConnection connection, string name) =>
        new(name, connection) { CommandType = CommandType.StoredProcedure };

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
