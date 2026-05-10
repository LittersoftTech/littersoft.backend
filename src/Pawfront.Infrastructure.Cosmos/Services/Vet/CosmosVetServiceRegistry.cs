using System.Net;
using Microsoft.Azure.Cosmos;
using Pawfront.Application.Services.Vet;
using Pawfront.Domain.Services;
using Pawfront.Infrastructure.Cosmos.Documents;
using Pawfront.Infrastructure.Cosmos.ProviderServices;

namespace Pawfront.Infrastructure.Cosmos.Services.Vet;

internal sealed class CosmosVetServiceRegistry(
    IProviderServicesContainerAccessor containerAccessor) : IVetServiceRegistry
{
    private const int FreelanceMaxConcurrentConsultations = 1;

    private static readonly string ServiceCategory = ProviderServiceCategory.Vet.ToString();

    private static readonly IReadOnlySet<string> AllowedAnimals = new HashSet<string>(StringComparer.Ordinal)
    {
        "Dog", "Cat", "Bird", "Rabbit", "Hamster", "GuineaPig", "Reptile", "Fish", "Other"
    };

    private static readonly IReadOnlySet<string> AllowedServiceLocations = new HashSet<string>(StringComparer.Ordinal)
    {
        "VetClinic", "CustomerLocation", "Both"
    };

    public async Task<VetServiceResult> RegisterVetClinicAsync(
        RegisterVetClinicCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        var existingClinic = existing?.SubCategory == VetSubCategories.VetClinic ? existing.VetClinic : null;

        var document = new VetServiceDocument
        {
            Id = command.ProviderId.ToString(),
            ProviderId = command.ProviderId.ToString(),
            ServiceCategory = ServiceCategory,
            SubCategory = VetSubCategories.VetClinic,
            Address = Required(command.Address, nameof(command.Address)),
            Zip = Required(command.Zip, nameof(command.Zip)),
            City = Required(command.City, nameof(command.City)),
            Website = Trim(command.Website),
            VetClinic = new VetClinicDetails
            {
                Name = Required(command.ClinicName, nameof(command.ClinicName)),
                TelephoneCountryCode = Required(command.TelephoneCountryCode, nameof(command.TelephoneCountryCode)),
                TelephoneNumber = Required(command.TelephoneNumber, nameof(command.TelephoneNumber)),
                Email = Required(command.Email, nameof(command.Email)),
                Description = Required(command.Description, nameof(command.Description)),
                ImageUrl = Trim(command.ClinicImageUrl),
                Certificate = existingClinic?.Certificate,
                Offering = existingClinic?.Offering
            },
            Freelance = null,
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now
        };

        await container.UpsertItemAsync(
            document,
            new PartitionKey(ServiceCategory),
            cancellationToken: cancellationToken);

        return ToResult(document);
    }

    public async Task<VetServiceResult> RegisterFreelanceVeterinarianAsync(
        RegisterFreelanceVeterinarianCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        var existingFreelance = existing?.SubCategory == VetSubCategories.Freelance ? existing.Freelance : null;

        var document = new VetServiceDocument
        {
            Id = command.ProviderId.ToString(),
            ProviderId = command.ProviderId.ToString(),
            ServiceCategory = ServiceCategory,
            SubCategory = VetSubCategories.Freelance,
            Address = Required(command.Address, nameof(command.Address)),
            Zip = Required(command.Zip, nameof(command.Zip)),
            City = Required(command.City, nameof(command.City)),
            Website = Trim(command.Website),
            VetClinic = null,
            Freelance = new FreelanceVeterinarianDetails
            {
                AboutYou = Required(command.AboutYou, nameof(command.AboutYou)),
                ImageUrl = Trim(command.ProfileImageUrl),
                Certificate = existingFreelance?.Certificate,
                Offering = existingFreelance?.Offering
            },
            CreatedAtUtc = existing?.CreatedAtUtc ?? now,
            UpdatedAtUtc = now
        };

        await container.UpsertItemAsync(
            document,
            new PartitionKey(ServiceCategory),
            cancellationToken: cancellationToken);

        return ToResult(document);
    }

    public async Task<VetServiceResult> SaveVetClinicOfferingAsync(
        SaveVetClinicOfferingCommand command,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        if (existing is null
            || existing.SubCategory != VetSubCategories.VetClinic
            || existing.VetClinic is null)
        {
            throw new VetClinicNotRegisteredException(command.ProviderId);
        }

        if (command.MaxConcurrentConsultations is < 1 or > 20)
        {
            throw new ArgumentException(
                "MaxConcurrentConsultations must be between 1 and 20.",
                nameof(command.MaxConcurrentConsultations));
        }

        var (certificate, offering) = BuildCertificateAndOffering(
            command.CertificateImageUrl,
            command.Appointment,
            command.AnimalsTreated,
            command.MaxConcurrentConsultations,
            command.ServiceLocation);

        existing.VetClinic.Certificate = certificate;
        existing.VetClinic.Offering = offering;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await container.UpsertItemAsync(
            existing,
            new PartitionKey(ServiceCategory),
            cancellationToken: cancellationToken);

        return ToResult(existing);
    }

    public async Task<VetServiceResult> SaveFreelanceVeterinarianOfferingAsync(
        SaveFreelanceVeterinarianOfferingCommand command,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        if (existing is null
            || existing.SubCategory != VetSubCategories.Freelance
            || existing.Freelance is null)
        {
            throw new FreelanceVeterinarianNotRegisteredException(command.ProviderId);
        }

        var (certificate, offering) = BuildCertificateAndOffering(
            command.CertificateImageUrl,
            command.Appointment,
            command.AnimalsTreated,
            FreelanceMaxConcurrentConsultations,
            command.ServiceLocation);

        existing.Freelance.Certificate = certificate;
        existing.Freelance.Offering = offering;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await container.UpsertItemAsync(
            existing,
            new PartitionKey(ServiceCategory),
            cancellationToken: cancellationToken);

        return ToResult(existing);
    }

    public async Task<VetServiceResult?> GetAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var document = await TryReadAsync(container, providerId, cancellationToken);
        return document is null ? null : ToResult(document);
    }

    private static (VetCertificate Certificate, VetOffering Offering) BuildCertificateAndOffering(
        string certificateImageUrl,
        VetAppointmentInput appointment,
        IReadOnlyCollection<string> animalsTreated,
        int maxConcurrentConsultations,
        string serviceLocation)
    {
        if (appointment is null)
        {
            throw new ArgumentException("Appointment details are required.", nameof(appointment));
        }

        var animals = NormalizeSet(animalsTreated, AllowedAnimals, nameof(animalsTreated));
        var location = NormalizeOne(serviceLocation, AllowedServiceLocations, nameof(serviceLocation));

        var certificate = new VetCertificate
        {
            ImageUrl = Required(certificateImageUrl, nameof(certificateImageUrl))
        };

        var offering = new VetOffering
        {
            Appointment = ToVetAppointment(appointment, nameof(appointment)),
            AnimalsTreated = animals,
            MaxConcurrentConsultations = maxConcurrentConsultations,
            ServiceLocation = location
        };

        return (certificate, offering);
    }

    private static VetAppointment ToVetAppointment(VetAppointmentInput input, string fieldName)
    {
        if (input.AppointmentDurationHours <= 0)
        {
            throw new ArgumentException($"{fieldName}.AppointmentDurationHours must be greater than zero.", fieldName);
        }

        if (input.PricePerAppointment < 0)
        {
            throw new ArgumentException($"{fieldName}.PricePerAppointment must be non-negative.", fieldName);
        }

        return new VetAppointment
        {
            AppointmentDurationHours = input.AppointmentDurationHours,
            PricePerAppointment = input.PricePerAppointment
        };
    }

    private static List<string> NormalizeSet(
        IReadOnlyCollection<string>? values,
        IReadOnlySet<string> allowed,
        string fieldName)
    {
        if (values is null)
        {
            throw new ArgumentException($"{fieldName} is required.", fieldName);
        }

        var deduped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in values)
        {
            var trimmed = raw?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            if (!allowed.Contains(trimmed))
            {
                throw new ArgumentException($"{fieldName} contains unsupported value '{trimmed}'.", fieldName);
            }

            if (seen.Add(trimmed))
            {
                deduped.Add(trimmed);
            }
        }

        if (deduped.Count == 0)
        {
            throw new ArgumentException($"{fieldName} requires at least one value.", fieldName);
        }

        return deduped;
    }

    private static string NormalizeOne(
        string? value,
        IReadOnlySet<string> allowed,
        string fieldName)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new ArgumentException($"{fieldName} is required.", fieldName);
        }

        if (!allowed.Contains(trimmed))
        {
            throw new ArgumentException($"{fieldName} value '{trimmed}' is not supported.", fieldName);
        }

        return trimmed;
    }

    private static async Task<VetServiceDocument?> TryReadAsync(
        Container container,
        Guid providerId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.ReadItemAsync<VetServiceDocument>(
                providerId.ToString(),
                new PartitionKey(ServiceCategory),
                cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static VetServiceResult ToResult(VetServiceDocument document)
    {
        return new VetServiceResult(
            ProviderId: Guid.Parse(document.ProviderId),
            SubCategory: document.SubCategory,
            Address: document.Address,
            Zip: document.Zip,
            City: document.City,
            Website: document.Website,
            VetClinic: document.VetClinic is null
                ? null
                : new VetClinicResult(
                    document.VetClinic.Name,
                    document.VetClinic.TelephoneCountryCode,
                    document.VetClinic.TelephoneNumber,
                    document.VetClinic.Email,
                    document.VetClinic.Description,
                    document.VetClinic.ImageUrl,
                    ToCertificateResult(document.VetClinic.Certificate),
                    ToOfferingResult(document.VetClinic.Offering)),
            Freelance: document.Freelance is null
                ? null
                : new FreelanceVeterinarianResult(
                    document.Freelance.AboutYou,
                    document.Freelance.ImageUrl,
                    ToCertificateResult(document.Freelance.Certificate),
                    ToOfferingResult(document.Freelance.Offering)),
            CreatedAtUtc: document.CreatedAtUtc,
            UpdatedAtUtc: document.UpdatedAtUtc);
    }

    private static VetCertificateResult? ToCertificateResult(VetCertificate? certificate)
    {
        return certificate is null
            ? null
            : new VetCertificateResult(certificate.ImageUrl);
    }

    private static VetOfferingResult? ToOfferingResult(VetOffering? offering)
    {
        return offering is null
            ? null
            : new VetOfferingResult(
                ToAppointmentResult(offering.Appointment),
                offering.AnimalsTreated.ToArray(),
                offering.MaxConcurrentConsultations,
                offering.ServiceLocation);
    }

    private static VetAppointmentResult? ToAppointmentResult(VetAppointment? appointment)
    {
        return appointment is null
            ? null
            : new VetAppointmentResult(
                appointment.AppointmentDurationHours,
                appointment.PricePerAppointment);
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} is required.", name);
        }

        return value.Trim();
    }

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static class VetSubCategories
    {
        public const string VetClinic = "VetClinic";
        public const string Freelance = "FreelanceVeterinarian";
    }
}
