namespace Pawfront.Application.Providers;

/// <summary>
/// Parent-facing per-service booking search. Each method narrows discovery
/// to one bookable service type, applies the category's availability
/// semantics against real slots (working hours, closures, confirmed
/// bookings vs capacity), and decorates the survivors with completed-booking
/// counts and charges.
/// </summary>
public interface IProviderSearchService
{
    Task<IReadOnlyList<ProviderSearchResult>> SearchDayCareAsync(
        DayCareProviderSearchCriteria criteria,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderSearchResult>> SearchNightStayAsync(
        NightStayProviderSearchCriteria criteria,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderSearchResult>> SearchGroomingAsync(
        GroomingProviderSearchCriteria criteria,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ProviderSearchResult>> SearchVetAsync(
        VetProviderSearchCriteria criteria,
        CancellationToken cancellationToken);
}
