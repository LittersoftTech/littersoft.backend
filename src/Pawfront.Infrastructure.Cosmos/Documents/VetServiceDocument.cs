using System.Text.Json.Serialization;

namespace Pawfront.Infrastructure.Cosmos.Documents;

public sealed class VetServiceDocument : ProviderServiceDocument
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

    [JsonPropertyName("vetClinic")]
    public VetClinicDetails? VetClinic { get; set; }

    [JsonPropertyName("freelance")]
    public FreelanceVeterinarianDetails? Freelance { get; set; }
}

public sealed class VetClinicDetails
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

    [JsonPropertyName("certificate")]
    public VetCertificate? Certificate { get; set; }

    [JsonPropertyName("offering")]
    public VetOffering? Offering { get; set; }
}

public sealed class FreelanceVeterinarianDetails
{
    [JsonPropertyName("aboutYou")]
    public string AboutYou { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("certificate")]
    public VetCertificate? Certificate { get; set; }

    [JsonPropertyName("offering")]
    public VetOffering? Offering { get; set; }
}

public sealed class VetCertificate
{
    [JsonPropertyName("imageUrl")]
    public string ImageUrl { get; set; } = string.Empty;
}

public sealed class VetOffering
{
    [JsonPropertyName("appointment")]
    public VetAppointment? Appointment { get; set; }

    [JsonPropertyName("animalsTreated")]
    public List<string> AnimalsTreated { get; set; } = new();

    [JsonPropertyName("maxConcurrentConsultations")]
    public int MaxConcurrentConsultations { get; set; }

    [JsonPropertyName("serviceLocation")]
    public string ServiceLocation { get; set; } = string.Empty;
}

public sealed class VetAppointment
{
    [JsonPropertyName("appointmentDurationHours")]
    public decimal AppointmentDurationHours { get; set; }

    [JsonPropertyName("pricePerAppointment")]
    public decimal PricePerAppointment { get; set; }
}
