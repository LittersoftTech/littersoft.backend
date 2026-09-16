using Pawfront.Application.Storage;

namespace Pawfront.Application.Billing;

/// <summary>
/// Resolves an invoice row and streams its PDF. Both hosts share it; the only
/// difference between them is which party they scope by, which the query carries.
/// </summary>
internal sealed class InvoiceService(
    IInvoiceStore store,
    IPawfrontBlobStorage blobStorage) : IInvoiceService
{
    public async Task<InvoiceDownload> GetDownloadAsync(
        InvoiceDownloadQuery query,
        CancellationToken cancellationToken)
    {
        var invoice = await store.GetAsync(query, cancellationToken)
            ?? throw new InvoiceNotFoundException(query.BookingType, query.BookingId);

        // Raised but not rendered. A distinct failure from "no such invoice",
        // because this one is worth retrying in a moment and that one never is.
        if (!invoice.IsDownloadable)
        {
            throw new InvoiceNotReadyException(invoice.InvoiceId, invoice.Status);
        }

        var download = await blobStorage.DownloadAsync(invoice.InvoiceUrl!, cancellationToken)
            ?? throw new InvoiceFileMissingException(invoice.InvoiceId, invoice.InvoiceUrl!);

        return new InvoiceDownload(
            invoice,
            download.Content,
            // The stored content type wins when present; the fallback matters only
            // for a blob uploaded before the content type was being set.
            string.IsNullOrWhiteSpace(download.ContentType) ? "application/pdf" : download.ContentType,
            $"{invoice.InvoiceNumber}.pdf");
    }
}
