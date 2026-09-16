using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Billing;
using Pawfront.Application.Configuration;

namespace Pawfront.Functions.Invoices;

/// <summary>
/// The renderer's side of <c>Billing.Invoices</c> — claim, complete, sweep.
///
/// Talks to SQL directly, exactly as the booking sweeps and
/// <c>SqlNotificationOutboxStore</c> do, so this host does not take a dependency
/// on <c>Pawfront.Infrastructure.Sql</c>. The API hosts' <c>SqlInvoiceStore</c> is
/// the read-only counterpart and shares nothing with this by design: they are the
/// two ends of an outbox, not one store used twice.
/// </summary>
internal sealed class SqlInvoiceGenerationStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider,
    ILogger<SqlInvoiceGenerationStore> logger) : IInvoiceGenerationStore
{
    public async Task<InvoiceClaim> ClaimAsync(
        string bookingType,
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Billing].[ClaimBookingInvoicesForGeneration]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingType", bookingType);
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@MaxAttempts", InvoiceLimits.MaxAttempts);
        command.Parameters.AddWithValue("@LeaseMinutes", InvoiceLimits.LeaseMinutes);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1 — the shared booking + parties payload.
        InvoiceBookingFacts? facts = null;
        if (await reader.ReadAsync(cancellationToken))
        {
            facts = new InvoiceBookingFacts(
                BookingId: reader.GetGuid(0),
                BookingType: reader.GetString(1),
                JobId: reader.GetString(2),
                ProviderId: reader.GetGuid(3),
                PetParentId: reader.GetGuid(4),
                ServiceCategory: reader.GetString(5),
                SubCategory: reader.GetString(6),
                ServiceItemCode: reader.IsDBNull(7) ? null : reader.GetString(7),
                ServiceDate: DateOnly.FromDateTime(reader.GetDateTime(8)),
                EndDate: reader.IsDBNull(9) ? null : DateOnly.FromDateTime(reader.GetDateTime(9)),
                StartTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(10)),
                EndTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(11)),
                UnitPrice: reader.IsDBNull(12) ? null : reader.GetDecimal(12),
                ProviderFirstName: reader.IsDBNull(13) ? null : reader.GetString(13),
                ProviderLastName: reader.IsDBNull(14) ? null : reader.GetString(14),
                ProviderMobileCountryCode: reader.IsDBNull(15) ? null : reader.GetString(15),
                ProviderMobileNumber: reader.IsDBNull(16) ? null : reader.GetString(16),
                ProviderEmail: reader.IsDBNull(17) ? null : reader.GetString(17),
                ParentFirstName: reader.IsDBNull(18) ? null : reader.GetString(18),
                ParentLastName: reader.IsDBNull(19) ? null : reader.GetString(19),
                ParentAddressLine: reader.IsDBNull(20) ? null : reader.GetString(20),
                ParentCity: reader.IsDBNull(21) ? null : reader.GetString(21),
                ParentZipCode: reader.IsDBNull(22) ? null : reader.GetString(22),
                ParentEmail: reader.IsDBNull(23) ? null : reader.GetString(23),
                PetName: reader.IsDBNull(24) ? null : reader.GetString(24),
                PetType: reader.IsDBNull(25) ? null : reader.GetString(25),
                PetBreed: reader.IsDBNull(26) ? null : reader.GetString(26),
                // DATETIME2 carries no offset; this codebase stores UTC throughout.
                PaidAtUtc: reader.IsDBNull(27)
                    ? null
                    : new DateTimeOffset(reader.GetDateTime(27), TimeSpan.Zero),
                PaymentMethod: reader.IsDBNull(28) ? null : reader.GetString(28),
                // Appended LAST to the sproc's projection on purpose, so none of
                // the ordinals above moved.
                ServiceType: reader.IsDBNull(29) ? null : reader.GetString(29));
        }

        // Result set 2 — what this call actually claimed. Empty means already
        // rendered, or somebody else holds the lease; either way there is nothing
        // to do and the message should be dequeued rather than retried.
        var invoices = new List<ClaimedInvoice>(2);
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                invoices.Add(new ClaimedInvoice(
                    InvoiceId: reader.GetGuid(0),
                    InvoiceNumber: reader.GetString(1),
                    Recipient: reader.GetString(2),
                    Amount: reader.GetDecimal(3),
                    PawfrontFee: reader.GetDecimal(4),
                    IssuedAtUtc: new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero),
                    AttemptCount: reader.GetInt32(6)));
            }
        }

        return new InvoiceClaim(facts, invoices);
    }

    public async Task CompleteAsync(
        Guid invoiceId,
        string invoiceUrl,
        string? issuedBy,
        CancellationToken cancellationToken)
        => await ExecuteCompleteAsync(invoiceId, true, invoiceUrl, null, issuedBy, cancellationToken);

    public async Task FailAsync(
        Guid invoiceId,
        string error,
        CancellationToken cancellationToken)
        // The column is NVARCHAR(2000); truncate rather than let a long stack
        // trace fail the very call that is recording a failure.
        => await ExecuteCompleteAsync(
            invoiceId, false, null,
            error.Length > 2000 ? error[..2000] : error,
            null,
            cancellationToken);

    private async Task ExecuteCompleteAsync(
        Guid invoiceId,
        bool succeeded,
        string? invoiceUrl,
        string? error,
        string? issuedBy,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Billing].[CompleteInvoiceGeneration]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@InvoiceId", invoiceId);
        command.Parameters.AddWithValue("@Succeeded", succeeded);
        command.Parameters.AddWithValue("@InvoiceUrl", (object?)invoiceUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@Error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("@MaxAttempts", InvoiceLimits.MaxAttempts);
        // Only meaningful on the success path, where the procedure uses it to name
        // the issuer in the parent's "invoice ready" push.
        command.Parameters.AddWithValue("@IssuedBy", (object?)issuedBy ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InvoiceGenerationRequest>> ListUnrenderedAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Billing].[ListUnrenderedInvoiceBookings]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@GraceMinutes", InvoiceLimits.SweepGraceMinutes);
        command.Parameters.AddWithValue("@MaxAttempts", InvoiceLimits.MaxAttempts);
        command.Parameters.AddWithValue("@BatchSize", batchSize);

        var results = new List<InvoiceGenerationRequest>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var bookingType = reader.GetString(0);
            var bookingId = reader.GetGuid(1);
            var waitingSince = reader.GetDateTime(2);
            var attempts = reader.GetInt32(3);

            logger.LogWarning(
                "Invoice generation for {BookingType} booking {BookingId} has been waiting since " +
                "{WaitingSince:u} ({Attempts} attempt(s)); re-queueing.",
                bookingType, bookingId, waitingSince, attempts);

            results.Add(new InvoiceGenerationRequest(bookingType, bookingId));
        }

        return results;
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

/// <summary>
/// The renderer's contract with SQL. Public because the Functions runtime requires
/// a function class to be public, and InvoiceSweepFunction takes this on its
/// constructor.
/// </summary>
public interface IInvoiceGenerationStore
{
    Task<InvoiceClaim> ClaimAsync(string bookingType, Guid bookingId, CancellationToken cancellationToken);

    /// <param name="issuedBy">
    /// The provider's business name, for the parent's "invoice ready" push. Passed
    /// down rather than read in SQL because it lives in Cosmos, and passed at all
    /// so the notification names the same issuer the PDF prints.
    /// </param>
    Task CompleteAsync(Guid invoiceId, string invoiceUrl, string? issuedBy, CancellationToken cancellationToken);

    Task FailAsync(Guid invoiceId, string error, CancellationToken cancellationToken);

    Task<IReadOnlyList<InvoiceGenerationRequest>> ListUnrenderedAsync(
        int batchSize, CancellationToken cancellationToken);
}
