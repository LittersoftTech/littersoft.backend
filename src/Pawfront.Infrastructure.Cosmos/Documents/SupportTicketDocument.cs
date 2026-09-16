using System.Text.Json.Serialization;

namespace Pawfront.Infrastructure.Cosmos.Documents;

/// <summary>
/// A support ticket's narrative — what was said about it, by whom, in order.
///
/// Partitioned by <c>/ticketId</c>, with the document id set to the same value, so a
/// ticket's whole thread is a single point read. The SQL row in <c>Support.Tickets</c> is
/// the INDEX (parties, subject, status); this is the volume, and it is here for the reason
/// chat message bodies are: the clarification thread can grow without bound as support and
/// the creator go back and forth.
///
/// <b>Status is deliberately NOT duplicated here.</b> Three rules read it — the open-pair
/// uniqueness, the chat legal hold, and the account/pet delete refusals — and all three are
/// T-SQL predicates that cannot reach Cosmos. A copy would be a value that could silently
/// disagree with the one those rules use.
/// </summary>
public sealed class SupportTicketDocument
{
    public const string PartitionKeyPath = "/ticketId";

    /// <summary>The ticket id, as a string. Same value as <see cref="TicketId"/>.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("ticketId")]
    public string TicketId { get; set; } = string.Empty;

    /// <summary>
    /// The thread, oldest first. The opening entry is always the reporter's own
    /// <c>Report</c>; support's requests and the creator's replies follow.
    /// </summary>
    [JsonPropertyName("entries")]
    public List<SupportTicketEntryDocument> Entries { get; set; } = [];

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>One entry in a ticket's narrative.</summary>
public sealed class SupportTicketEntryDocument
{
    [JsonPropertyName("entryId")]
    public string EntryId { get; set; } = string.Empty;

    /// <summary>
    /// <c>Provider</c>, <c>PetParent</c> or <c>Support</c>. Support has no row in this
    /// database — the admin panel is not a party to the ticket — so its entries carry a
    /// null <see cref="AuthorId"/>.
    /// </summary>
    [JsonPropertyName("authorType")]
    public string AuthorType { get; set; } = string.Empty;

    [JsonPropertyName("authorId")]
    public string? AuthorId { get; set; }

    /// <summary>
    /// <c>Report</c>, <c>ClarificationRequest</c>, <c>ClarificationReply</c> or <c>Note</c>.
    /// Stored as the name, so adding a kind is additive.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }
}
