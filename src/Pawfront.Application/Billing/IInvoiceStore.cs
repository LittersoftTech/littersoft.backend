namespace Pawfront.Application.Billing;

/// <summary>
/// The API hosts' narrow view of <c>Billing.Invoices</c> — a single scoped point
/// read. Raising an invoice is a stored procedure called from inside the mark-paid
/// transaction, and rendering belongs to <c>Pawfront.Functions</c>, so neither
/// appears here: these hosts only ever look one up to serve it.
/// </summary>
public interface IInvoiceStore
{
    /// <summary>
    /// The invoice for one booking and one recipient, scoped to the caller.
    ///
    /// The scoping is part of the SQL predicate rather than a check afterwards, so
    /// somebody else's invoice is indistinguishable from one that does not exist.
    /// Returns null for both.
    /// </summary>
    Task<BookingInvoice?> GetAsync(InvoiceDownloadQuery query, CancellationToken cancellationToken);
}
