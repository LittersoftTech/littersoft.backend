using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Billing;
using Pawfront.Functions.Invoices;

namespace Pawfront.Functions.Functions;

/// <summary>
/// Renders a paid booking's invoices, triggered by a message on the
/// <c>invoice-generation</c> queue.
///
/// Queue-triggered rather than timer-triggered because the work is event-shaped:
/// a booking reaching PAID is the trigger, and a parent tapping Download moments
/// later should find their invoice there. A timer would add up to its own interval
/// of latency for no benefit.
///
/// The message is enqueued by the API hosts from <c>BookingService.MarkPaidAsync</c>
/// AFTER the payment transaction commits — SQL cannot enqueue a Storage Queue
/// message, so the two cannot be made atomic. The safety net is that the same
/// transaction wrote the invoice rows as 'Pending', which
/// <see cref="InvoiceSweepFunction"/> recovers.
/// </summary>
public sealed class InvoiceGenerationFunction(
    InvoiceGenerator generator,
    ILoggerFactory loggerFactory)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger _logger = loggerFactory.CreateLogger<InvoiceGenerationFunction>();

    [Function("InvoiceGenerationFunction")]
    public async Task Run(
        // Connection names an APPLICATION SETTING, not the value — and it is
        // deliberately NOT AzureWebJobsStorage: the queue lives on the same
        // account as the invoice blobs (littersoftbs), which is not necessarily
        // the account the Functions runtime uses for its own bookkeeping.
        [QueueTrigger("%InvoiceQueue:QueueName%", Connection = "InvoiceStorage")]
        string message,
        CancellationToken cancellationToken)
    {
        InvoiceGenerationRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<InvoiceGenerationRequest>(message, SerializerOptions);
        }
        catch (JsonException exception)
        {
            // Unparseable. Throwing would retry it to the poison queue five times
            // over; nothing about it will parse on the sixth. Swallow so it is
            // dequeued once, and log loudly — a malformed message means a producer
            // is broken, which is worth seeing.
            _logger.LogError(exception, "Discarding an unparseable invoice-generation message: {Message}", message);
            return;
        }

        if (request is null || request.BookingId == Guid.Empty || string.IsNullOrWhiteSpace(request.BookingType))
        {
            _logger.LogError("Discarding an invoice-generation message with no usable booking: {Message}", message);
            return;
        }

        // Anything past here IS worth retrying, so it is allowed to throw: the
        // queue's own redelivery is the first line of retry, the invoice row's
        // backoff the second, and the poison queue the backstop after five
        // attempts.
        var outcome = await generator.GenerateAsync(request, cancellationToken);

        if (!outcome.DidWork)
        {
            return;
        }

        _logger.LogInformation(
            "Invoice generation for {BookingType} booking {BookingId}: {Generated} generated, {Failed} failed.",
            request.BookingType, request.BookingId, outcome.Generated, outcome.Failed);
    }
}
