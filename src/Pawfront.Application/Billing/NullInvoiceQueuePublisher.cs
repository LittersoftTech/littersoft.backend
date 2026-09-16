using Microsoft.Extensions.Logging;

namespace Pawfront.Application.Billing;

/// <summary>
/// The dev fallback for <see cref="IInvoiceQueuePublisher"/>: logs the request and
/// drops it. Registered by TryAdd, so a host that wires the real Azure publisher
/// BEFORE <c>AddPawfrontApplication()</c> keeps it.
///
/// Accepting rather than throwing is deliberate, and matches
/// <c>NullBookingLocationService</c>: a developer with no storage account must
/// still be able to mark a booking paid. The invoice rows are written by SQL
/// either way, so nothing is lost that a real deployment's sweep would not
/// recover — the invoices simply sit 'Pending' until something renders them.
/// </summary>
internal sealed class NullInvoiceQueuePublisher(ILogger<NullInvoiceQueuePublisher> logger)
    : IInvoiceQueuePublisher
{
    public Task PublishAsync(InvoiceGenerationRequest request, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "No invoice queue is configured; dropping the generation request for {BookingType} booking {BookingId}. " +
            "Its invoice rows remain 'Pending' and would be picked up by the sweep in a deployed environment.",
            request.BookingType, request.BookingId);

        return Task.CompletedTask;
    }
}
