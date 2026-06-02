namespace Pawfront.Application.ProviderOnboarding;

/// <summary>
/// Conflicting confirmed booking returned when the provider tries to flip their
/// master Active/Inactive switch to Inactive while future bookings still exist.
/// </summary>
public sealed record ActiveStatusConflictingBooking(
    Guid BookingId,
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    // Null for Source = 'Custom' (provider-added private job).
    Guid? PetParentId,
    string Source,
    string? CustomerName,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime);

/// <summary>
/// Result of <see cref="IProviderOnboardingService.SetActiveStatusAsync"/>.
/// Discriminated: either the flag was applied, or there are existing future
/// confirmed bookings blocking deactivation — caller surfaces the list so the
/// provider can move or cancel them before retrying.
/// </summary>
public abstract record SetActiveStatusOutcome
{
    private SetActiveStatusOutcome() { }

    public sealed record Updated(Guid ProviderId, bool IsActive, DateTimeOffset UpdatedAtUtc)
        : SetActiveStatusOutcome;

    public sealed record BookingsExist(IReadOnlyList<ActiveStatusConflictingBooking> Bookings)
        : SetActiveStatusOutcome;
}
