using Pawfront.Application.Bookings;

namespace Pawfront.Infrastructure.Sql.Bookings;

/// <summary>
/// In-memory dev fallback for the chat thread's "View Jobs" list: reports that
/// the pair have no shared history.
/// </summary>
/// <remarks>
/// A zeros-reporting Null store rather than a working in-memory one, matching
/// <c>NullProviderEarningsStore</c> and for the same reason: this is a read over
/// the two booking tables joined to provider and pet rows, none of which the
/// in-memory stores hold in a form it could query. An empty jobs list is a far
/// better outcome on a developer machine than a 500 on the chat screen.
/// </remarks>
internal sealed class NullParentProviderBookingReader : IParentProviderBookingReader
{
    public Task<ParentProviderBookingPage> ListAsync(
        Guid providerId,
        Guid petParentId,
        int skip,
        int take,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ParentProviderBookingPage([], 0));
}
