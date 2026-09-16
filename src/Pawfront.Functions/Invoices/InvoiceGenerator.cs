using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pawfront.Application.Billing;
using Pawfront.Application.Configuration;
using Pawfront.Application.Providers;
using Pawfront.Application.Storage;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace Pawfront.Functions.Invoices;

/// <summary>
/// Renders a paid booking's invoices and files them in blob storage.
///
/// The order is deliberate and matters: <b>claim → render → upload → record</b>.
/// The URL is written to SQL only once the PDF is durable in blob storage, so a
/// row can never advertise a file that is not there. The reverse ordering would
/// produce exactly that, and the download endpoint would 404 on an invoice the
/// database says exists.
///
/// Failures are recorded per invoice rather than thrown, so one bad document does
/// not cost the other — a provider's fee invoice failing must not deprive the
/// parent of their receipt.
/// </summary>
public sealed class InvoiceGenerator(
    IInvoiceGenerationStore store,
    IPawfrontBlobStorage blobStorage,
    IProviderSummaryReader providerSummaryReader,
    IOptions<InvoiceOptions> invoiceOptions,
    IOptions<PawfrontFeeOptions> feeOptions,
    ILogger<InvoiceGenerator> logger)
{
    private readonly InvoiceOptions options = invoiceOptions.Value;

    public async Task<InvoiceGenerationOutcome> GenerateAsync(
        InvoiceGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var claim = await store.ClaimAsync(request.BookingType, request.BookingId, cancellationToken);

        if (claim.Invoices.Count == 0)
        {
            // Already rendered, or another renderer holds the lease. Either way
            // this message has nothing to do — dequeue it rather than retrying.
            logger.LogInformation(
                "No unrendered invoices for {BookingType} booking {BookingId}; nothing to do.",
                request.BookingType, request.BookingId);
            return InvoiceGenerationOutcome.Empty;
        }

        if (claim.Facts is null)
        {
            // Rows exist but the booking does not — a data problem no retry fixes,
            // so fail them out to their attempt cap rather than spinning.
            foreach (var invoice in claim.Invoices)
            {
                await store.FailAsync(
                    invoice.InvoiceId,
                    $"Booking {request.BookingId} ({request.BookingType}) was not found.",
                    cancellationToken);
            }

            logger.LogError(
                "Invoices exist for {BookingType} booking {BookingId} but the booking row does not.",
                request.BookingType, request.BookingId);
            return new InvoiceGenerationOutcome(0, claim.Invoices.Count);
        }

        var facts = claim.Facts;

        // The provider's BUSINESS identity lives in Cosmos, not SQL. Best-effort:
        // a provider still mid-onboarding has no offering document, and one whose
        // account delete removed their listing has none either — the invoice must
        // still render, falling back to their personal name.
        var providerIdentity = await ResolveProviderIdentityAsync(facts, cancellationToken);

        var generated = 0;
        var failed = 0;

        foreach (var invoice in claim.Invoices)
        {
            try
            {
                var document = BuildDocument(facts, invoice, providerIdentity);

                // Rendered into memory first: the upload needs a seekable stream,
                // and an invoice is a single page of vector content — well under a
                // megabyte, so streaming to a temp file would buy nothing.
                using var buffer = new MemoryStream();
                document.GeneratePdf(buffer);
                buffer.Position = 0;

                var url = await blobStorage.UploadAsync(
                    BlobUploadKind.Invoice,
                    // The BOOKING is the folder, so a job's two invoices sit side
                    // by side under one prefix.
                    ownerId: facts.BookingId,
                    fileName: $"{invoice.InvoiceNumber}.pdf",
                    content: buffer,
                    contentType: "application/pdf",
                    cancellationToken);

                // Only now is the invoice downloadable — which is also why the
                // parent's "invoice ready" push is enqueued inside this call and
                // not back at mark-paid, where the PDF did not exist yet. The
                // issuer name is the one the document itself prints.
                await store.CompleteAsync(
                    invoice.InvoiceId, url, ResolveIssuerName(facts, providerIdentity), cancellationToken);
                generated++;

                logger.LogInformation(
                    "Generated {Recipient} invoice {InvoiceNumber} for {BookingType} booking {BookingId}.",
                    invoice.Recipient, invoice.InvoiceNumber, facts.BookingType, facts.BookingId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;

                // Recorded, not rethrown: the other invoice for this booking must
                // still get its chance, and the row's own backoff is what governs
                // the retry.
                logger.LogError(
                    exception,
                    "Failed to generate {Recipient} invoice {InvoiceNumber} for {BookingType} booking {BookingId} " +
                    "(attempt {Attempt}).",
                    invoice.Recipient, invoice.InvoiceNumber, facts.BookingType, facts.BookingId,
                    invoice.AttemptCount);

                await store.FailAsync(invoice.InvoiceId, exception.Message, CancellationToken.None);
            }
        }

        return new InvoiceGenerationOutcome(generated, failed);
    }

    private IDocument BuildDocument(
        InvoiceBookingFacts facts,
        ClaimedInvoice invoice,
        InvoiceProviderIdentity providerIdentity)
    {
        if (string.Equals(invoice.Recipient, InvoiceRecipients.PetParent, StringComparison.Ordinal))
        {
            return new ParentInvoiceDocument(facts, invoice, providerIdentity);
        }

        if (string.Equals(invoice.Recipient, InvoiceRecipients.Provider, StringComparison.Ordinal))
        {
            return new ProviderInvoiceDocument(
                facts,
                invoice,
                providerIdentity,
                options.Littersoft,
                // The SAME percentage the booking detail and the earnings figures
                // use, so an invoice can never quote a different commission from
                // the app that produced it.
                feeOptions.Value.PawfrontFeePercentage,
                options.LaunchDiscountPercentage,
                options.MwstPercentage);
        }

        // The CHECK constraint admits only the two above, so this is a schema
        // change nobody finished rather than bad input.
        throw new InvalidOperationException($"Unsupported invoice recipient '{invoice.Recipient}'.");
    }

    /// <summary>
    /// Who the invoice says issued it: the provider's business name, falling back
    /// to their own name for a freelancer or a provider with no offering document.
    /// MUST match what the two documents render, or the push and the PDF it points
    /// at would name different issuers.
    /// </summary>
    private static string? ResolveIssuerName(
        InvoiceBookingFacts facts,
        InvoiceProviderIdentity providerIdentity)
    {
        if (!string.IsNullOrWhiteSpace(providerIdentity.BusinessName))
        {
            return providerIdentity.BusinessName;
        }

        var personName = $"{facts.ProviderFirstName} {facts.ProviderLastName}".Trim();
        // Null rather than empty: the renderer's "your provider" fallback then
        // carries the sentence, instead of it reading "Issued by  ·".
        return string.IsNullOrWhiteSpace(personName) ? null : personName;
    }

    private async Task<InvoiceProviderIdentity> ResolveProviderIdentityAsync(
        InvoiceBookingFacts facts,
        CancellationToken cancellationToken)
    {
        try
        {
            var summary = await providerSummaryReader.GetAsync(
                facts.ProviderId, facts.ServiceCategory, cancellationToken);

            return summary is null
                ? InvoiceProviderIdentity.Unknown
                : new InvoiceProviderIdentity(
                    summary.DisplayName, summary.Address, summary.City, summary.Zip);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An invoice with a slightly thinner issuer block beats no invoice.
            logger.LogWarning(
                exception,
                "Could not read the Cosmos offering document for provider {ProviderId} in category " +
                "{ServiceCategory}; the invoice will fall back to their personal name.",
                facts.ProviderId, facts.ServiceCategory);
            return InvoiceProviderIdentity.Unknown;
        }
    }
}

/// <param name="Generated">Invoices rendered and filed on this run.</param>
/// <param name="Failed">Invoices that errored and will be retried under backoff.</param>
public readonly record struct InvoiceGenerationOutcome(int Generated, int Failed)
{
    public static readonly InvoiceGenerationOutcome Empty = new(0, 0);

    public bool DidWork => Generated > 0 || Failed > 0;
}
