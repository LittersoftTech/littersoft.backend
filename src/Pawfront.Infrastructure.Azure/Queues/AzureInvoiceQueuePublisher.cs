using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pawfront.Application.Billing;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Azure.Queues;

/// <summary>
/// Puts an invoice-generation request on the <c>invoice-generation</c> queue.
///
/// The message is sent AFTER <c>Booking.MarkBookingPaid</c> has committed — SQL
/// cannot enqueue an Azure Storage Queue message, so there is no way to make the
/// two atomic. That is precisely why the invoice rows are written inside that
/// transaction: this call is the fast path, and the sweep is the guarantee.
///
/// It therefore NEVER THROWS. The payment has already happened; failing here
/// would report a completed payment as failed, and the retry would be refused
/// with "already paid". A lost message costs latency (until the next sweep), not
/// an invoice.
/// </summary>
internal sealed class AzureInvoiceQueuePublisher(
    IPawfrontSecretProvider secretProvider,
    IOptions<InvoiceQueueOptions> queueOptions,
    ILogger<AzureInvoiceQueuePublisher> logger) : IInvoiceQueuePublisher
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly InvoiceQueueOptions options = queueOptions.Value;
    private readonly SemaphoreSlim semaphore = new(1, 1);
    private QueueClient? queueClient;

    public async Task PublishAsync(InvoiceGenerationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var client = await GetQueueClientAsync(cancellationToken);
            var payload = JsonSerializer.Serialize(request, SerializerOptions);
            await client.SendMessageAsync(payload, cancellationToken);

            logger.LogInformation(
                "Queued invoice generation for {BookingType} booking {BookingId}.",
                request.BookingType, request.BookingId);
        }
        catch (Exception exception)
        {
            // Deliberately swallowed — see the class remarks. Logged at Warning
            // rather than Error because the outcome is recoverable by design and
            // an Error here would page somebody for something the sweep fixes.
            logger.LogWarning(
                exception,
                "Could not queue invoice generation for {BookingType} booking {BookingId}. " +
                "Its invoice rows are already committed as 'Pending', so the sweep will pick them up.",
                request.BookingType, request.BookingId);
        }
    }

    private async Task<QueueClient> GetQueueClientAsync(CancellationToken cancellationToken)
    {
        if (queueClient is not null)
        {
            return queueClient;
        }

        await semaphore.WaitAsync(cancellationToken);
        try
        {
            if (queueClient is not null)
            {
                return queueClient;
            }

            if (string.IsNullOrWhiteSpace(options.QueueName))
            {
                throw new InvalidOperationException($"{InvoiceQueueOptions.SectionName}:QueueName is required.");
            }

            var connectionString = await ResolveConnectionStringAsync(cancellationToken);

            // Base64 because the Functions QueueTrigger binding expects it by
            // default; sending raw text produces a message the trigger cannot
            // decode, and it fails at dequeue rather than here.
            var client = new QueueClient(
                connectionString,
                options.QueueName,
                new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 });

            await client.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

            queueClient = client;
            return queueClient;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<string> ResolveConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return options.ConnectionString;
        }

        if (!string.IsNullOrWhiteSpace(options.ConnectionStringSecretName))
        {
            return await secretProvider.GetSecretValueAsync(options.ConnectionStringSecretName, cancellationToken);
        }

        // The intended path: the queue is on the same storage account as the
        // blobs, so the account key already configured for them covers it.
        return await secretProvider.GetBlobStorageKeyAsync(cancellationToken);
    }
}
