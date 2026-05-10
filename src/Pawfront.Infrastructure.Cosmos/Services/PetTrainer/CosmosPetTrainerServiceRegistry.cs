using System.Net;
using Microsoft.Azure.Cosmos;
using Pawfront.Application.Services.PetTrainer;
using Pawfront.Domain.Services;
using Pawfront.Infrastructure.Cosmos.Documents;
using Pawfront.Infrastructure.Cosmos.ProviderServices;

namespace Pawfront.Infrastructure.Cosmos.Services.PetTrainer;

internal sealed class CosmosPetTrainerServiceRegistry(
    IProviderServicesContainerAccessor containerAccessor) : IPetTrainerServiceRegistry
{
    private static readonly string ServiceCategory = ProviderServiceCategory.PetTrainer.ToString();

    private static readonly IReadOnlySet<string> AllowedPets = new HashSet<string>(StringComparer.Ordinal)
    {
        "Dog", "Cat"
    };

    private static readonly IReadOnlySet<string> AllowedAgeGroups = new HashSet<string>(StringComparer.Ordinal)
    {
        "Puppy", "Adolescent", "Adult", "Senior"
    };

    private static readonly IReadOnlySet<string> AllowedTemperaments = new HashSet<string>(StringComparer.Ordinal)
    {
        "Calm", "Energetic", "Anxious", "Sensitive", "Aggressive", "AllTemperaments"
    };

    private static readonly IReadOnlySet<string> AllowedServiceLocations = new HashSet<string>(StringComparer.Ordinal)
    {
        "CustomerLocation", "TrainerLocation", "NatureOrParks", "UrbanOrCity", "TrainingSchool"
    };

    public async Task<PetTrainerServiceResult> RegisterTrainingSchoolAsync(
        RegisterTrainingSchoolCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        var existingSchool = existing?.SubCategory == PetTrainerSubCategories.TrainingSchool ? existing.TrainingSchool : null;

        var document = new PetTrainerServiceDocument
        {
            Id = command.ProviderId.ToString(),
            ProviderId = command.ProviderId.ToString(),
            ServiceCategory = ServiceCategory,
            SubCategory = PetTrainerSubCategories.TrainingSchool,
            Address = Required(command.Address, nameof(command.Address)),
            Zip = Required(command.Zip, nameof(command.Zip)),
            City = Required(command.City, nameof(command.City)),
            Website = Trim(command.Website),
            TrainingSchool = new TrainingSchoolDetails
            {
                Name = Required(command.TrainingSchoolName, nameof(command.TrainingSchoolName)),
                TelephoneCountryCode = Required(command.TelephoneCountryCode, nameof(command.TelephoneCountryCode)),
                TelephoneNumber = Required(command.TelephoneNumber, nameof(command.TelephoneNumber)),
                Email = Required(command.Email, nameof(command.Email)),
                Description = Required(command.Description, nameof(command.Description)),
                ImageUrl = Trim(command.SchoolImageUrl),
                License = existingSchool?.License,
                Offering = existingSchool?.Offering
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

    public async Task<PetTrainerServiceResult> RegisterFreelanceTrainerAsync(
        RegisterFreelanceTrainerCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        var existingFreelance = existing?.SubCategory == PetTrainerSubCategories.Freelance ? existing.Freelance : null;

        var document = new PetTrainerServiceDocument
        {
            Id = command.ProviderId.ToString(),
            ProviderId = command.ProviderId.ToString(),
            ServiceCategory = ServiceCategory,
            SubCategory = PetTrainerSubCategories.Freelance,
            Address = Required(command.Address, nameof(command.Address)),
            Zip = Required(command.Zip, nameof(command.Zip)),
            City = Required(command.City, nameof(command.City)),
            Website = Trim(command.Website),
            TrainingSchool = null,
            Freelance = new FreelanceTrainerDetails
            {
                AboutYou = Required(command.AboutYou, nameof(command.AboutYou)),
                ImageUrl = Trim(command.ProfileImageUrl),
                License = existingFreelance?.License,
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

    public async Task<PetTrainerServiceResult> SaveTrainingSchoolOfferingAsync(
        SaveTrainingSchoolOfferingCommand command,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        if (existing is null
            || existing.SubCategory != PetTrainerSubCategories.TrainingSchool
            || existing.TrainingSchool is null)
        {
            throw new TrainingSchoolNotRegisteredException(command.ProviderId);
        }

        var (license, offering) = BuildLicenseAndOffering(
            command.LicenseNumber,
            command.LicenseType,
            command.LicenseImageUrl,
            command.Session,
            command.PetsTrained,
            command.AgeGroups,
            command.Temperaments,
            command.MaxConcurrentSessions,
            command.ServiceLocations,
            command.TrainingApproaches,
            command.PreviousExperience,
            command.PrivateTrainingDescription);

        existing.TrainingSchool.License = license;
        existing.TrainingSchool.Offering = offering;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await container.UpsertItemAsync(
            existing,
            new PartitionKey(ServiceCategory),
            cancellationToken: cancellationToken);

        return ToResult(existing);
    }

    public async Task<PetTrainerServiceResult> SaveFreelanceTrainerOfferingAsync(
        SaveFreelanceTrainerOfferingCommand command,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        if (existing is null
            || existing.SubCategory != PetTrainerSubCategories.Freelance
            || existing.Freelance is null)
        {
            throw new FreelanceTrainerNotRegisteredException(command.ProviderId);
        }

        var (license, offering) = BuildLicenseAndOffering(
            command.LicenseNumber,
            command.LicenseType,
            command.LicenseImageUrl,
            command.Session,
            command.PetsTrained,
            command.AgeGroups,
            command.Temperaments,
            command.MaxConcurrentSessions,
            command.ServiceLocations,
            command.TrainingApproaches,
            command.PreviousExperience,
            command.PrivateTrainingDescription);

        existing.Freelance.License = license;
        existing.Freelance.Offering = offering;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await container.UpsertItemAsync(
            existing,
            new PartitionKey(ServiceCategory),
            cancellationToken: cancellationToken);

        return ToResult(existing);
    }

    public async Task<PetTrainerServiceResult?> GetAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var document = await TryReadAsync(container, providerId, cancellationToken);
        return document is null ? null : ToResult(document);
    }

    private static (PetTrainerLicense License, PetTrainerOffering Offering) BuildLicenseAndOffering(
        string licenseNumber,
        string licenseType,
        string licenseImageUrl,
        TrainingSessionInput session,
        IReadOnlyCollection<string> petsTrained,
        IReadOnlyCollection<string> ageGroups,
        IReadOnlyCollection<string> temperaments,
        int maxConcurrentSessions,
        IReadOnlyCollection<string> serviceLocations,
        IReadOnlyCollection<string> trainingApproaches,
        IReadOnlyCollection<string>? previousExperience,
        string privateTrainingDescription)
    {
        if (session is null)
        {
            throw new ArgumentException("Session details are required.", nameof(session));
        }

        var pets = NormalizeSet(petsTrained, AllowedPets, nameof(petsTrained));
        var ages = NormalizeSet(ageGroups, AllowedAgeGroups, nameof(ageGroups));
        var temps = NormalizeSet(temperaments, AllowedTemperaments, nameof(temperaments));
        var locations = NormalizeSet(serviceLocations, AllowedServiceLocations, nameof(serviceLocations));
        var approaches = NormalizeFreeFormSet(trainingApproaches, nameof(trainingApproaches));
        var experience = NormalizeFreeFormSet(previousExperience, nameof(previousExperience), allowEmpty: true);

        if (maxConcurrentSessions is < 1 or > 20)
        {
            throw new ArgumentException("MaxConcurrentSessions must be between 1 and 20.", nameof(maxConcurrentSessions));
        }

        var license = new PetTrainerLicense
        {
            LicenseNumber = Required(licenseNumber, nameof(licenseNumber)),
            LicenseType = Required(licenseType, nameof(licenseType)),
            ImageUrl = Required(licenseImageUrl, nameof(licenseImageUrl))
        };

        var offering = new PetTrainerOffering
        {
            Session = ToTrainingSession(session, nameof(session)),
            PetsTrained = pets,
            AgeGroups = ages,
            Temperaments = temps,
            MaxConcurrentSessions = maxConcurrentSessions,
            ServiceLocations = locations,
            TrainingApproaches = approaches,
            PreviousExperience = experience,
            PrivateTrainingDescription = Required(privateTrainingDescription, nameof(privateTrainingDescription))
        };

        return (license, offering);
    }

    private static TrainingSession ToTrainingSession(TrainingSessionInput input, string fieldName)
    {
        if (input.SessionDurationHours <= 0)
        {
            throw new ArgumentException($"{fieldName}.SessionDurationHours must be greater than zero.", fieldName);
        }

        if (input.PricePerSession < 0)
        {
            throw new ArgumentException($"{fieldName}.PricePerSession must be non-negative.", fieldName);
        }

        return new TrainingSession
        {
            SessionDurationHours = input.SessionDurationHours,
            PricePerSession = input.PricePerSession
        };
    }

    private static List<string> NormalizeSet(
        IReadOnlyCollection<string>? values,
        IReadOnlySet<string> allowed,
        string fieldName,
        bool allowEmpty = false)
    {
        if (values is null)
        {
            if (allowEmpty)
            {
                return new List<string>();
            }

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

        if (deduped.Count == 0 && !allowEmpty)
        {
            throw new ArgumentException($"{fieldName} requires at least one value.", fieldName);
        }

        return deduped;
    }

    private static List<string> NormalizeFreeFormSet(
        IReadOnlyCollection<string>? values,
        string fieldName,
        bool allowEmpty = false)
    {
        if (values is null)
        {
            if (allowEmpty)
            {
                return new List<string>();
            }

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

            if (seen.Add(trimmed))
            {
                deduped.Add(trimmed);
            }
        }

        if (deduped.Count == 0 && !allowEmpty)
        {
            throw new ArgumentException($"{fieldName} requires at least one value.", fieldName);
        }

        return deduped;
    }

    private static async Task<PetTrainerServiceDocument?> TryReadAsync(
        Container container,
        Guid providerId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.ReadItemAsync<PetTrainerServiceDocument>(
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

    private static PetTrainerServiceResult ToResult(PetTrainerServiceDocument document)
    {
        return new PetTrainerServiceResult(
            ProviderId: Guid.Parse(document.ProviderId),
            SubCategory: document.SubCategory,
            Address: document.Address,
            Zip: document.Zip,
            City: document.City,
            Website: document.Website,
            TrainingSchool: document.TrainingSchool is null
                ? null
                : new TrainingSchoolResult(
                    document.TrainingSchool.Name,
                    document.TrainingSchool.TelephoneCountryCode,
                    document.TrainingSchool.TelephoneNumber,
                    document.TrainingSchool.Email,
                    document.TrainingSchool.Description,
                    document.TrainingSchool.ImageUrl,
                    ToLicenseResult(document.TrainingSchool.License),
                    ToOfferingResult(document.TrainingSchool.Offering)),
            Freelance: document.Freelance is null
                ? null
                : new FreelanceTrainerResult(
                    document.Freelance.AboutYou,
                    document.Freelance.ImageUrl,
                    ToLicenseResult(document.Freelance.License),
                    ToOfferingResult(document.Freelance.Offering)),
            CreatedAtUtc: document.CreatedAtUtc,
            UpdatedAtUtc: document.UpdatedAtUtc);
    }

    private static PetTrainerLicenseResult? ToLicenseResult(PetTrainerLicense? license)
    {
        return license is null
            ? null
            : new PetTrainerLicenseResult(
                license.LicenseNumber,
                license.LicenseType,
                license.ImageUrl);
    }

    private static PetTrainerOfferingResult? ToOfferingResult(PetTrainerOffering? offering)
    {
        return offering is null
            ? null
            : new PetTrainerOfferingResult(
                ToTrainingSessionResult(offering.Session),
                offering.PetsTrained.ToArray(),
                offering.AgeGroups.ToArray(),
                offering.Temperaments.ToArray(),
                offering.MaxConcurrentSessions,
                offering.ServiceLocations.ToArray(),
                offering.TrainingApproaches.ToArray(),
                offering.PreviousExperience.ToArray(),
                offering.PrivateTrainingDescription);
    }

    private static TrainingSessionResult? ToTrainingSessionResult(TrainingSession? session)
    {
        return session is null
            ? null
            : new TrainingSessionResult(
                session.SessionDurationHours,
                session.PricePerSession);
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

    private static class PetTrainerSubCategories
    {
        public const string TrainingSchool = "TrainingSchool";
        public const string Freelance = "FreelanceTrainer";
    }
}
