using Pawfront.Application.Bookings;

namespace Pawfront.Application.Billing;

/// <summary>
/// The download side of invoicing, shared by both API hosts. Rendering lives
/// entirely in <c>Pawfront.Functions</c>; nothing here produces a PDF.
/// </summary>
public interface IInvoiceService
{
    /// <summary>
    /// The caller's own invoice for a booking, ready to stream.
    ///
    /// The recipient is PINNED BY THE HOST, never taken from the client: the
    /// provider host always asks for the provider's fee invoice and the parent
    /// host always for the parent's service invoice. The two documents name
    /// different parties and carry different money, so letting a client choose
    /// would be the whole vulnerability.
    /// </summary>
    /// <exception cref="InvoiceNotFoundException">
    /// No invoice for this booking, or it is not the caller's. The two are
    /// deliberately indistinguishable so an id cannot be probed.
    /// </exception>
    /// <exception cref="InvoiceNotReadyException">
    /// Raised but still rendering. Distinct from not-found because a client that
    /// has just tapped Download needs to know whether to retry.
    /// </exception>
    Task<InvoiceDownload> GetDownloadAsync(InvoiceDownloadQuery query, CancellationToken cancellationToken);
}

/// <param name="BookingType">One of <see cref="BookingTypes"/>.</param>
/// <param name="ProviderId">Set by the provider host; null on the parent host.</param>
/// <param name="PetParentId">Set by the parent host; null on the provider host.</param>
public sealed record InvoiceDownloadQuery(
    string BookingType,
    Guid BookingId,
    string Recipient,
    Guid? ProviderId,
    Guid? PetParentId);

/// <summary>
/// A resolved invoice plus its bytes. <paramref name="FileName"/> is the invoice
/// number so a downloaded file is self-identifying on disk rather than being one
/// of a dozen "invoice.pdf".
/// </summary>
public sealed record InvoiceDownload(
    BookingInvoice Invoice,
    Stream Content,
    string ContentType,
    string FileName);
