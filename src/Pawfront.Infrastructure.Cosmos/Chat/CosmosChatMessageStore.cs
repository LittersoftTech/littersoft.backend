using System.Net;
using Microsoft.Azure.Cosmos;
using Pawfront.Application.Chat;
using Pawfront.Infrastructure.Cosmos.Documents;

namespace Pawfront.Infrastructure.Cosmos.Chat;

/// <summary>
/// Message bodies in the ChatMessages container. Every operation here is
/// single-partition — that is what partitioning by conversation buys.
/// </summary>
internal sealed class CosmosChatMessageStore(
    IChatMessagesContainerAccessor containerAccessor) : IChatMessageStore
{
    public async Task<ChatMessage?> TryGetAsync(
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var document = await TryReadAsync(container, conversationId, messageId, cancellationToken);

        return document is null ? null : ToMessage(document);
    }

    public async Task<ChatMessage> CreateAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var document = ToDocument(message);

        try
        {
            var response = await container.CreateItemAsync(
                document,
                new PartitionKey(document.ConversationId),
                cancellationToken: cancellationToken);

            return ToMessage(response.Resource);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            // A concurrent duplicate that slipped past the caller's pre-check.
            // The FIRST write wins deliberately: its sequence is the one
            // Chat.ReserveMessageSequence assigned it, so overwriting it
            // would leave the stored message disagreeing with the thread's cache.
            var existing = await TryReadAsync(
                container, message.ConversationId, message.MessageId, cancellationToken);

            return existing is null
                // A 409 whose document then cannot be read means the id collided
                // with something outside this partition, which cannot happen —
                // surfacing it beats returning a message we did not store.
                ? throw new InvalidOperationException(
                    $"Message '{message.MessageId}' conflicted on create but could not be read back.")
                : ToMessage(existing);
        }
    }

    public async Task<ChatMessagePage> ListAsync(
        Guid conversationId,
        long? beforeSequence,
        long afterSequence,
        int take,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);

        // Ordered by sequence, never by timestamp: two messages can share a
        // millisecond, so a timestamp cursor could skip or repeat one.
        // Over-fetch by one to learn whether another page exists without a
        // second query or a count.
        //
        // The cursor predicate is APPENDED rather than written as
        // "(@beforeSequence IS NULL OR ...)". Cosmos NoSQL has no IS NULL
        // operator — only the IS_NULL() function — so that form is a syntax error
        // (SC1001) the service rejects with 400 before it ever looks at the
        // parameter. It therefore failed every history read, first page included,
        // which is why the whole endpoint 500'd whatever the client sent.
        // Building the clause only when there is a cursor also keeps the first
        // page's plan a plain ranged scan.
        var cursorClause = beforeSequence.HasValue
            ? "AND c.sequence < @beforeSequence "
            : string.Empty;

        // The reader's own "delete chat" watermark, appended for the same reason
        // and in the same way as the cursor: built only when it bites, so the
        // ordinary read (afterSequence 0 — nobody has cleared anything) keeps a
        // plain ranged plan, and never written with an IS NULL comparison, which
        // Cosmos NoSQL has no operator for.
        //
        // It belongs in the QUERY, not in a filter over the results: removing
        // rows afterwards would return short pages and hand back a cursor that
        // walks down through cleared history one empty page at a time.
        var clearedClause = afterSequence > 0
            ? "AND c.sequence > @afterSequence "
            : string.Empty;

        var query = new QueryDefinition(
                "SELECT TOP @take c.id, c.conversationId, c.sequence, c.senderType, c.senderId, " +
                "c.kind, c.text, c.attachment, c.createdAtUtc, c.editedAtUtc, c.deletedAtUtc " +
                "FROM c WHERE c.conversationId = @conversationId " +
                cursorClause +
                clearedClause +
                "ORDER BY c.sequence DESC")
            .WithParameter("@take", take + 1)
            .WithParameter("@conversationId", conversationId.ToString());

        if (beforeSequence.HasValue)
        {
            query = query.WithParameter("@beforeSequence", beforeSequence.Value);
        }

        if (afterSequence > 0)
        {
            query = query.WithParameter("@afterSequence", afterSequence);
        }

        using var iterator = container.GetItemQueryIterator<ChatMessageDocument>(
            query,
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(conversationId.ToString()),
                MaxItemCount = take + 1
            });

        var documents = new List<ChatMessageDocument>(take + 1);
        while (iterator.HasMoreResults && documents.Count <= take)
        {
            foreach (var document in await iterator.ReadNextAsync(cancellationToken))
            {
                documents.Add(document);
            }
        }

        var hasMore = documents.Count > take;
        if (hasMore)
        {
            documents.RemoveRange(take, documents.Count - take);
        }

        var messages = documents.Select(ToMessage).ToList();

        return new ChatMessagePage(
            messages,
            // The cursor for the next (older) page. Returned rather than left to
            // the client to derive, so paging cannot drift from the ordering rule.
            NextBeforeSequence: hasMore && messages.Count > 0 ? messages[^1].Sequence : null,
            HasMore: hasMore);
    }

    public async Task<ChatMessage?> SoftDeleteAsync(
        Guid conversationId,
        Guid messageId,
        ChatParticipant sender,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var document = await TryReadAsync(container, conversationId, messageId, cancellationToken);

        if (document is null
            || !string.Equals(document.SenderId, sender.Id.ToString(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(document.SenderType, sender.Type.ToSqlValue(), StringComparison.Ordinal))
        {
            // "Not yours" and "no such message" collapse to one answer, so a
            // message id cannot be probed for existence.
            return null;
        }

        if (document.DeletedAtUtc is not null)
        {
            // Already retracted — idempotent, so a double-tap is harmless.
            return ToMessage(document);
        }

        // The document survives with its content cleared. Removing it would leave
        // a hole in the sequence and silently rewrite what the other party saw.
        document.Text = null;
        document.Attachment = null;
        document.DeletedAtUtc = DateTimeOffset.UtcNow;

        var response = await container.ReplaceItemAsync(
            document,
            document.Id,
            new PartitionKey(document.ConversationId),
            cancellationToken: cancellationToken);

        return ToMessage(response.Resource);
    }

    private static async Task<ChatMessageDocument?> TryReadAsync(
        Container container,
        Guid conversationId,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await container.ReadItemAsync<ChatMessageDocument>(
                messageId.ToString(),
                new PartitionKey(conversationId.ToString()),
                cancellationToken: cancellationToken);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static ChatMessageDocument ToDocument(ChatMessage message) => new()
    {
        // The message id IS the document id — see ChatMessageDocument for why.
        Id = message.MessageId.ToString(),
        ConversationId = message.ConversationId.ToString(),
        Sequence = message.Sequence,
        SenderType = message.SenderType.ToSqlValue(),
        SenderId = message.SenderId.ToString(),
        Kind = message.Kind.ToWireValue(),
        Text = message.Text,
        Attachment = message.Attachment is null ? null : new ChatAttachmentDetails
        {
            BlobUrl = message.Attachment.BlobUrl,
            ContentType = message.Attachment.ContentType,
            SizeBytes = message.Attachment.SizeBytes,
            Width = message.Attachment.Width,
            Height = message.Attachment.Height
        },
        CreatedAtUtc = message.CreatedAtUtc,
        EditedAtUtc = message.EditedAtUtc,
        DeletedAtUtc = message.DeletedAtUtc
    };

    private static ChatMessage ToMessage(ChatMessageDocument document) => new(
        MessageId: Guid.TryParse(document.Id, out var messageId) ? messageId : Guid.Empty,
        ConversationId: Guid.TryParse(document.ConversationId, out var conversationId)
            ? conversationId
            : Guid.Empty,
        Sequence: document.Sequence,
        SenderType: ChatParticipantTypes.FromSqlValue(document.SenderType),
        SenderId: Guid.TryParse(document.SenderId, out var senderId) ? senderId : Guid.Empty,
        Kind: ChatMessageKinds.FromWireValue(document.Kind),
        Text: document.Text,
        Attachment: document.Attachment is null ? null : new ChatAttachment(
            document.Attachment.BlobUrl,
            document.Attachment.ContentType,
            document.Attachment.SizeBytes,
            document.Attachment.Width,
            document.Attachment.Height),
        CreatedAtUtc: document.CreatedAtUtc,
        EditedAtUtc: document.EditedAtUtc,
        DeletedAtUtc: document.DeletedAtUtc);
}
