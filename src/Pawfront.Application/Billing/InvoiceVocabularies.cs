namespace Pawfront.Application.Billing;

/// <summary>
/// Who an invoice is issued TO — which is also what says who issued it, since the
/// two documents a paid booking raises come from two different entities:
/// <list type="bullet">
/// <item><see cref="PetParent"/> — issued BY the provider FOR the service. Pawfront
/// only delivers it on the provider's behalf; the document is not Pawfront's, and
/// the copy on it says so.</item>
/// <item><see cref="Provider"/> — issued BY Littersoft GmbH FOR the Pawfront fee
/// earned on that one job. It exists because a cash job hands the provider the
/// whole amount, fee included, so the fee has to be billed back. When digital
/// payouts land, the same fee is netted off the payout instead and this half stops
/// being raised.</item>
/// </list>
/// Mirrors the <c>CK_Invoices_Recipient</c> CHECK. SQL owns every write, so these
/// constants exist to name the values for C# readers and for the endpoints, which
/// PIN the recipient by host rather than accepting it from a client.
/// </summary>
public static class InvoiceRecipients
{
    public const string PetParent = "PetParent";
    public const string Provider = "Provider";
}

/// <summary>
/// Where an invoice is in its lifecycle. Mirrors <c>CK_Invoices_Status</c>.
/// </summary>
public static class InvoiceStatuses
{
    /// <summary>
    /// Raised inside the mark-paid transaction, not yet rendered. This is the
    /// state that makes the feature durable: the queue message that triggers
    /// rendering is sent afterwards from C# and can be lost, but a Pending row
    /// cannot be, so the sweep can always recover the work.
    /// </summary>
    public const string Pending = "Pending";

    /// <summary>Claimed by a renderer; the lease is held in NextAttemptAtUtc.</summary>
    public const string Generating = "Generating";

    /// <summary>Rendered and uploaded — InvoiceUrl is populated and downloadable.</summary>
    public const string Generated = "Generated";

    /// <summary>
    /// Gave up after the attempt cap and stopped being swept. Needs a human: the
    /// usual cause is missing reference data that retrying will not conjure.
    /// </summary>
    public const string Failed = "Failed";
}

/// <summary>The bounds invoice rendering is built to.</summary>
public static class InvoiceLimits
{
    /// <summary>
    /// Render attempts before an invoice settles as <see cref="InvoiceStatuses.Failed"/>.
    /// Matches the notification outbox's own cap, and for the same reason: past
    /// this, a failure is a bug rather than a blip.
    /// </summary>
    public const int MaxAttempts = 5;

    /// <summary>
    /// How long a renderer's claim on an invoice is good for. A process that dies
    /// mid-render releases its rows automatically when the lease lapses, rather
    /// than stranding them in 'Generating' forever.
    /// </summary>
    public const int LeaseMinutes = 5;

    /// <summary>
    /// How long the sweep waits before treating an unrendered invoice as work the
    /// queue lost. A booking paid seconds ago is almost certainly mid-render, and
    /// re-enqueuing it would only collide on the claim.
    /// </summary>
    public const int SweepGraceMinutes = 5;
}
