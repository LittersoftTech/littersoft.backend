using Microsoft.AspNetCore.RateLimiting;
using Pawfront.Application.Chat;
using Pawfront.ChatApi.Auth;
using Pawfront.ChatApi.RateLimiting;
using Pawfront.Contracts.Chat;

namespace Pawfront.ChatApi.Endpoints;

/// <summary>
/// The inbox and its threads.
///
/// No route carries the caller's own id — it is resolved from the token on every
/// request. That is a deliberate departure from the provider host's "trust the
/// route id" posture, and it is the right one here: a conversation id is the only
/// handle, so trusting anything client-supplied would let one caller read another
/// person's messages.
/// </summary>
internal static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapConversationEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/conversations");

        // Get-or-create. Not idempotent-by-accident but idempotent by design: the
        // UNIQUE (ProviderId, PetParentId) means a second call returns the same
        // thread, so a client need not remember whether it has opened one before.
        group.MapPost("/", async (
            OpenConversationRequest request,
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
                var detail = await chatService.OpenConversationAsync(
                    me!.Value,
                    // Always the other side — a thread never runs within one side.
                    me.Value.Type.Counterparty(),
                    request.CounterpartyId,
                    cancellationToken);

                return ApiResults.Ok(ChatMapping.ToResponse(detail));
            }
            catch (Exception exception)
            {
                return ChatMapping.ToProblem(exception);
            }
        })
        // The tighter of the two limits. Opening threads with strangers is the
        // actual spam vector — it reaches people who never chose to hear from you
        // — whereas message volume within one thread is bounded by the recipient's
        // ability to block.
        .RequireRateLimiting(ChatRateLimiting.OpenConversationPolicy);

        group.MapGet("/", async (
            ICurrentChatParticipant currentParticipant,
            IChatService chatService,
            CancellationToken cancellationToken,
            int? skip,
            int? take) =>
        {
            var (me, failure) = await ChatMapping.ResolveAsync(currentParticipant, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var cards = await chatService.ListConversationsAsync(
                me!.Value,
                skip ?? 0,
                take ?? ChatLimits.MaxConversationPageSize,
                cancellationToken);

            return ApiResults.Ok(cards.Select(ChatMapping.ToResponse).ToList());
        });

        // Declared before "/{conversationId:guid}" so the literal segment is not
        // swallowed by the GUID route. The GUID constraint makes that safe anyway;
        // the ordering is belt and braces.
        group.MapGet("/unread-summary", async (
            ICurrentChatParticipant currentParticipant,
            IChatService chatService,
            CancellationToken cancellationToken) =>
        {
            var (me, failure) = await ChatMapping.ResolveAsync(currentParticipant, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var summary = await chatService.GetUnreadSummaryAsync(me!.Value, cancellationToken);
            return ApiResults.Ok(ChatMapping.ToResponse(summary));
        });

        group.MapGet("/{conversationId:guid}", async (
            Guid conversationId,
            ICurrentChatParticipant currentParticipant,
            IChatService chatService,
            CancellationToken cancellationToken) =>
        {
            var (me, failure) = await ChatMapping.ResolveAsync(currentParticipant, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            try
            {
                var detail = await chatService.GetConversationAsync(conversationId, me!.Value, cancellationToken);
                return ApiResults.Ok(ChatMapping.ToResponse(detail));
            }
            catch (Exception exception)
            {
                return ChatMapping.ToProblem(exception);
            }
        });

        group.MapPost("/{conversationId:guid}/read", async (
            Guid conversationId,
            MarkConversationReadRequest request,
            ICurrentChatParticipant currentParticipant,
            IChatService chatService,
            CancellationToken cancellationToken) =>
        {
            var (me, failure) = await ChatMapping.ResolveAsync(currentParticipant, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            try
            {
                var state = await chatService.MarkReadAsync(
                    conversationId, me!.Value, request.UpToSequence, cancellationToken);

                return ApiResults.Ok(ChatMapping.ToResponse(conversationId, state));
            }
            catch (Exception exception)
            {
                return ChatMapping.ToProblem(exception);
            }
        });

        return builder;
    }
}
