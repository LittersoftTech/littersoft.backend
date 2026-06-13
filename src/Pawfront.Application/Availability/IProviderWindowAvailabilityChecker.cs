namespace Pawfront.Application.Availability;

/// <summary>
/// Parent-facing discovery helper: answers "could this provider take a NEW
/// booking somewhere inside the requested window on that date?". Used by
/// the <c>GET /providers</c> list filter — a provider only appears in
/// date/time-filtered results when at least one of its active services has
/// a free-capacity slot satisfying the window.
/// </summary>
public interface IProviderWindowAvailabilityChecker
{
    /// <summary>
    /// True when any active service of the provider has a bookable slot for
    /// the window. Window semantics differ per duration rule:
    /// fixed-duration services (TrainingSession / VetAppointment / a grooming
    /// menu item) match when a free slot of that duration fits anywhere
    /// inside [start, end]; minimum-duration services (DayCare / NightStay)
    /// match when the FULL [start, end] window itself is bookable and is at
    /// least the offering's minimum length.
    /// </summary>
    Task<bool> HasBookableWindowAsync(
        Guid providerId,
        DateOnly date,
        TimeOnly startTime,
        TimeOnly endTime,
        CancellationToken cancellationToken);
}
