using System.Text.Json.Serialization;

namespace Pawfront.Infrastructure.Cosmos.Documents;

public sealed class PetGroomerServiceDocument : ProviderServiceDocument
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

    [JsonPropertyName("groomerShop")]
    public GroomerShopDetails? GroomerShop { get; set; }

    [JsonPropertyName("freelance")]
    public FreelanceGroomerDetails? Freelance { get; set; }
}

public sealed class GroomerShopDetails
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

    [JsonPropertyName("license")]
    public PetGroomerLicense? License { get; set; }

    [JsonPropertyName("offering")]
    public PetGroomerOffering? Offering { get; set; }
}

public sealed class FreelanceGroomerDetails
{
    [JsonPropertyName("aboutYou")]
    public string AboutYou { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("license")]
    public PetGroomerLicense? License { get; set; }

    [JsonPropertyName("offering")]
    public PetGroomerOffering? Offering { get; set; }
}

public sealed class PetGroomerLicense
{
    [JsonPropertyName("licenseNumber")]
    public string LicenseNumber { get; set; } = string.Empty;

    [JsonPropertyName("licenseType")]
    public string LicenseType { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string ImageUrl { get; set; } = string.Empty;
}

public sealed class PetGroomerOffering
{
    [JsonPropertyName("session")]
    public GroomingOffering? Session { get; set; }

    [JsonPropertyName("animalsHandled")]
    public List<string> AnimalsHandled { get; set; } = new();

    [JsonPropertyName("maxPetsAtOneTime")]
    public int MaxPetsAtOneTime { get; set; }

    [JsonPropertyName("dogTemperaments")]
    public List<string> DogTemperaments { get; set; } = new();

    [JsonPropertyName("serviceLocation")]
    public string ServiceLocation { get; set; } = string.Empty;
}

public sealed class GroomingOffering
{
    /// <summary>
    /// The provider's menu of grooming services, one entry per service they offer.
    /// Each item has a stable canonical <see cref="GroomingServiceItem.Code"/>
    /// (from <c>GroomingServiceCatalog</c>) plus per-groomer price + duration +
    /// active flag. Capacity (`maxPetsAtOneTime` on the parent offering) is the
    /// shop-wide bucket — all grooming bookings on this provider compete for the
    /// same slot capacity.
    /// </summary>
    [JsonPropertyName("services")]
    public List<GroomingServiceItem> Services { get; set; } = new();

    [JsonPropertyName("addOns")]
    public List<string> AddOns { get; set; } = new();

    [JsonPropertyName("latePickupCharges")]
    public decimal LatePickupCharges { get; set; }

    [JsonPropertyName("dropOffTime")]
    public TimeOnly DropOffTime { get; set; }

    [JsonPropertyName("pickUpTime")]
    public TimeOnly PickUpTime { get; set; }
}

public sealed class GroomingServiceItem
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("durationMinutes")]
    public int DurationMinutes { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;
}
