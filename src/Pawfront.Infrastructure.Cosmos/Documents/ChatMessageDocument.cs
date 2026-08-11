using System.Text.Json.Serialization;

namespace Pawfront.Infrastructure.Cosmos.Documents;

/// <summary>
/// One chat message.
///
/// Partitioned by <c>/conversationId</c>, so a whole thread lives in one logical
/// partition and its history is a single-partition query however long it gets.
///
/// <b>The document id is the client-generated message id.</b> That is what makes
/// a retried send idempotent at no cost: a create with an existing id comes back
/// 409 rather than duplicating. Cosmos enforces uniqueness on nothing but
/// <c>id</c>, so this is the only way to get it without maintaining a second
/// index — and since ids are scoped to their partition, two conversations can
/// never collide.
/// </summary>
public sealed class ChatMessageDocument
{
    public const string PartitionKeyPath = "/conversationId";

    /// <summary>The message id. See the class remarks for why these are the same thing.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("conversationId")]
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>
    /// Assigned by <c>Chat.ReserveMessageSequence</c>. Orders the thread and is the paging
    /// cursor — ordering is by this and never by <see cref="CreatedAtUtc"/>,
    /// because two messages can share a millisecond but never a sequence.
    /// </summary>
    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    /// <summary><c>Provider</c> or <c>PetParent</c>.</summary>
    [JsonPropertyName("senderType")]
    public string SenderType { get; set; } = string.Empty;

    [JsonPropertyName("senderId")]
    public string SenderId { get; set; } = string.Empty;

    /// <summary><c>Text</c> or <c>Image</c>. Stored as the enum name, so adding a kind is additive.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    /// <summary>
    /// The message body, or an image's caption. Cleared when the sender retracts
    /// the message.
    /// </summary>
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("attachment")]
    public ChatAttachmentDetails? Attachment { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonPropertyName("editedAtUtc")]
    public DateTimeOffset? EditedAtUtc { get; set; }

    /// <summary>
    /// Set when the sender retracts the message. The document is NOT removed:
    /// deleting it would leave a hole in the sequence and rewrite what the other
    /// party saw. Same anonymise-rather-than-remove rule the account and pet
    /// deletes follow — the text goes, the row stays.
    /// </summary>
    [JsonPropertyName("deletedAtUtc")]
    public DateTimeOffset? DeletedAtUtc { get; set; }
}

public sealed class ChatAttachmentDetails
{
    /// <summary>
    /// A URL in the private blob container, so it is not directly fetchable —
    /// clients read the bytes through <c>POST /blob-images</c> like every other
    /// image in the product.
    /// </summary>
    [JsonPropertyName("blobUrl")]
    public string BlobUrl { get; set; } = string.Empty;

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    /// <summary>Optional, so the client can reserve the right space before the image loads.</summary>
    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }
}
