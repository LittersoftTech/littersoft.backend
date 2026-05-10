namespace Pawfront.Contracts.Services.Vet;

public sealed record RegisterVetClinicRequest(
    string ClinicName,
    string Address,
    string Zip,
    string City,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string? Website,
    string Description,
    string? ClinicImageUrl,
    decimal Latitude,
    decimal Longitude);

public sealed record RegisterFreelanceVeterinarianRequest(
    string Address,
    string Zip,
    string City,
    string? Website,
    string AboutYou,
    string? ProfileImageUrl,
    decimal Latitude,
    decimal Longitude);

public sealed record SaveVetClinicOfferingRequest(
    string CertificateImageUrl,
    VetAppointmentRequest Appointment,
    IReadOnlyCollection<string> AnimalsTreated,
    int MaxConcurrentConsultations,
    string ServiceLocation);

public sealed record SaveFreelanceVeterinarianOfferingRequest(
    string CertificateImageUrl,
    VetAppointmentRequest Appointment,
    IReadOnlyCollection<string> AnimalsTreated,
    string ServiceLocation);

public sealed record VetAppointmentRequest(
    decimal AppointmentDurationHours,
    decimal PricePerAppointment);

public sealed record VetServiceResponse(
    Guid ProviderId,
    string SubCategory,
    string Address,
    string Zip,
    string City,
    string? Website,
    VetClinicResponse? VetClinic,
    FreelanceVeterinarianResponse? Freelance,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record VetClinicResponse(
    string Name,
    string TelephoneCountryCode,
    string TelephoneNumber,
    string Email,
    string Description,
    string? ImageUrl,
    VetCertificateResponse? Certificate,
    VetOfferingResponse? Offering);

public sealed record FreelanceVeterinarianResponse(
    string AboutYou,
    string? ImageUrl,
    VetCertificateResponse? Certificate,
    VetOfferingResponse? Offering);

public sealed record VetCertificateResponse(
    string ImageUrl);

public sealed record VetOfferingResponse(
    VetAppointmentResponse? Appointment,
    IReadOnlyCollection<string> AnimalsTreated,
    int MaxConcurrentConsultations,
    string ServiceLocation);

public sealed record VetAppointmentResponse(
    decimal AppointmentDurationHours,
    decimal PricePerAppointment);
