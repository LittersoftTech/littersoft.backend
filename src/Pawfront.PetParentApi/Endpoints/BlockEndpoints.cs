using Pawfront.Application.Blocks;
using Pawfront.Contracts.Blocks;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// The pet parent's block list.
/// </summary>
/// <remarks>
/// <para>
/// A block severs the pair across the product from one row: the parent can no
/// longer book the provider, neither sees the other's events, neither can
/// message, and the provider drops out of browse and all five searches. See
/// <see cref="IBlockService"/> for the full policy.
/// </para>
/// <para>
/// <b>Blocking cancels the pair's unfinished jobs</b>, which is why the create
/// response reports how many — a parent should see that blocking their sitter
/// ended the booking they had for Saturday, not discover it later. The one
/// exception is a job already underway (their pet is currently in that provider's
/// care), which runs to completion and comes back in
/// <see cref="BlockParticipantResponse.UncancelledBookings"/>.
/// </para>
/// <para>
/// The routes sit in the <c>RequireOwnedPetParent()</c> group, so the caller's
/// PetParentId is resolved from the JWT and any other id is already rejected with
/// 403 — no explicit ownership check is needed here, unlike the provider host.
/// </para>
/// </remarks>
internal static class BlockEndpoints
{
    public static IEndpointRouteBuilder MapBlockEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder
            .MapGroup("/pet-parents/{petParentId:guid}/blocks")
            .RequireOwnedPetParent();

        group.MapPost("/", async (
            Guid petParentId,
            BlockParticipantRequest request,
            IBlockService blockService,
            CancellationToken cancellationToken) =>
        {
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
                    new BlockParty(BlockPartyType.PetParent, petParentId),
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
            Guid petParentId,
            int? skip,
            int? take,
            IBlockService blockService,
            CancellationToken cancellationToken) =>
        {
            // Only blocks the caller PLACED. One placed against them is never
            // listed: telling somebody they have been blocked confirms the other
            // party acted, which is the thing a block is meant to end.
            var page = await blockService.ListAsync(
                new BlockParty(BlockPartyType.PetParent, petParentId),
                BlockListLimits.NormalizeSkip(skip),
                BlockListLimits.NormalizeTake(take),
                cancellationToken);

            return ApiResults.Ok(BlockMapping.ToResponse(page));
        });

        group.MapDelete("/{blockId:guid}", async (
            Guid petParentId,
            Guid blockId,
            IBlockService blockService,
            CancellationToken cancellationToken) =>
        {
            var block = await blockService.UnblockAsync(
                blockId, new BlockParty(BlockPartyType.PetParent, petParentId), cancellationToken);

            // Unknown id and somebody else's block are one case, so a block id
            // cannot be probed for existence. Unblocking restores contact but does
            // NOT resurrect the bookings the block cancelled — they were cancelled
            // through the ordinary transition and their capacity has been released.
            return block is null
                ? ApiResults.NotFound("BlockNotFound", "This block was not found.")
                : ApiResults.Ok(BlockMapping.ToResponse(block));
        });

        return builder;
    }
}
