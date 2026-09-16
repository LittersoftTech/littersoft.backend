namespace Pawfront.Application.Billing;

/// <summary>
/// No invoice for this booking, or it is not the caller's — one exception for both,
/// on purpose. An invoice id (and a booking id) must not be probeable, so "unknown"
/// and "not yours" answer identically. Same posture as
/// <c>DeviceTokenNotFoundException</c> (THROW 51005) and support tickets.
/// Maps to <b>404 InvoiceNotFound</b>.
/// </summary>
public sealed class InvoiceNotFoundException(string bookingType, Guid bookingId)
    : Exception($"No invoice was found for {bookingType} booking '{bookingId}'.")
{
    public string BookingType { get; } = bookingType;
    public Guid BookingId { get; } = bookingId;
}

/// <summary>
/// The invoice has been raised but its PDF is not rendered yet — normally a few
/// seconds after payment, longer if the queue message was lost and the sweep has
/// to recover it.
///
/// Deliberately NOT a 404: the client has just been told the booking is paid, and
/// "no such invoice" would be wrong and unretryable. Maps to
/// <b>409 InvoiceNotReady</b>, which says "ask again shortly".
/// </summary>
public sealed class InvoiceNotReadyException(Guid invoiceId, string status)
    : Exception($"Invoice '{invoiceId}' is not ready to download yet (status '{status}').")
{
    public Guid InvoiceId { get; } = invoiceId;
    public string Status { get; } = status;
}

/// <summary>
/// The row says 'Generated' but the blob is gone. A CHECK constraint guarantees a
/// generated invoice carries a URL, so this means the file was deleted underneath
/// us rather than never written. Maps to <b>404 InvoiceFileNotFound</b> — kept
/// distinct from <see cref="InvoiceNotFoundException"/> so this shows up as the
/// storage problem it is instead of hiding as a routine miss.
/// </summary>
public sealed class InvoiceFileMissingException(Guid invoiceId, string invoiceUrl)
    : Exception($"Invoice '{invoiceId}' is recorded at '{invoiceUrl}' but the file is missing.")
{
    public Guid InvoiceId { get; } = invoiceId;
    public string InvoiceUrl { get; } = invoiceUrl;
}
