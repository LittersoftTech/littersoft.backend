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

    // The venue address. Nullable for legacy physical docs created before
    // location capture; always set for events created since.
    [JsonPropertyName("location")]
    public EventLocationDetails? Location { get; set; }

    // Ticketing (isPaid / price) moved to the SQL Event.Events row so it's
    // returned for every event type, including online events that have no
    // Cosmos document. Legacy docs may still carry a "ticketing" property; it
    // is simply ignored on read.
}

public sealed class EventLocationDetails
{
    [JsonPropertyName("houseNumber")]
    public string HouseNumber { get; set; } = string.Empty;

    [JsonPropertyName("street")]
    public string Street { get; set; } = string.Empty;

    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;

    [JsonPropertyName("zip")]
    public string Zip { get; set; } = string.Empty;

    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }
}
