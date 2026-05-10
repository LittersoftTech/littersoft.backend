using System.Text.Json.Serialization;

namespace Pawfront.Infrastructure.Cosmos.Documents;

public sealed class PetAdoptionSaleServiceDocument : ProviderServiceDocument
{
    [JsonPropertyName("subCategory")]
    public string SubCategory { get; set; } = string.Empty;

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("zip")]
    public string Zip { get; set; } = string.Empty;

    [JsonPropertyName("city")]
    public string City { get; set; } = string.Empty;

    [JsonPropertyName("website")]
    public string? Website { get; set; }

    [JsonPropertyName("petShelter")]
    public PetShelterDetails? PetShelter { get; set; }

    [JsonPropertyName("petShop")]
    public PetShopDetails? PetShop { get; set; }

    [JsonPropertyName("freelance")]
    public FreelancePetAdoptionSaleDetails? Freelance { get; set; }
}

public sealed class PetShelterDetails
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("telephoneCountryCode")]
    public string TelephoneCountryCode { get; set; } = string.Empty;

    [JsonPropertyName("telephoneNumber")]
    public string TelephoneNumber { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }
}

public sealed class PetShopDetails
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("telephoneCountryCode")]
    public string TelephoneCountryCode { get; set; } = string.Empty;

    [JsonPropertyName("telephoneNumber")]
    public string TelephoneNumber { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }
}

public sealed class FreelancePetAdoptionSaleDetails
{
    [JsonPropertyName("aboutYou")]
    public string AboutYou { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }
}
