using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Billing;
using Pawfront.Functions.Invoices;

namespace Pawfront.Functions.Functions;

/// <summary>
/// Re-queues invoice work the queue lost.
///
/// This closes the one gap in the design. <c>Booking.MarkBookingPaid</c> writes
/// the invoice rows as 'Pending' inside the payment transaction, but the queue
/// message that renders them is sent from C# afterwards — SQL cannot enqueue a
/// Storage Queue message, so a crash in that window loses it. Nothing else would
/// ever notice, because the booking is legitimately PAID and its ledger row is
/// written; only the 'Pending' rows say the work is outstanding.
///
/// It also recovers a renderer that died holding a lease, and a message that
/// failed transiently and backed off past its queue redeliveries.
///
/// Every five minutes, matching <see cref="BookingSweepFunction"/>: this is a
/// backstop, not a delivery path, and the steady state is that it finds nothing.
/// A grace period keeps it out of the fast path's way — a booking paid seconds ago
/// is almost certainly mid-render.
///
/// Like the other timer triggers here it relies on the Functions host serialising
/// a timer's invocations across instances, which holds only while this stays the
/// ONE deployed Function App for these triggers. A duplicate would be harmless
/// anyway: the claim is lease-based, so a second renderer finds nothing to take.
/// </summary>
public sealed class InvoiceSweepFunction(
    IInvoiceGenerationStore store,
    IInvoiceQueuePublisher publisher,
    ILoggerFactory loggerFactory)
{
    /// <summary>
    /// Bookings re-queued per tick. Generous — the steady state is zero, and a
    /// backlog this large means something was broken for a while and should drain
    /// promptly.
    /// </summary>
    private const int BatchSize = 100;

    private readonly ILogger _logger = loggerFactory.CreateLogger<InvoiceSweepFunction>();

    [Function("InvoiceSweepFunction")]
    public async Task Run(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timer,
        CancellationToken cancellationToken)
    {
        try
        {
            var pending = await store.ListUnrenderedAsync(BatchSize, cancellationToken);
            if (pending.Count == 0)
            {
                return;
            }

            foreach (var request in pending)
            {
                // The publisher swallows its own failures, so a queue outage costs
                // this tick and nothing more — the rows stay 'Pending' and the next
                // tick tries again.
                await publisher.PublishAsync(request, cancellationToken);
            }

            _logger.LogWarning(
                "Invoice sweep re-queued {Count} booking(s) whose invoices were still unrendered. " +
                "A non-zero count here means queue messages are being lost or renders are failing.",
                pending.Count);
        }
        catch (Exception exception)
        {
            // The rows are durable and this runs again in five minutes, so a
            // transient outage self-heals without failing the host.
            _logger.LogError(exception, "Invoice sweep failed; will retry at the next scheduled tick.");
        }
    }
}
