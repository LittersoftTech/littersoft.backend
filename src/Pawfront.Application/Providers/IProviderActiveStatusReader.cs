namespace Pawfront.Application.Providers;

/// <summary>
/// Narrow SQL reader for the provider master Active/Inactive switch
/// (<c>Provider.Providers.IsActive</c>) and the permanent delete flag
/// (<c>IsDeleted</c>). Kept separate from <c>IProviderOnboardingService</c>
/// (which owns the write side) — this is a read-only batch membership test.
///
/// It exists because provider DISCOVERY is Cosmos-only: the offering document
/// carries no notion of the switch, which lives in SQL. Without this the parent
/// apps listed providers who had explicitly turned themselves off, and every
/// booking attempt against them failed with 409 ProviderInactive.
/// </summary>
public interface IProviderActiveStatusReader
{
    /// <summary>
    /// Returns the subset of <paramref name="providerIds"/> that are currently
    /// bookable — <c>IsActive = 1</c> and <c>IsDeleted = 0</c>.
    ///
    /// Fails closed: an id with no <c>Provider.Providers</c> row at all is absent
    /// from the result. Registration can't produce that state (the service
    /// registration sproc THROWs 51010 without a profile row), so an id that
    /// can't be confirmed active is a data inconsistency, not a provider to show.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetActiveProviderIdsAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken);
}
