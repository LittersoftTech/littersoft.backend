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

        // The inbox, and its search bar — one endpoint, because searching an inbox
        // returns inbox cards. `?search=` filters on the counterparty's name and
        // the thread's last-message preview, both of which this read already has,
        // so the filtered and unfiltered lists cannot page or sort differently.
        //
        // It is deliberately NOT full message-history search: bodies live in
        // Cosmos partitioned by conversation, so matching every one of them would
        // be a cross-partition scan per keystroke. That wants a search index, not
        // a query.
        group.MapGet("/", async (
            ICurrentChatParticipant currentParticipant,
            IChatService chatService,
            CancellationToken cancellationToken,
            string? search,
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
                search,
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

        // "Delete this chat" — for the caller ONLY. The counterparty keeps every
        // message and their place in the thread, which is both the requirement and
        // the only thing the storage allows: a message body is one Cosmos document
        // read by both sides, so clearing it for one would clear it for both.
        //
        // Not permanent, and deliberately so. The thread leaves this caller's
        // inbox now and comes back the moment the counterparty writes again,
        // carrying on without the cleared history. A delete that stopped you
        // receiving messages would not be a delete, it would be a broken
        // conversation.
        group.MapDelete("/{conversationId:guid}", async (
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
                var state = await chatService.DeleteConversationAsync(
                    conversationId, me!.Value, cancellationToken);

                return ApiResults.Ok(ChatMapping.ToDeleteResponse(conversationId, state));
            }
            catch (Exception exception)
            {
                return ChatMapping.ToProblem(exception);
            }
        });

        // The jobs behind the thread — the chat screen's "View Jobs". Works from
        // either side: the conversation id IS the provider/parent pair, so the
        // caller names nobody and there is nothing extra to authorise beyond the
        // participant check every other route here makes.
        group.MapGet("/{conversationId:guid}/bookings", async (
            Guid conversationId,
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

            try
            {
                var jobs = await chatService.GetConversationJobsAsync(
                    conversationId,
                    me!.Value,
                    skip ?? 0,
                    take ?? ChatLimits.MaxJobsPageSize,
                    cancellationToken);

                return ApiResults.Ok(ChatMapping.ToResponse(jobs));
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
