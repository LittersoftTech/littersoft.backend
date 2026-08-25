using Microsoft.Extensions.Logging;
using Pawfront.Application.Bookings;
using Pawfront.Application.Providers;

namespace Pawfront.Application.Blocks;

/// <inheritdoc cref="IBlockService"/>
public sealed class BlockService(
    IBlockStore store,
    IBulkBookingCancellationService bulkCancellation,
    IProviderDiscoveryService discovery,
    ILogger<BlockService> logger) : IBlockService
{
    public async Task<BlockResult> BlockAsync(
        BlockParty blocker,
        Guid blockedId,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (blockedId == Guid.Empty)
        {
            throw new ArgumentException("A counterparty id is required.", nameof(blockedId));
        }

        if (blockedId == blocker.Id)
        {
            // Not reachable through the hosts (the two ids come from different
            // tables), but a self-block would sail past the same-side CHECK and
            // sit in the table forever poisoning every read.
            throw new InvalidBlockPairException("You cannot block yourself.");
        }

        // The block lands FIRST and commits before anything is cancelled, so a
        // booking cannot be created underneath the cancellations: the create
        // procedures read this table under HOLDLOCK and serialise behind the
        // insert. The job list comes back from that same transaction.
        var (block, wasAlreadyBlocked, jobs) = await store.BlockAsync(
            blocker,
            blocker.Type.Counterparty(),
            blockedId,
            reason,
            cancellationToken);

        if (jobs.Count == 0)
        {
            return new BlockResult(block, wasAlreadyBlocked, Array.Empty<BlockCancelledJob>());
        }

        var outcomes = await CancelJobsAsync(blocker, jobs, cancellationToken);
        return new BlockResult(block, wasAlreadyBlocked, outcomes);
    }

    /// <summary>
    /// Cancels the pair's unfinished jobs through the ORDINARY per-booking
    /// transition, by handing them to the same service the app's multi-select
    /// uses. Nothing about cancelling is reimplemented here, so each job gets its
    /// party check, its audit row, its freed capacity and its counterparty push
    /// exactly as a hand cancellation would -- and a batch of three notifies the
    /// counterparty three times, which is correct: each is a different commitment
    /// being ended.
    /// </summary>
    private async Task<IReadOnlyList<BlockCancelledJob>> CancelJobsAsync(
        BlockParty blocker,
        IReadOnlyList<BlockCancellableJob> jobs,
        CancellationToken cancellationToken)
    {
        var actor = blocker.Type == BlockPartyType.Provider
            ? BookingStatusActor.Provider
            : BookingStatusActor.Parent;

        var jobIdsByBooking = jobs.ToDictionary(
            job => (job.BookingId, job.BookingType),
            job => $"PF-{job.JobNumber:D6}");

        var results = new List<BlockCancelledJob>(jobs.Count);

        // Chunked to the batch cap. A pair with more unfinished jobs than that is
        // implausible, but a block must not fail because one did -- and the cap
        // exists to bound one request's cost, which chunking respects.
        foreach (var chunk in jobs.Chunk(BulkBookingCancellationLimits.MaxItems))
        {
            var command = new BulkCancelBookingsCommand(
                chunk.Select(job => new BulkCancelBookingItem(job.BookingId, job.BookingType)).ToList(),
                actor,
                blocker.Id,
                "Cancelled automatically when the booking parties were blocked.");

            BulkCancelBookingsResult batch;
            try
            {
                batch = await bulkCancellation.CancelAsync(command, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The block has committed. Failing the call now would tell the
                // user their block did not happen when it did -- the same failure
                // the chat send was rebuilt to avoid -- and would leave them
                // unable to retry, since a second block is a no-op that cancels
                // nothing. Report the jobs as uncancelled instead; either party
                // can still cancel them by hand, and the pair is already severed.
                logger.LogError(
                    exception,
                    "Cancelling {JobCount} job(s) after {BlockerType} {BlockerId} placed a block failed. The block itself stands.",
                    chunk.Length, blocker.Type, blocker.Id);

                results.AddRange(chunk.Select(job => new BlockCancelledJob(
                    job.BookingId,
                    job.BookingType,
                    jobIdsByBooking[(job.BookingId, job.BookingType)],
                    Cancelled: false,
                    Status: job.Status,
                    ErrorCode: "CancellationFailed",
                    Message: "This booking could not be cancelled automatically. Cancel it from your bookings.")));
                continue;
            }

            results.AddRange(batch.Results.Select(outcome => new BlockCancelledJob(
                outcome.BookingId,
                outcome.BookingType,
                jobIdsByBooking.TryGetValue((outcome.BookingId, outcome.BookingType), out var jobId)
                    ? jobId
                    : string.Empty,
                outcome.Cancelled,
                outcome.Status,
                outcome.ErrorCode,
                outcome.Message)));
        }

        return results;
    }

    public Task<ParticipantBlock?> UnblockAsync(
        Guid blockId,
        BlockParty blocker,
        CancellationToken cancellationToken) =>
        store.UnblockAsync(blockId, blocker, cancellationToken);

    public async Task<ParticipantBlockPage> ListAsync(
        BlockParty blocker,
        int skip,
        int take,
        CancellationToken cancellationToken)
    {
        var normalisedSkip = BlockListLimits.NormalizeSkip(skip);
        var normalisedTake = BlockListLimits.NormalizeTake(take);

        var (rows, totalCount) = await store.ListAsync(
            blocker, normalisedSkip, normalisedTake, cancellationToken);

        var blocks = await ResolveBusinessNamesAsync(rows, cancellationToken);
        return new ParticipantBlockPage(blocks, totalCount, normalisedSkip, normalisedTake);
    }

    /// <summary>
    /// Fills in a blocked PROVIDER's business name and image from their Cosmos
    /// offering document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Provider.Providers</c> holds only the person's own name, so a parent who
    /// blocked "Happy Paws Hotel" would otherwise see the owner's name and not
    /// recognise who they had blocked. The offering document is the same source
    /// the booking detail's provider block and the chat inbox avatars use, which
    /// is deliberate: the same business should look the same everywhere.
    /// </para>
    /// <para>
    /// One point read per DISTINCT provider on the page, all in flight together.
    /// Best-effort throughout -- a provider still mid-onboarding legitimately has
    /// no offering, and a read that fails costs a business name rather than the
    /// list. Freelancers have no business name by design and read null.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ParticipantBlock>> ResolveBusinessNamesAsync(
        IReadOnlyList<ParticipantBlockRow> rows,
        CancellationToken cancellationToken)
    {
        var lookups = rows
            .Where(row => row.Block.BlockedType == BlockPartyType.Provider
                          && !string.IsNullOrWhiteSpace(row.BlockedServiceCategory))
            .Select(row => (row.Block.BlockedId, Category: row.BlockedServiceCategory!))
            .DistinctBy(pair => pair.BlockedId)
            .ToList();

        var summaries = new Dictionary<Guid, ProviderSummary>();
        if (lookups.Count > 0)
        {
            var reads = lookups.Select(async pair =>
            {
                try
                {
                    return (pair.BlockedId,
                        Summary: await discovery.GetSummaryAsync(
                            pair.BlockedId, pair.Category, cancellationToken));
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(
                        exception,
                        "Reading the offering document for blocked provider {ProviderId} failed; their business name is omitted.",
                        pair.BlockedId);
                    return (pair.BlockedId, Summary: (ProviderSummary?)null);
                }
            });

            foreach (var (providerId, summary) in await Task.WhenAll(reads))
            {
                if (summary is not null)
                {
                    summaries[providerId] = summary;
                }
            }
        }

        return rows.Select(row =>
        {
            if (row.Block.BlockedType != BlockPartyType.Provider
                || !summaries.TryGetValue(row.Block.BlockedId, out var summary))
            {
                return row.Block;
            }

            return row.Block with
            {
                BlockedBusinessName = summary.DisplayName,
                // The parent's own photo already came off their SQL row; only a
                // provider's has to come from here.
                BlockedPhotoUrl = row.Block.BlockedPhotoUrl ?? summary.ImageUrl
            };
        }).ToList();
    }
}
