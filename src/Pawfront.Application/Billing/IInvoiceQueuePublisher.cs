namespace Pawfront.Application.Billing;

/// <summary>
/// Asks the renderer to produce a booking's invoices.
///
/// This exists as an abstraction rather than a direct QueueClient call because the
/// Application layer must not reference an Azure SDK, and because the in-memory
/// dev configuration needs a no-op that keeps the mark-paid flow usable without a
/// storage account.
///
/// EVERY IMPLEMENTATION MUST SWALLOW ITS FAILURES. The caller has already
/// committed the payment: the booking is PAID, the ledger row is written and the
/// parent has been sent their receipt. Throwing here would report a payment that
/// happened as failed — the same failure the chat send was rebuilt to avoid — and
/// the retry would then hit "already paid". The invoice rows written inside that
/// transaction are the safety net: whatever this call loses, the sweep recovers.
/// </summary>
public interface IInvoiceQueuePublisher
{
    Task PublishAsync(InvoiceGenerationRequest request, CancellationToken cancellationToken);
}
