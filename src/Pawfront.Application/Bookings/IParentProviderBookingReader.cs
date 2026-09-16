using Pawfront.Application.ParentOnboarding;

namespace Pawfront.Application.Bookings;

/// <summary>
/// One page of the jobs between a provider and a pet parent.
/// </summary>
/// <param name="TotalCount">
/// Every job the pair has, not just this page — the read returns it alongside the
/// rows so a caller gets both the header count and <c>hasMore</c> without a second
/// query.
/// </param>
public sealed record ParentProviderBookingPage(
    IReadOnlyList<PendingParentJob> Jobs,
    int TotalCount);

/// <summary>
/// Every booking between ONE provider and ONE pet parent, newest first.
/// </summary>
/// <remarks>
/// <para>
/// Backs the chat screen's "View Jobs". The two "my bookings" lists are scoped to
/// one person and the pending-job lists are filtered to unfinished work; this is
/// scoped to the PAIR and filtered by nothing, because a completed or cancelled
/// job is part of what these two have done together and belongs on the list.
/// </para>
/// <para>
/// It yields <see cref="PendingParentJob"/> — the job card the two delete
/// refusals already hand back — so the same shape, the same
/// <c>PendingJobReader</c> and the same <see cref="IPendingJobEnricher"/> serve
/// every surface that shows a job summary. The type keeps its original name
/// because renaming it would churn two shipped endpoints for nothing; read it as
/// "a parent's job card", not "a job that is pending".
/// </para>
/// <para>
/// Custom walk-ins can never appear here: they carry no PetParentId, so the pair
/// predicate excludes them without needing to say so.
/// </para>
/// </remarks>
public interface IParentProviderBookingReader
{
    Task<ParentProviderBookingPage> ListAsync(
        Guid providerId,
        Guid petParentId,
        int skip,
        int take,
        CancellationToken cancellationToken);
}
