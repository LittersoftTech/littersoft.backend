using System.Text.Json.Serialization;

namespace Pawfront.Infrastructure.Cosmos.Documents;

public sealed class EventDocument
{
    public const string PartitionKeyPath = "/eventCategory";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    [JsonPropertyName("eventCategory")]
    public string EventCategory { get; set; } = string.Empty;

    [JsonPropertyName("physical")]
    public PhysicalEventDetails? Physical { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class PhysicalEventDetails
{
    [JsonPropertyName("maximumCapacity")]
    public int MaximumCapacity { get; set; }

    [JsonPropertyName("ticketing")]
    public EventTicketing Ticketing { get; set; } = new();
}

public sealed class EventTicketing
{
    [JsonPropertyName("isPaid")]
    public bool IsPaid { get; set; }

    [JsonPropertyName("price")]
    public decimal? Price { get; set; }
}
