using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Pawfront.Application.Chat;
using Pawfront.ChatApi.Auth;
using Pawfront.ChatApi.Endpoints;
using Pawfront.Contracts.Chat;

namespace Pawfront.ChatApi.Hubs;

/// <summary>
/// The live end of chat.
///
/// Every method here funnels into <see cref="IChatService"/> — the same path the
/// REST endpoints take — so a message sent over a socket and one sent over HTTP
/// are handled identically. There is deliberately no second implementation to
/// keep in step.
///
/// The caller's identity comes from the JWT on the connection, never from a
/// method argument. It is resolved once in <see cref="OnConnectedAsync"/> and
/// cached in <see cref="HubCallerContext.Items"/>: hub invocations do not run
/// inside an HTTP request, so the scoped per-request resolver is unusable here.
/// </summary>
[Authorize(AuthenticationSchemes = AuthServiceCollectionExtensions.ProviderScheme + ","
                                   + AuthServiceCollectionExtensions.ParentScheme,
    Policy = AuthServiceCollectionExtensions.ChatUserPolicy)]
internal sealed class ChatHub(
    IChatParticipantResolver participantResolver,
    IChatService chatService,
    IChatPresenceStore presenceStore,
    ILogger<ChatHub> logger) : Hub
{
    private const string ParticipantItemKey = "pawfront.chat.participant";

    public override async Task OnConnectedAsync()
    {
        var participant = await participantResolver.ResolveAsync(Context.User!, Context.ConnectionAborted);

        if (participant is null)
        {
            // Authenticated, but onboarding never produced a ProviderId /
            // PetParentId — there is no identity to send or receive as. Aborting
            // beats leaving a connection that fails every method it calls.
            logger.LogInformation(
                "Rejecting chat connection {ConnectionId}: the caller has no completed profile.",
                Context.ConnectionId);

            Context.Abort();
            return;
        }

        Context.Items[ParticipantItemKey] = participant.Value;

        // The per-person group is what lets a message reach every device this
        // person has open, including ones not looking at the thread.
        await Groups.AddToGroupAsync(
            Context.ConnectionId, ChatGroups.User(participant.Value), Context.ConnectionAborted);

        await presenceStore.SaveConnectionAsync(
            Context.ConnectionId, participant.Value, Context.ConnectionAborted);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Best-effort. This does not run at all if the host crashes, which is why
        // Chat.PurgeStaleConnections exists — a presence row that outlives its
        // socket makes the recipient look permanently present and silences their
        // pushes.
        try
        {
            await presenceStore.DeleteConnectionAsync(Context.ConnectionId, CancellationToken.None);
        }
        catch (Exception disconnectException)
        {
            logger.LogError(
                disconnectException,
                "Failed to clear presence for connection {ConnectionId}; the stale sweep will.",
                Context.ConnectionId);
        }

        // Group membership is dropped by SignalR itself, so there is nothing to
        // undo there.
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Subscribes this connection to a thread and marks it as the one being
    /// viewed — which is what suppresses the recipient's push while they are
    /// actually reading.
    /// </summary>
    public async Task<ConversationResponse> JoinConversation(Guid conversationId)
    {
        var me = RequireParticipant();

        // Authorise BEFORE joining. The group name is derived from the id, so
        // without this check a client could subscribe to any thread by guessing
        // one. This throws ConversationNotFoundException for both "no such thread"
        // and "not yours", which is the intended non-disclosure.
        var detail = await chatService.GetConversationAsync(conversationId, me, Context.ConnectionAborted);

        await Groups.AddToGroupAsync(
            Context.ConnectionId, ChatGroups.Conversation(conversationId), Context.ConnectionAborted);

        await presenceStore.SetActiveConversationAsync(
            Context.ConnectionId, me, conversationId, Context.ConnectionAborted);

        return ChatMapping.ToResponse(detail);
    }

    /// <summary>
    /// Unsubscribes and clears the viewing flag, so messages start earning a push
    /// again.
    /// </summary>
    public async Task LeaveConversation(Guid conversationId)
    {
        var me = RequireParticipant();

        await Groups.RemoveFromGroupAsync(
            Context.ConnectionId, ChatGroups.Conversation(conversationId), Context.ConnectionAborted);

        await presenceStore.SetActiveConversationAsync(
            Context.ConnectionId, me, conversationId: null, Context.ConnectionAborted);
    }

    /// <summary>
    /// Sends a message. Identical path to <c>POST /conversations/{id}/messages</c>,
    /// including the idempotency: resending the same
    /// <see cref="SendMessageRequest.ClientMessageId"/> returns the original.
    /// </summary>
    public async Task<ChatMessageResponse> SendMessage(Guid conversationId, SendMessageRequest request)
    {
        var me = RequireParticipant();

        if (!ChatMessageKinds.TryParseRequested(request.Kind, out var kind))
        {
            throw new HubException("kind must be 'Text' or 'Image'.");
        }

        var messageId = request.ClientMessageId is { } supplied && supplied != Guid.Empty
            ? supplied
            : Guid.NewGuid();

        var result = await chatService.SendMessageAsync(
            new SendChatMessageCommand(
                conversationId,
                me,
                messageId,
                kind,
                request.Text,
                request.Attachment is null ? null : new ChatAttachment(
                    request.Attachment.BlobUrl,
                    request.Attachment.ContentType,
                    request.Attachment.SizeBytes,
                    request.Attachment.Width,
                    request.Attachment.Height)),
            Context.ConnectionAborted);

        return ChatMapping.ToResponse(result.Message);
    }

    public async Task<ConversationReadStateResponse> MarkRead(Guid conversationId, long upToSequence)
    {
        var me = RequireParticipant();

        var state = await chatService.MarkReadAsync(
            conversationId, me, upToSequence, Context.ConnectionAborted);

        return ChatMapping.ToResponse(conversationId, state);
    }

    /// <summary>
    /// Broadcasts typing state to the thread. Never stored anywhere — it is
    /// meaningless a second later, so it exists only as a hub broadcast.
    /// </summary>
    public async Task SetTyping(Guid conversationId, bool isTyping)
    {
        var me = RequireParticipant();

        // Authorised like everything else: without this, anyone could make a
        // stranger's chat show a typing indicator.
        _ = await chatService.GetConversationAsync(conversationId, me, Context.ConnectionAborted);

        await Clients.OthersInGroup(ChatGroups.Conversation(conversationId))
            .SendAsync(
                ChatHubEvents.TypingChanged,
                new
                {
                    conversationId,
                    participantType = me.Type.ToSqlValue(),
                    participantId = me.Id,
                    isTyping
                },
                Context.ConnectionAborted);
    }

    /// <summary>
    /// Keeps the presence row alive. Clients call this on a timer well inside the
    /// sweep's stale window — otherwise a live connection gets purged and its
    /// owner starts receiving pushes for a thread they are reading.
    /// </summary>
    public Task Heartbeat()
    {
        var me = RequireParticipant();
        return presenceStore.SaveConnectionAsync(Context.ConnectionId, me, Context.ConnectionAborted);
    }

    private ChatParticipant RequireParticipant()
    {
        if (Context.Items.TryGetValue(ParticipantItemKey, out var stored)
            && stored is ChatParticipant participant)
        {
            return participant;
        }

        // OnConnectedAsync aborts a connection it cannot resolve, so reaching here
        // means the connection outlived its identity — treat it as unauthenticated
        // rather than guessing.
        throw new HubException("This connection is not associated with a chat profile. Reconnect.");
    }
}
