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

    /// <summary>Both parties agreed the service was delivered.</summary>
    public const string Completed = "COMPLETED";

    /// <summary>A schedule change was requested and is awaiting agreement.</summary>
    public const string ApprovalNeeded = "APPROVAL_NEEDED";

    /// <summary>Provider cancelled the booking.</summary>
    public const string ProviderCancelled = "PROVIDER_CANCELLED";

    /// <summary>Parent cancelled the booking.</summary>
    public const string ParentCancelled = "PARENT_CANCELLED";

    /// <summary>Every valid status value.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Created, Confirmed, Completed, ApprovalNeeded, ProviderCancelled, ParentCancelled
    };

    /// <summary>
    /// Statuses a provider may set via the status-change API.
    /// </summary>
    public static readonly IReadOnlySet<string> ProviderSettable = new HashSet<string>(StringComparer.Ordinal)
    {
        Confirmed, Completed, ApprovalNeeded, ProviderCancelled
    };

    /// <summary>
    /// Statuses a parent may set via the status-change API.
    /// </summary>
    public static readonly IReadOnlySet<string> ParentSettable = new HashSet<string>(StringComparer.Ordinal)
    {
        ApprovalNeeded, Completed, ParentCancelled
    };

    /// <summary>
    /// Terminal statuses — once a booking reaches one, no further status change
    /// is allowed.
    /// </summary>
    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Completed, ProviderCancelled, ParentCancelled
    };

    /// <summary>
    /// Statuses that free up capacity. Every other (non-cancelled) status still
    /// holds the booking's slot — this is the SQL "active booking" predicate.
    /// </summary>
    public static readonly IReadOnlySet<string> Cancelled = new HashSet<string>(StringComparer.Ordinal)
    {
        ProviderCancelled, ParentCancelled
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
