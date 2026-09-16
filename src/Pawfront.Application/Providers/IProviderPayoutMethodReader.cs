namespace Pawfront.Application.Providers;

/// <summary>
/// Narrow batched SQL reader for the payment methods a provider accepts
/// (<c>Provider.ProviderPayoutMethods</c>), behind the parent-facing "Payments"
/// search filter.
/// </summary>
/// <remarks>
/// <para>
/// Kept separate from <see cref="Policies.IProviderPolicyService"/>, which owns
/// the write side and reads ONE provider at a time: search hands over a whole
/// page of candidates at once, so a per-provider read would be an N+1. Same
/// posture as <see cref="IProviderActiveStatusReader"/> and
/// <c>IProviderBookingStatsReader</c>.
/// </para>
/// <para>
/// It exists for the same structural reason the active-status reader does:
/// discovery lists from Cosmos, and the accepted payment methods live in SQL, so
/// the filter cannot be pushed into the discovery query.
/// </para>
/// </remarks>
public interface IProviderPayoutMethodReader
{
    /// <summary>
    /// Returns the accepted methods (<c>Cash</c> / <c>Digital</c>) per provider.
    /// A provider who has not saved a payout policy is ABSENT from the map
    /// rather than present with an empty set, so a caller can tell "accepts
    /// nothing recorded" from "accepts neither" if it ever needs to; the search
    /// filter treats both the same way and excludes them, because a parent
    /// filtering on "Cash" is asking for providers who have said they take it.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>> GetPayoutMethodsAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken);
}
