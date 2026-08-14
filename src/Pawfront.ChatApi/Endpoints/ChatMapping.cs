using Pawfront.Application.Chat;
using Pawfront.Application.ParentOnboarding;
using Pawfront.ChatApi.Auth;
using Pawfront.Contracts.Chat;
using Pawfront.Contracts.ParentOnboarding;
using Pawfront.Application.Support;

namespace Pawfront.ChatApi.Endpoints;

/// <summary>
/// Application types to wire types, and typed chat failures to HTTP.
///
/// Kept in one place so every chat endpoint answers the same code for the same
/// condition — the thing that quietly drifts when each handler maps its own.
/// </summary>
internal static class ChatMapping
{
    public static ConversationResponse ToResponse(ChatConversationDetail detail) =>
        Build(detail.Conversation, detail.Me, detail.Counterparty);

    public static ConversationResponse ToResponse(ChatConversationCard card) =>
        Build(card.Conversation, card.Me, card.Counterparty);

    private static ConversationResponse Build(
        ChatConversation conversation,
        ChatParticipantState me,
        ChatCounterparty counterparty) =>
        new(
            conversation.ConversationId,
            conversation.ProviderId,
            conversation.PetParentId,
            new ChatCounterpartyResponse(
                counterparty.ParticipantType.ToSqlValue(),
                counterparty.ParticipantId,
                counterparty.Name,
                counterparty.PhotoUrl),
            conversation.LastSequence,
            conversation.LastMessageAtUtc,
            conversation.LastMessagePreview,
            conversation.LastMessageSenderType,
            me.LastReadSequence,
            me.UnreadCount,
            me.IsMuted,
            conversation.CreatedAtUtc);

    public static ChatMessageResponse ToResponse(ChatMessage message) =>
        new(
            message.MessageId,
            message.ConversationId,
            message.Sequence,
            message.SenderType.ToSqlValue(),
            message.SenderId,
            message.Kind.ToWireValue(),
            message.Text,
            message.Attachment is null ? null : new ChatAttachmentPayload(
                message.Attachment.BlobUrl,
                message.Attachment.ContentType,
                message.Attachment.SizeBytes,
                message.Attachment.Width,
                message.Attachment.Height),
            message.IsDeleted,
            message.CreatedAtUtc,
            message.EditedAtUtc,
            message.DeletedAtUtc);

    public static ChatMessagePageResponse ToResponse(ChatMessagePage page) =>
        new(
            page.Messages.Select(ToResponse).ToList(),
            page.NextBeforeSequence,
            page.HasMore);

    public static ConversationReadStateResponse ToResponse(Guid conversationId, ChatParticipantState state) =>
        new(
            conversationId,
            state.ParticipantType.ToSqlValue(),
            state.ParticipantId,
            state.LastReadSequence,
            state.UnreadCount,
            state.IsMuted);

    public static ChatUnreadSummaryResponse ToResponse(ChatUnreadSummary summary) =>
        new(summary.UnreadMessageCount, summary.UnreadConversationCount);

    public static DeleteConversationResponse ToDeleteResponse(
        Guid conversationId, ChatParticipantState state) =>
        new(conversationId, state.ClearedUpToSequence, state.DeletedAtUtc);

    /// <summary>
    /// The thread's job list. Reuses <see cref="PendingParentJobResponse"/> rather
    /// than declaring a near-identical eighteen-field twin: it is already the
    /// product's job card, the app already parses it from the two delete
    /// refusals, and a second shape would be one more thing to keep in step. Read
    /// the name as "a parent's job card" — nothing here is necessarily pending.
    /// </summary>
    public static ConversationJobsResponse ToResponse(ChatConversationJobs jobs) =>
        new(
            jobs.ConversationId,
            jobs.ProviderId,
            jobs.PetParentId,
            jobs.Jobs.Select(ToResponse).ToList(),
            jobs.TotalCount,
            jobs.Skip,
            jobs.Take,
            jobs.HasMore);

