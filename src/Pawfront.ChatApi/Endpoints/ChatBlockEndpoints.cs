using Pawfront.Application.Chat;
using Pawfront.ChatApi.Auth;
using Pawfront.Contracts.Chat;

namespace Pawfront.ChatApi.Endpoints;

/// <summary>
/// Blocking, which chat being OPEN makes non-optional: any parent can message any
/// provider with no booking between them, so unsolicited contact is possible by
/// design and a block is the user's own remedy for it.
///
/// A block stops new messages BOTH ways and stops the thread being reopened, but
/// deliberately leaves existing history readable — it is part of both parties'
/// record, and removing it would also remove what a blocked user might need in
/// order to report the exchange.
///
/// Reporting is NOT here. It has to terminate in a support workflow and this
/// backend has no Helpline / ticket module, so it belongs with that module rather
/// than ahead of it.
/// </summary>
internal static class ChatBlockEndpoints
{
    public static IEndpointRouteBuilder MapChatBlockEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/blocks");

        group.MapPost("/", async (
            BlockChatParticipantRequest request,
            ICurrentChatParticipant currentParticipant,
            IChatService chatService,
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
                // Always the opposite side, derived from the caller's token — a
                // block only ever runs provider <-> parent.
                var block = await chatService.BlockAsync(
                    me!.Value,
                    me.Value.Type.Counterparty(),
                    request.CounterpartyId,
                    request.Reason,
                    cancellationToken);

                return ApiResults.Ok(ChatMapping.ToResponse(block));
            }
            catch (Exception exception)
            {
                return ChatMapping.ToProblem(exception);
            }
        });

        group.MapGet("/", async (
            ICurrentChatParticipant currentParticipant,
            IChatService chatService,
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
            var blocks = await chatService.ListBlocksAsync(me!.Value, cancellationToken);
            return ApiResults.Ok(blocks.Select(ChatMapping.ToResponse).ToList());
        });

        group.MapDelete("/{chatBlockId:guid}", async (
            Guid chatBlockId,
            ICurrentChatParticipant currentParticipant,
            IChatService chatService,
            CancellationToken cancellationToken) =>
        {
            var (me, failure) = await ChatMapping.ResolveAsync(currentParticipant, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var block = await chatService.UnblockAsync(chatBlockId, me!.Value, cancellationToken);

            // Unknown id and somebody else's block are one case, so a block id
            // cannot be probed for existence.
            return block is null
                ? ApiResults.NotFound("ChatBlockNotFound", "This block was not found.")
                : ApiResults.Ok(ChatMapping.ToResponse(block));
        });

        return builder;
    }
}
