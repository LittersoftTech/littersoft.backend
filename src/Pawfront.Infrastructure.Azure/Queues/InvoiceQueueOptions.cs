namespace Pawfront.Infrastructure.Azure.Queues;

/// <summary>
/// The queue that carries invoice-generation requests to
/// <c>Pawfront.Functions</c>.
///
/// It lives on the SAME storage account as the blobs — the account key already in
/// <c>BlobStorage:ConnectionString</c> covers both the blob and queue endpoints —
/// so there is deliberately no second credential here. Set
/// <see cref="ConnectionStringSecretName"/> only if the queue ever moves to its
/// own account.
/// </summary>
public sealed class InvoiceQueueOptions
{
    public const string SectionName = "InvoiceQueue";

    /// <summary>
    /// Queue name. Must match the <c>QueueTrigger</c> in
    /// <c>InvoiceGenerationFunction</c> — they are two halves of one contract, and
    /// a mismatch fails silently (messages pile up, nothing renders, and the sweep
    /// keeps re-enqueuing into the void).
    /// </summary>
    public string QueueName { get; init; } = "invoice-generation";

    /// <summary>
    /// Optional override. Null means "use the blob storage connection string",
    /// which is the intended arrangement.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// Optional Key Vault secret name for the above, for a deployed environment
    /// that wants the queue on a separate account.
    /// </summary>
    public string? ConnectionStringSecretName { get; init; }
}
