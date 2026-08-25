using Pawfront.Application.Blocks;

namespace Pawfront.Application.Providers;

/// <summary>
/// Wraps the discovery reader and drops providers the caller is blocked from, in
/// either direction.
/// </summary>
/// <remarks>
/// <para>
/// Exactly the shape <see cref="ActiveOnlyProviderDiscoveryService"/> takes, and
/// for the same reason: the block lives in SQL while discovery reads Cosmos, so
/// the filter cannot be pushed into the query. It sits in ONE decorator rather
/// than at each call site because <c>GET /providers</c> and all five
/// <c>/providers/search/*</c> cards go through <see cref="ListAsync"/> -- and so
/// will anything added later. A per-call-site check would have to be remembered
/// every time.
/// </para>
/// <para>
/// Without it a parent keeps seeing a provider they blocked and only finds out at
/// booking time, when the create is refused. Symmetrically a provider who blocked
/// a parent stays visible to them.
/// </para>
/// <para>
/// <see cref="GetSummaryAsync"/> is deliberately NOT filtered, matching the
/// active-status decorator. It is the point read behind "who is the provider on my
/// booking", and a parent must still see who a booking they already have is with
/// -- blocking someone does not erase the history with them. The booking-detail
/// provider block would otherwise go blank.
/// </para>
/// <para>
/// Public for the same reason its sibling is: the wrapping has to happen where
/// the inner implementation is known, in the Cosmos registration. Registering it
/// in Application would let it resolve itself as its own inner reader.
/// </para>
/// </remarks>
public sealed class BlockAwareProviderDiscoveryService(
    IProviderDiscoveryService inner,
    ICurrentBlockParty currentParty,
    IMyBlockLookup blockLookup) : IProviderDiscoveryService
{
    public async Task<IReadOnlyList<ProviderSummary>> ListAsync(
        ProviderDiscoveryFilter filter,
        CancellationToken cancellationToken)
    {
        var me = await currentParty.GetAsync(cancellationToken);
        if (me is null)
        {
            // Nobody to have blocked anybody. Pass straight through rather than
            // paying for a lookup that cannot match.
            return await inner.ListAsync(filter, cancellationToken);
        }

        var blocked = await blockLookup.GetAsync(me.Value, cancellationToken);
        if (blocked.IsEmpty)
        {
            return await inner.ListAsync(filter, cancellationToken);
        }

        // Paging happens AFTER the filter, or a blocked provider inside a page
        // would leave a hole (and could push a visible one off the end). Asking
        // the inner reader for everything costs nothing extra: it already fetches
        // the whole category partition and pages in memory. Same as the
        // active-status decorator this sits on top of.
        var candidates = await inner.ListAsync(
            filter with { Skip = 0, Take = int.MaxValue },
            cancellationToken);

        return candidates
            .Where(c => !blocked.IsBlocked(c.ProviderId))
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
