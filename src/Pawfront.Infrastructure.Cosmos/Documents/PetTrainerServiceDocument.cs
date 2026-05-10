using System.Text.Json.Serialization;

namespace Pawfront.Infrastructure.Cosmos.Documents;

public sealed class PetTrainerServiceDocument : ProviderServiceDocument
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

    [JsonPropertyName("trainingSchool")]
    public TrainingSchoolDetails? TrainingSchool { get; set; }

    [JsonPropertyName("freelance")]
    public FreelanceTrainerDetails? Freelance { get; set; }
}

public sealed class TrainingSchoolDetails
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
    public PetTrainerLicense? License { get; set; }

    [JsonPropertyName("offering")]
    public PetTrainerOffering? Offering { get; set; }
}

public sealed class FreelanceTrainerDetails
{
    [JsonPropertyName("aboutYou")]
    public string AboutYou { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("license")]
    public PetTrainerLicense? License { get; set; }

    [JsonPropertyName("offering")]
    public PetTrainerOffering? Offering { get; set; }
}

public sealed class PetTrainerLicense
{
    [JsonPropertyName("licenseNumber")]
    public string LicenseNumber { get; set; } = string.Empty;

    [JsonPropertyName("licenseType")]
    public string LicenseType { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string ImageUrl { get; set; } = string.Empty;
}

public sealed class PetTrainerOffering
{
    [JsonPropertyName("session")]
    public TrainingSession? Session { get; set; }

    [JsonPropertyName("petsTrained")]
    public List<string> PetsTrained { get; set; } = new();

    [JsonPropertyName("ageGroups")]
    public List<string> AgeGroups { get; set; } = new();

    [JsonPropertyName("temperaments")]
    public List<string> Temperaments { get; set; } = new();

    [JsonPropertyName("maxConcurrentSessions")]
    public int MaxConcurrentSessions { get; set; }

    [JsonPropertyName("serviceLocations")]
    public List<string> ServiceLocations { get; set; } = new();

    [JsonPropertyName("trainingApproaches")]
    public List<string> TrainingApproaches { get; set; } = new();

    [JsonPropertyName("previousExperience")]
    public List<string> PreviousExperience { get; set; } = new();

    [JsonPropertyName("privateTrainingDescription")]
    public string PrivateTrainingDescription { get; set; } = string.Empty;
}

public sealed class TrainingSession
{
    [JsonPropertyName("sessionDurationHours")]
    public decimal SessionDurationHours { get; set; }

    [JsonPropertyName("pricePerSession")]
    public decimal PricePerSession { get; set; }
}
