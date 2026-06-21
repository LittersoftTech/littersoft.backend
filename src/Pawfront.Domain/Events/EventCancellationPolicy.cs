namespace Pawfront.Domain.Events;

/// <summary>
/// Refund policy an event creator sets at creation time. Governs whether a
/// ticket buyer is entitled to a full refund based on how far ahead of the
/// event they cancel. (The cancellation/refund execution flow itself is not
/// built yet — this captures the policy the creator advertises.)
/// </summary>
public enum EventCancellationPolicy
{
    /// <summary>Full refund if cancelled at least 4 hours before the event.</summary>
    FullRefundUpTo4Hours = 0,

    /// <summary>Full refund if cancelled at least 2 hours before the event.</summary>
    FullRefundUpTo2Hours = 1,

    /// <summary>No refund under any circumstances.</summary>
    NoRefund = 2
}
