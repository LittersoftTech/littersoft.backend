namespace Pawfront.Application.Billing;

/// <summary>
/// One invoice row, as the download path sees it. Deliberately does NOT carry the
/// PDF's contents — the endpoints stream the blob, and a row that has not been
/// rendered yet has no bytes to carry.
/// </summary>
/// <param name="Amount">
/// The gross the parent paid, frozen from the ledger at PAID rather than
/// re-derived. Both documents quote it, and a re-render must show what was
/// actually paid even if the fee percentage or the offering has changed since.
/// </param>
/// <param name="PawfrontFee">
/// The platform commission carved OUT of <paramref name="Amount"/> (provider net =
/// Amount - PawfrontFee). It is not added on top: the parent pays Amount either
/// way, which is why the parent invoice's subtotal and total are the same figure.
/// </param>
public sealed record BookingInvoice(
    Guid InvoiceId,
    string InvoiceNumber,
    string BookingType,
    Guid BookingId,
    string Recipient,
    Guid ProviderId,
    Guid PetParentId,
    decimal Amount,
    decimal PawfrontFee,
    string Status,
    string? InvoiceUrl,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset? GeneratedAtUtc)
{
    /// <summary>
    /// True once the PDF exists and can be streamed. The CHECK constraint
    /// guarantees a 'Generated' row has a URL, so this is belt and braces for the
    /// in-memory store.
    /// </summary>
    public bool IsDownloadable =>
        string.Equals(Status, InvoiceStatuses.Generated, StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(InvoiceUrl);
}

/// <summary>
/// Identifies a booking whose invoices need rendering. This is the queue message's
/// payload and the sweep's unit of work alike — one message per BOOKING, not per
/// invoice, because the two documents describe the same job from two sides and
/// their figures must agree, which is easiest to guarantee when one call assembles
/// them from one read.
/// </summary>
public sealed record InvoiceGenerationRequest(string BookingType, Guid BookingId);
