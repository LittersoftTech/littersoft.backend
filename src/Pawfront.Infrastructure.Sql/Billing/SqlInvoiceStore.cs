using Microsoft.Data.SqlClient;
using Pawfront.Application.Billing;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Sql.Billing;

/// <summary>
/// Reads <c>Billing.Invoices</c> for the download endpoints, via
/// <c>Billing.GetBookingInvoice</c>.
///
/// Read-only by design. Invoices are RAISED by
/// <c>Billing.RaiseBookingInvoices</c>, called from inside the mark-paid
/// transaction, and RENDERED by <c>Pawfront.Functions</c> — neither path goes
/// through the API hosts, so neither belongs here.
/// </summary>
internal sealed class SqlInvoiceStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IInvoiceStore
{
    public async Task<BookingInvoice?> GetAsync(
        InvoiceDownloadQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Billing].[GetBookingInvoice]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingType", query.BookingType);
        command.Parameters.AddWithValue("@BookingId", query.BookingId);
        command.Parameters.AddWithValue("@Recipient", query.Recipient);
        // Exactly one of these is supplied, by the calling host. The sproc THROWs
        // 51401 if both are null rather than returning the row unscoped.
        command.Parameters.AddWithValue(
            "@ProviderId", query.ProviderId is null ? DBNull.Value : query.ProviderId.Value);
        command.Parameters.AddWithValue(
            "@PetParentId", query.PetParentId is null ? DBNull.Value : query.PetParentId.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // No row means "no such invoice" OR "not yours" — deliberately the same
        // answer, so an id cannot be probed.
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BookingInvoice(
            InvoiceId: reader.GetGuid(0),
            InvoiceNumber: reader.GetString(1),
            BookingType: reader.GetString(2),
            BookingId: reader.GetGuid(3),
            Recipient: reader.GetString(4),
            ProviderId: reader.GetGuid(5),
            PetParentId: reader.GetGuid(6),
            Amount: reader.GetDecimal(7),
            PawfrontFee: reader.GetDecimal(8),
            Status: reader.GetString(9),
            InvoiceUrl: reader.IsDBNull(10) ? null : reader.GetString(10),
            // DATETIME2 columns carry no offset; the codebase stores UTC
            // throughout, so the offset is pinned rather than inferred.
            IssuedAtUtc: new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero),
            GeneratedAtUtc: reader.IsDBNull(12)
                ? null
                : new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero));
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
