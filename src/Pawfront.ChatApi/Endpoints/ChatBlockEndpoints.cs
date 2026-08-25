using Pawfront.Application.Blocks;
using Pawfront.ChatApi.Auth;
using Pawfront.Contracts.Blocks;

namespace Pawfront.ChatApi.Endpoints;

/// <summary>
/// Blocking, from the chat host.
/// </summary>
/// <remarks>
/// <para>
/// These routes were the whole of blocking when it was a chat remedy: chat is
/// OPEN, so any parent can message any provider with no booking between them, and
/// a block was the user's answer to that. It is more than that now -- the same
/// block refuses new bookings, hides each party's events from the other, and takes
/// the provider out of browse and all five searches -- so the feature lives in
/// <see cref="IBlockService"/> and the two API hosts carry the same three routes.
/// This host keeps them because blocking somebody you are talking to is where the
/// action naturally sits.
/// </para>
/// <para>
/// <b>Placing a block cancels the pair's unfinished bookings</b>, which is why the
/// response says how many. The one it cannot cancel is a job already underway --
/// see <see cref="BlockParticipantResponse.UncancelledBookings"/>.
/// </para>
/// <para>
/// Reporting is NOT here and is a different thing: a support ticket is a report to
/// support, not a sanction the reporter applies, so it blocks nobody. The two are
/// independent remedies.
/// </para>
/// </remarks>
internal static class ChatBlockEndpoints
{
    public static IEndpointRouteBuilder MapChatBlockEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/blocks");

        group.MapPost("/", async (
            BlockParticipantRequest request,
            ICurrentChatParticipant currentParticipant,
            IBlockService blockService,
            CancellationToken cancellationToken) =>
        {
            var (me, failure) = await ChatMapping.ResolveAsync(currentParticipant, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            if (request.CounterpartyId == Guid.Empty)
            {
                return ApiResults.BadRequest("InvalidRequest", "counterpartyId is required.");
            }

            try
            {
                // The blocked side is derived from the caller's own — a block only
                // ever runs provider <-> parent, so taking it from the body would
                // only create a way to get it wrong.
                var result = await blockService.BlockAsync(
                    ToBlockParty(me!.Value),
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
            int? skip,
            int? take,
            ICurrentChatParticipant currentParticipant,
            IBlockService blockService,
            CancellationToken cancellationToken) =>
        {
            var (me, failure) = await ChatMapping.ResolveAsync(currentParticipant, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            // Only blocks the caller PLACED. Blocks against them are never
            // returned: telling someone they have been blocked confirms the other
            // party acted, which is exactly what a block is meant to end.
            var page = await blockService.ListAsync(
                ToBlockParty(me!.Value),
                BlockListLimits.NormalizeSkip(skip),
                BlockListLimits.NormalizeTake(take),
                cancellationToken);

            return ApiResults.Ok(BlockMapping.ToResponse(page));
        });

        group.MapDelete("/{blockId:guid}", async (
            Guid blockId,
            ICurrentChatParticipant currentParticipant,
            IBlockService blockService,
            CancellationToken cancellationToken) =>
        {
            var (me, failure) = await ChatMapping.ResolveAsync(currentParticipant, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var block = await blockService.UnblockAsync(
                blockId, ToBlockParty(me!.Value), cancellationToken);

            // Unknown id and somebody else's block are one case, so a block id
            // cannot be probed for existence.
            return block is null
                ? ApiResults.NotFound("BlockNotFound", "This block was not found.")
                : ApiResults.Ok(BlockMapping.ToResponse(block));
        });

        return builder;
    }

    /// <summary>
    /// The one place chat's participant vocabulary meets the block module's. The
    /// two enums agree and probably always will, but they answer different
    /// questions, and converting here is what keeps a block placed from the
    /// bookings screen from having to name itself in chat's terms.
    /// </summary>
    private static BlockParty ToBlockParty(Application.Chat.ChatParticipant participant) =>
        new(
            participant.Type == Application.Chat.ChatParticipantType.Provider
                ? BlockPartyType.Provider
                : BlockPartyType.PetParent,
            participant.Id);
}
