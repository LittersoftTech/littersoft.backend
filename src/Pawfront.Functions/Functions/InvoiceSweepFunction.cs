using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Pawfront.Functions.Invoices;

namespace Pawfront.Functions.Functions;

/// <summary>
/// Recovers due invoice work directly from SQL every 30 seconds, independently
/// of queue delivery. SQL leases and retry backoff coordinate with queue workers.
/// </summary>
public sealed class InvoiceSweepFunction(
    IInvoiceGenerationStore store,
    InvoiceGenerator generator,
    ILoggerFactory loggerFactory)
{
    /// <summary>
    /// Bookings processed per tick. Generous — the steady state is zero, and a
    /// backlog this large means something was broken for a while and should drain
    /// promptly.
    /// </summary>
    private const int BatchSize = 100;

    private readonly ILogger _logger = loggerFactory.CreateLogger<InvoiceSweepFunction>();

    [Function("InvoiceSweepFunction")]
    public async Task Run(
        [TimerTrigger("*/30 * * * * *")] TimerInfo timer,
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
                try
                {
                    // Use the durable SQL work directly so recovery also works
                    // when queue publishing or the queue trigger is unavailable.
                    await generator.GenerateAsync(request, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogError(exception,
                        "Invoice recovery failed for {BookingType} booking {BookingId}.",
                        request.BookingType, request.BookingId);
                }
            }

            _logger.LogWarning(
                "Invoice sweep processed {Count} booking(s) whose invoices were still unrendered. " +
                "A non-zero count here means queue messages are being lost or renders are failing.",
                pending.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The rows are durable and this runs again in 30 seconds, so a
            // transient outage self-heals without failing the host.
            _logger.LogError(exception, "Invoice sweep failed; will retry at the next scheduled tick.");
        }
    }
}
