using System.Text.Json.Serialization;

namespace Pawfront.Infrastructure.Cosmos.Documents;

public class ProviderServiceDocument
{
    public const string PartitionKeyPath = "/serviceCategory";

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("providerId")]
    public string ProviderId { get; set; } = string.Empty;

    [JsonPropertyName("serviceCategory")]
    public string ServiceCategory { get; set; } = string.Empty;

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonPropertyName("updatedAtUtc")]
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
