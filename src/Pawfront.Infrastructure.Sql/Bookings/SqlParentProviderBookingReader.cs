using Microsoft.Data.SqlClient;
using Pawfront.Application.Bookings;
using Pawfront.Application.Configuration;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Infrastructure.Sql.ParentOnboarding;

namespace Pawfront.Infrastructure.Sql.Bookings;

/// <summary>
/// Reads a provider and a pet parent's shared job history through
/// <c>Booking.ListBookingsForParentProvider</c>.
/// </summary>
/// <remarks>
/// The rows come back in the same 16-column shape the two delete refusals use, so
/// <see cref="PendingJobReader"/> reads all three — one job card everywhere it
/// appears. The 17th column is the whole-result count, repeated on every row by
/// <c>COUNT(*) OVER()</c>, which is what lets one round trip serve both the page
/// and its total.
/// </remarks>
internal sealed class SqlParentProviderBookingReader(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IParentProviderBookingReader
{
    private const int TotalCountOrdinal = 16;

    public async Task<ParentProviderBookingPage> ListAsync(
        Guid providerId,
        Guid petParentId,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Booking].[ListBookingsForParentProvider]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@PetParentId", petParentId);
        command.Parameters.AddWithValue("@Skip", skip);
        command.Parameters.AddWithValue("@Take", take);

        var jobs = new List<PendingParentJob>();
        var totalCount = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(PendingJobReader.Read(reader));

            // Identical on every row; read once. An empty page never sets it,
            // which is correct — no rows means no jobs.
            totalCount = reader.GetInt32(TotalCountOrdinal);
        }

        return new ParentProviderBookingPage(jobs, totalCount);
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
