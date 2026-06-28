namespace Pawfront.Application.Bookings;

/// <summary>
/// Canonical service-booking lifecycle statuses and the rules around them.
/// Stored verbatim (uppercase) in <c>Booking.Bookings.Status</c> and the audit
/// table. The stored procedure <c>Booking.UpdateBookingStatus</c> is the
/// authoritative gate for role + transition rules; these constants/sets exist
/// so the Application layer can give a clean 400 for an unknown status before
/// hitting SQL, and so callers can reason about the lifecycle.
/// </summary>
public static class BookingStatuses
{
    /// <summary>Initial state of a parent-created booking.</summary>
    public const string Created = "CREATED";

    /// <summary>Provider accepted the booking.</summary>
    public const string Confirmed = "CONFIRMED";

    /// <summary>Provider rejected the booking (terminal, frees capacity).</summary>
    public const string ProviderDeclined = "PROVIDER_DECLINED";

    /// <summary>Provider started the job (after entering the parent's start-OTP).</summary>
    public const string JobStarted = "JOB_STARTED";

    /// <summary>Provider uploaded evidence and the job is done.</summary>
    public const string Completed = "COMPLETED";

    /// <summary>Deprecated: superseded by the modification flow. Kept for legacy rows.</summary>
    public const string ApprovalNeeded = "APPROVAL_NEEDED";

    /// <summary>Parent proposed a schedule change, awaiting the provider's response.</summary>
    public const string ModificationRequestByParent = "MODIFICATION_REQUEST_BY_PARENT";

    /// <summary>Provider proposed a schedule change, awaiting the parent's response.</summary>
    public const string ModificationRequestByProvider = "MODIFICATION_REQUEST_BY_PROVIDER";

    /// <summary>Provider accepted the parent's modification (new details applied).</summary>
    public const string ProviderAcceptedModification = "PROVIDER_ACCEPTED_MODIFICATION";

    /// <summary>Provider declined the parent's modification (old details kept).</summary>
    public const string ProviderDeclinedModification = "PROVIDER_DECLINED_MODIFICATION";

    /// <summary>Parent accepted the provider's modification (new details applied).</summary>
    public const string ParentAcceptedModification = "PARENT_ACCEPTED_MODIFICATION";

    /// <summary>Parent declined the provider's modification (old details kept).</summary>
    public const string ParentDeclinedModification = "PARENT_DECLINED_MODIFICATION";

    /// <summary>Provider cancelled the booking.</summary>
    public const string ProviderCancelled = "PROVIDER_CANCELLED";

    /// <summary>Parent cancelled the booking.</summary>
    public const string ParentCancelled = "PARENT_CANCELLED";

    /// <summary>Every valid status value (APPROVAL_NEEDED kept for legacy rows).</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Created, Confirmed, ProviderDeclined, JobStarted, Completed, ApprovalNeeded,
        ModificationRequestByParent, ModificationRequestByProvider,
        ProviderAcceptedModification, ProviderDeclinedModification,
        ParentAcceptedModification, ParentDeclinedModification,
        ProviderCancelled, ParentCancelled
    };

    /// <summary>
    /// "Live" resting states a job can be started, modified, or cancelled from —
    /// CONFIRMED plus the four post-modification resting states.
    /// </summary>
    public static readonly IReadOnlySet<string> ConfirmedEquivalent = new HashSet<string>(StringComparer.Ordinal)
    {
        Confirmed, ProviderAcceptedModification, ParentAcceptedModification,
        ProviderDeclinedModification, ParentDeclinedModification
    };

    /// <summary>
    /// States in which a proposal is sitting in the staging area awaiting the
    /// counterparty's response (so a pending-modification read is worthwhile).
    /// </summary>
    public static readonly IReadOnlySet<string> ModificationRequested = new HashSet<string>(StringComparer.Ordinal)
    {
        ModificationRequestByParent, ModificationRequestByProvider
    };

    /// <summary>
    /// Statuses the provider may set via the simple status engine
    /// (accept / decline / complete / cancel). Start + modifications use their
    /// own dedicated paths.
    /// </summary>
    public static readonly IReadOnlySet<string> ProviderSettable = new HashSet<string>(StringComparer.Ordinal)
    {
        Confirmed, ProviderDeclined, Completed, ProviderCancelled
    };

    /// <summary>Statuses the parent may set via the simple status engine.</summary>
    public static readonly IReadOnlySet<string> ParentSettable = new HashSet<string>(StringComparer.Ordinal)
    {
        ParentCancelled
    };

    /// <summary>
    /// Terminal statuses — once a booking reaches one, no further status change
    /// is allowed.
    /// </summary>
    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Completed, ProviderDeclined, ProviderCancelled, ParentCancelled
    };

    /// <summary>
    /// Statuses that free up capacity. Every other status still holds the
    /// booking's slot — this is the SQL "active booking" predicate.
    /// </summary>
    public static readonly IReadOnlySet<string> Cancelled = new HashSet<string>(StringComparer.Ordinal)
    {
        ProviderCancelled, ParentCancelled, ProviderDeclined
    };

    /// <summary>
    /// Trims + validates an incoming status string. Throws
    /// <see cref="UnsupportedBookingStatusException"/> when it is not one of the
    /// six canonical values.
    /// </summary>
    public static string Normalize(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || !All.Contains(trimmed))
        {
            throw new UnsupportedBookingStatusException(value ?? string.Empty);
        }

        return trimmed;
    }
}
