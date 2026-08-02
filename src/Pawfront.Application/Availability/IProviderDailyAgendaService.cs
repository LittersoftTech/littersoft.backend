namespace Pawfront.Application.Availability;

/// <summary>
/// Builds a provider service's day as a timeline of booked and free blocks.
/// Sibling of <see cref="IProviderAvailabilitySlotService"/>: both read the same
/// working hours, closures and bookings, but the slot service answers "where does
/// a booking of THIS length fit?" while the agenda answers "what does the day look
/// like?" — no duration needed, so the parent can browse before choosing.
/// </summary>
public interface IProviderDailyAgendaService
{
    /// <param name="callerPetParentId">
    /// The signed-in parent, resolved from the JWT. Bookings owned by them keep
    /// their real status + job id; every other booking is masked to a bare
    /// <c>BOOKED</c> block. Null (no profile yet) masks everything.
    /// </param>
    /// <exception cref="SlotServiceInvalidException">
    /// The ServiceId is unknown, inactive, or not owned by this provider.
    /// </exception>
    /// <exception cref="ProviderOfferingNotConfiguredException">
    /// The provider registered the category but never configured the offering, so
    /// there is no capacity to measure against.
    /// </exception>
    Task<ProviderDailyAgendaResult> GetDailyAgendaAsync(
        Guid providerId,
        Guid serviceId,
        DateOnly date,
        Guid? callerPetParentId,
        CancellationToken cancellationToken);
}