    private static PendingParentJobResponse ToResponse(PendingParentJob job) =>
        new(
            job.BookingId,
            job.BookingType,
            job.JobId,
            job.ProviderId,
            job.ProviderName,
            job.ProviderProfilePhotoUrl,
            job.ServiceCategory,
            job.SubCategory,
            job.Status,
            job.ServiceDate,
            job.CheckOutDate,
            job.StartTime,
            job.EndTime,
            job.PetName,
            job.ServiceId,
            job.ServiceItemCode,
            new PendingJobPriceResponse(
                job.Price?.PricePerUnit,
                // The unit follows from the booking kind and category, so it is
                // known even when the amount is not; per-hour is the neutral
                // fallback the parent host's copy of this mapping uses too.
                job.Price?.PriceUnit ?? PendingJobPriceUnits.PerHour,
                job.Price?.TotalAmount,
                job.Price?.PawfrontFee,
                job.Price?.FeePercentage ?? 0m));

    public static ChatBlockResponse ToResponse(ChatBlock block) =>
        new(
            block.ChatBlockId,
            block.BlockedType.ToSqlValue(),
            block.BlockedId,
            block.BlockedName,
            block.BlockedPhotoUrl,
            block.Reason,
            block.CreatedAtUtc);

    /// <summary>
    /// Resolves the caller, or returns the 403 to answer with. Every chat handler
    /// starts here: the participant id comes from the token, never from a route or
    /// body, so there is no path by which a caller can act as somebody else.
    /// </summary>
    public static async Task<(ChatParticipant? Participant, IResult? Failure)> ResolveAsync(
        ICurrentChatParticipant currentParticipant,
        CancellationToken cancellationToken)
    {
        var participant = await currentParticipant.GetParticipantAsync(cancellationToken);

        return participant is null
            ? (null, ApiResults.ChatProfileNotCompleted())
            : (participant, null);
    }

    /// <summary>
    /// Maps a typed chat failure to its HTTP answer.
    ///
    /// Note what is deliberately indistinguishable: a thread that does not exist
    /// and one that is not yours both answer 404 <c>ConversationNotFound</c>, so a
    /// conversation id cannot be probed for existence. A block answers 403 without
    /// saying which party raised it — naming the direction would confirm the other
    /// person acted, which is the thing a block is meant to end.
    /// </summary>
    public static IResult ToProblem(Exception exception) => exception switch
    {
        ConversationNotFoundException =>
            ApiResults.NotFound("ConversationNotFound", "This conversation was not found."),

        ChatForbiddenException =>
            ApiResults.Forbidden("Forbidden", "You are not a party to this conversation."),

        ChatBlockedException =>
            ApiResults.Forbidden("ConversationBlocked", "This conversation is not available."),

        ChatProviderAccountDeletedException =>
            ApiResults.Conflict("ProviderAccountDeleted", "This provider has deleted their account."),

        ChatCounterpartyNotFoundException { CounterpartyType: ChatParticipantType.Provider } =>
            ApiResults.NotFound("ProviderNotFound", "Provider was not found."),

        ChatCounterpartyNotFoundException =>
            ApiResults.NotFound("PetParentNotFound", "Pet parent was not found."),

        ChatInvalidBlockException =>
            ApiResults.BadRequest("InvalidRequest", "A conversation runs between a provider and a pet parent."),

        // The legal hold on a reported thread. The message NAMES the holding ticket,
        // which is the point — "TK-000123 is open on this chat" is actionable in a way
        // "you cannot delete this" is not. It binds BOTH parties, not just the
        // reporter: the accused is the one with a motive to erase.
        ConversationUnderLegalHoldException hold =>
            ApiResults.Conflict("ConversationUnderLegalHold", hold.Message),

        ArgumentException argument =>
            ApiResults.BadRequest("InvalidRequest", argument.Message),

        // Anything else is genuinely unexpected — let GlobalExceptionHandler log
        // it and answer 500 rather than dressing it up as a client error.
        _ => throw exception
    };
}
