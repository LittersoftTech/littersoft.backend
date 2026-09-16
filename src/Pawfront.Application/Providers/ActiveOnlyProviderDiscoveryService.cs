namespace Pawfront.Application.Providers;

/// <summary>
/// Wraps the Cosmos-backed discovery reader and drops providers whose SQL master
/// switch is off (<c>IsActive = 0</c>) or who have deleted their account
/// (<c>IsDeleted = 1</c>).
///
/// The two facts live in different stores — the offering document Cosmos lists
/// from has no notion of the switch — so the filter cannot be pushed into the
/// discovery query. It sits here, in ONE decorator, rather than at each call
/// site: <c>GET /providers</c> and all five <c>/providers/search/*</c> cards go
/// through <see cref="ListAsync"/>, and so will anything added later. A per-call-site
/// check would have to be remembered every time.
///
/// <see cref="GetSummaryAsync"/> is deliberately NOT filtered. That is the point
/// read behind "who is the provider on my booking" (address, photo, business
/// name) — a parent with an existing booking must still see who it is with after
/// the provider goes inactive, and the booking-detail read would otherwise start
/// returning a blank provider block.
/// </summary>
/// <remarks>
/// Public (unlike the other Application services, which are internal and
/// registered next door in <c>ApplicationServiceRegistration</c>) because the
/// wrapping has to be done where the inner implementation is known — the Cosmos
/// registration. Registering it in Application would not work: both hosts call
/// <c>AddPawfrontApplication()</c> BEFORE <c>AddPawfrontCosmosInfrastructure()</c>,
/// so the decorator would win the <c>TryAdd</c> for the interface and then resolve
/// itself as its own inner reader.
/// </remarks>
public sealed class ActiveOnlyProviderDiscoveryService(
    IProviderDiscoveryService inner,
    IProviderActiveStatusReader activeStatusReader) : IProviderDiscoveryService
{
    public async Task<IReadOnlyList<ProviderSummary>> ListAsync(
        ProviderDiscoveryFilter filter,
        CancellationToken cancellationToken)
    {
        // Paging must happen AFTER the active filter, or an inactive provider
        // inside a page would leave a hole (and could push an active one off the
        // end). So the inner read is asked for everything and paged here — which
        // costs nothing extra: the Cosmos reader already fetches the whole
        // category partition and applies skip/take in memory.
        var candidates = await inner.ListAsync(
            filter with { Skip = 0, Take = int.MaxValue },
            cancellationToken);

        if (candidates.Count == 0)
        {
            return candidates;
        }

        var providerIds = candidates.Select(c => c.ProviderId).Distinct().ToArray();
        var active = await activeStatusReader.GetActiveProviderIdsAsync(providerIds, cancellationToken);

        return candidates
            .Where(c => active.Contains(c.ProviderId))
            .Skip(Math.Max(0, filter.Skip))
            .Take(filter.Take <= 0 ? DefaultTake : filter.Take)
            .ToList();
    }

    public Task<ProviderSummary?> GetSummaryAsync(
        Guid providerId,
        string serviceCategory,
        CancellationToken cancellationToken)
        => inner.GetSummaryAsync(providerId, serviceCategory, cancellationToken);

    // Mirrors the inner reader's fallback for a non-positive Take.
    private const int DefaultTake = 50;
}
