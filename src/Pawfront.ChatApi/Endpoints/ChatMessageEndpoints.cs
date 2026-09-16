using Microsoft.AspNetCore.RateLimiting;
using Pawfront.Application.Chat;
using Pawfront.ChatApi.Auth;
using Pawfront.ChatApi.RateLimiting;
using Pawfront.Contracts.Chat;

namespace Pawfront.ChatApi.Endpoints;

/// <summary>
/// Messages within a thread.
///
/// Sending exists as REST as well as a hub method for two reasons: a client whose
/// socket has dropped can still send, and the flow stays testable with the
/// Postman/newman suite the rest of the product uses. Both entry points call the
/// same <see cref="IChatService.SendMessageAsync"/>, so there is no second
/// implementation to keep in step.
/// </summary>
internal static class ChatMessageEndpoints
{
    public static IEndpointRouteBuilder MapChatMessageEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/conversations/{conversationId:guid}/messages");

        group.MapGet("/", async (
            Guid conversationId,
            ICurrentChatParticipant currentParticipant,
            IChatService chatService,
            CancellationToken cancellationToken,
            long? beforeSequence,
            int? take) =>
        {
            var (me, failure) = await ChatMapping.ResolveAsync(currentParticipant, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            try
            {
                var page = await chatService.GetHistoryAsync(
                    conversationId,
                    me!.Value,
                    beforeSequence,
                    take ?? ChatLimits.DefaultPageSize,
                    cancellationToken);

                return ApiResults.Ok(ChatMapping.ToResponse(page));
            }
            catch (Exception exception)
            {
                return ChatMapping.ToProblem(exception);
            }
        });

        group.MapPost("/", async (
            Guid conversationId,
            SendMessageRequest request,
            ICurrentChatParticipant currentParticipant,
            IChatService chatService,
            CancellationToken cancellationToken) =>
        {
            var (me, failure) = await ChatMapping.ResolveAsync(currentParticipant, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            if (!ChatMessageKinds.TryParseRequested(request.Kind, out var kind))
            {
                return ApiResults.BadRequest(
                    "UnsupportedMessageKind", "kind must be 'Text' or 'Image'.");
            }

            // The client's id becomes the message id, which is what makes a retry
            // safe. Minting one here when it is omitted keeps the endpoint usable
            // from a REST console — but such a caller loses the idempotency, since
            // each attempt would carry a fresh id.
            var messageId = request.ClientMessageId is { } supplied && supplied != Guid.Empty
                ? supplied
                : Guid.NewGuid();

            try
            {
                var result = await chatService.SendMessageAsync(
                    new SendChatMessageCommand(
                        conversationId,
                        me!.Value,
                        messageId,
                        kind,
                        request.Text,
                        ToAttachment(request.Attachment)),
                    cancellationToken);

                // 200, not 201: resending the same clientMessageId is an accepted
                // replay that returns the original message, so "Created" would be a
                // lie half the time. There is no per-message GET to point a
                // Location header at either.
                return ApiResults.Ok(ChatMapping.ToResponse(result.Message));
            }
            catch (Exception exception)
            {
                return ChatMapping.ToProblem(exception);
            }
        })
        // Applies to the REST send only. The hub's SendMessage is NOT covered —
        // ASP.NET Core rate limiting is middleware, and a hub invocation arrives
        // over an already-established socket rather than as a new request. Worth
        // knowing: a client that wants to flood will do it over the socket.
        .RequireRateLimiting(ChatRateLimiting.SendMessagePolicy);

        group.MapDelete("/{messageId:guid}", async (
            Guid conversationId,
            Guid messageId,
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
                // A soft delete: the message keeps its place and its sequence, and
                // only its content goes. Removing it would leave a hole in the
                // thread and rewrite what the other party already saw.
                var message = await chatService.DeleteMessageAsync(
                    conversationId, messageId, me!.Value, cancellationToken);

                return ApiResults.Ok(ChatMapping.ToResponse(message));
            }
            catch (Exception exception)
            {
                return ChatMapping.ToProblem(exception);
            }
        });

        return builder;
    }

    private static ChatAttachment? ToAttachment(ChatAttachmentPayload? payload) =>
        payload is null
            ? null
            : new ChatAttachment(
                payload.BlobUrl,
                payload.ContentType,
                payload.SizeBytes,
                payload.Width,
                payload.Height);
}
