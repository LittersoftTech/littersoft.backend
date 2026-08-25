using Pawfront.Api.Auth;
using Pawfront.Application.Blocks;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Contracts.Blocks;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// The provider's block list.
/// </summary>
/// <remarks>
/// <para>
/// A block severs the pair across the product from one row: the parent can no
/// longer book them, neither sees the other's events, neither can message, and
/// they disappear from each other's search results. See
/// <see cref="IBlockService"/> for the full policy.
/// </para>
/// <para>
/// <b>Blocking cancels the pair's unfinished jobs</b>, which is why the create
/// response reports how many. The one it cannot cancel is a job already underway;
/// that runs to completion and comes back in
/// <see cref="BlockParticipantResponse.UncancelledBookings"/> so the app can say
/// so rather than let the provider discover a live booking with someone they have
/// just blocked.
/// </para>
/// <para>
/// <b>Every route resolves the caller's own ProviderId from the JWT and 403s on a
/// mismatch</b>, joining earnings, ratings, account-delete and support tickets as
/// the exceptions to this host's "trust the route id" posture. It matters here as
/// much as anywhere: trusting the route would let anyone knowing a provider id
/// block that provider's customers, and cancel their bookings in the process.
/// </para>
/// </remarks>
internal static class BlockEndpoints
{
    public static IEndpointRouteBuilder MapBlockEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/blocks");

        group.MapPost("/", async (
            Guid providerId,
            BlockParticipantRequest request,
            HttpContext httpContext,
            IProviderOnboardingService onboardingService,
            IBlockService blockService,
            CancellationToken cancellationToken) =>
        {
            var forbidden = await EnsureCallerOwnsProviderAsync(
                providerId, httpContext, onboardingService, cancellationToken);
            if (forbidden is not null)
            {
                return forbidden;
            }

            if (request.CounterpartyId == Guid.Empty)
            {
                return ApiResults.BadRequest("InvalidRequest", "counterpartyId is required.");
            }

            try
            {
                // The blocked side is always the opposite one, derived here rather
                // than accepted from the body — a block only ever runs
                // provider <-> parent, so taking it from the caller would only
                // create a way to get it wrong.
                var result = await blockService.BlockAsync(
                    new BlockParty(BlockPartyType.Provider, providerId),
                    request.CounterpartyId,
                    request.Reason,
                    cancellationToken);

                return ApiResults.Ok(BlockMapping.ToResponse(result));
            }
            catch (InvalidBlockPairException exception)
            {
                return ApiResults.BadRequest("InvalidRequest", exception.Message);
            }
            catch (ArgumentException exception)
            {
                return ApiResults.BadRequest("InvalidRequest", exception.Message);
            }
        });

        group.MapGet("/", async (
            Guid providerId,
            int? skip,
            int? take,
            HttpContext httpContext,
            IProviderOnboardingService onboardingService,
            IBlockService blockService,
            CancellationToken cancellationToken) =>
        {
            var forbidden = await EnsureCallerOwnsProviderAsync(
                providerId, httpContext, onboardingService, cancellationToken);
            if (forbidden is not null)
            {
                return forbidden;
            }

            // Only blocks the caller PLACED. One placed against them is never
            // listed: telling somebody they have been blocked confirms the other
            // party acted, which is the thing a block is meant to end.
            var page = await blockService.ListAsync(
                new BlockParty(BlockPartyType.Provider, providerId),
                BlockListLimits.NormalizeSkip(skip),
                BlockListLimits.NormalizeTake(take),
                cancellationToken);

            return ApiResults.Ok(BlockMapping.ToResponse(page));
        });

        group.MapDelete("/{blockId:guid}", async (
            Guid providerId,
            Guid blockId,
            HttpContext httpContext,
            IProviderOnboardingService onboardingService,
            IBlockService blockService,
            CancellationToken cancellationToken) =>
        {
            var forbidden = await EnsureCallerOwnsProviderAsync(
                providerId, httpContext, onboardingService, cancellationToken);
            if (forbidden is not null)
            {
                return forbidden;
            }

            var block = await blockService.UnblockAsync(
                blockId, new BlockParty(BlockPartyType.Provider, providerId), cancellationToken);

            // Unknown id and somebody else's block are one case, so a block id
            // cannot be probed for existence. Unblocking restores contact but does
            // NOT resurrect the bookings the block cancelled.
            return block is null
                ? ApiResults.NotFound("BlockNotFound", "This block was not found.")
                : ApiResults.Ok(BlockMapping.ToResponse(block));
        });

        return builder;
    }

    private static async Task<IResult?> EnsureCallerOwnsProviderAsync(
        Guid providerId,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        Guid? callerProviderId;
        try
        {
            var firebaseUserId = FirebaseClaims.GetFirebaseUserId(httpContext.User);
            var caller = await onboardingService.ResolveProviderByFirebaseUidAsync(
                firebaseUserId, cancellationToken);
            callerProviderId = caller.ProviderId;
        }
        catch (ProviderAuthIdentityForFirebaseUserNotFoundException)
        {
            callerProviderId = null;
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }

        return callerProviderId == providerId
            ? null
            : ApiResults.Forbidden("Forbidden", "You can only manage your own blocks.");
    }
}
