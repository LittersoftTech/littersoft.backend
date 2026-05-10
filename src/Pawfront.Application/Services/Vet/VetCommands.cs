namespace Pawfront.Application.Services.Vet;

public sealed record RegisterVetClinicCommand(
    Guid ProviderId,
    string ClinicName,
    string Address,
    string Zip,
    string City,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string? Website,
    string Description,
    string? ClinicImageUrl);

public sealed record RegisterFreelanceVeterinarianCommand(
    Guid ProviderId,
    string Address,
    string Zip,
    string City,
    string? Website,
    string AboutYou,
    string? ProfileImageUrl);

public sealed record SaveVetClinicOfferingCommand(
    Guid ProviderId,
    string CertificateImageUrl,
    VetAppointmentInput Appointment,
    IReadOnlyCollection<string> AnimalsTreated,
    int MaxConcurrentConsultations,
    string ServiceLocation);

public sealed record SaveFreelanceVeterinarianOfferingCommand(
    Guid ProviderId,
    string CertificateImageUrl,
    VetAppointmentInput Appointment,
    IReadOnlyCollection<string> AnimalsTreated,
    string ServiceLocation);

public sealed record VetAppointmentInput(
    decimal AppointmentDurationHours,
    decimal PricePerAppointment);

public sealed record VetServiceResult(
    Guid ProviderId,
    string SubCategory,
    string Address,
    string Zip,
    string City,
    string? Website,
    VetClinicResult? VetClinic,
    FreelanceVeterinarianResult? Freelance,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record VetClinicResult(
    string Name,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string Description,
    string? ImageUrl,
    VetCertificateResult? Certificate,
    VetOfferingResult? Offering);

public sealed record FreelanceVeterinarianResult(
    string AboutYou,
    string? ImageUrl,
    VetCertificateResult? Certificate,
    VetOfferingResult? Offering);

public sealed record VetCertificateResult(
    string ImageUrl);

public sealed record VetOfferingResult(
    VetAppointmentResult? Appointment,
    IReadOnlyCollection<string> AnimalsTreated,
    int MaxConcurrentConsultations,
    string ServiceLocation);

public sealed record VetAppointmentResult(
    decimal AppointmentDurationHours,
    decimal PricePerAppointment);

public sealed class VetClinicNotRegisteredException(Guid providerId)
    : Exception($"Vet Clinic registration was not found for provider '{providerId}'. Complete the basic Vet Clinic registration first.");

public sealed class FreelanceVeterinarianNotRegisteredException(Guid providerId)
    : Exception($"Freelance Veterinarian registration was not found for provider '{providerId}'. Complete the basic Freelance Veterinarian registration first.");
