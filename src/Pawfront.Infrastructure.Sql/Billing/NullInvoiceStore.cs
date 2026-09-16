using Pawfront.Application.Billing;

namespace Pawfront.Infrastructure.Sql.Billing;

/// <summary>
/// The in-memory dev fallback: no invoice is ever found, so every download
/// answers 404.
///
/// A zeros-style Null store rather than a functional in-memory one — unlike
/// reviews or blocks, which ARE functional in-memory because they are write flows
/// a developer needs to exercise. Nothing in the API hosts writes an invoice: they
/// are raised by SQL inside the mark-paid transaction and rendered by an Azure
/// Function against blob storage. With neither of those present there is nothing
/// to serve, and pretending otherwise would mean fabricating a PDF.
/// </summary>
internal sealed class NullInvoiceStore : IInvoiceStore
{
    public Task<BookingInvoice?> GetAsync(InvoiceDownloadQuery query, CancellationToken cancellationToken)
        => Task.FromResult<BookingInvoice?>(null);
}
