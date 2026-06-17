using System.Net;
using Microsoft.Azure.Cosmos;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Domain.Services;
using Pawfront.Domain.Vocabularies;
using Pawfront.Infrastructure.Cosmos.Documents;
using Pawfront.Infrastructure.Cosmos.ProviderServices;

namespace Pawfront.Infrastructure.Cosmos.Services.PetGroomer;

internal sealed class CosmosPetGroomerServiceRegistry(
    IProviderServicesContainerAccessor containerAccessor) : IPetGroomerServiceRegistry
{
    private static readonly string ServiceCategory = ProviderServiceCategory.PetGroomer.ToString();

    // Animals + temperaments come from the canonical platform vocabulary
    // (Pawfront.Domain.Vocabularies) — no per-category copy, so no drift.
    private static readonly IReadOnlySet<string> AllowedAnimals = VocabularyCatalog.AnimalCodes;

    private static readonly IReadOnlySet<string> AllowedAddOns = new HashSet<string>(StringComparer.Ordinal)
    {
        "NailTrim", "EarCleaning", "TeethBrushing", "DeShedding", "FleaTreatment"
    };

    private static readonly IReadOnlySet<string> AllowedTemperaments = VocabularyCatalog.BehaviourCodes;

    private static readonly IReadOnlySet<string> AllowedServiceLocations = new HashSet<string>(StringComparer.Ordinal)
    {
        "GroomerShop", "CustomerPlace", "Both"
    };

    public async Task<PetGroomerServiceResult> RegisterGroomerShopAsync(
        RegisterGroomerShopCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        var existingShop = existing?.SubCategory == PetGroomerSubCategories.GroomerShop ? existing.GroomerShop : null;

        var document = new PetGroomerServiceDocument
        {
            Id = command.ProviderId.ToString(),
            ProviderId = command.ProviderId.ToString(),
            ServiceCategory = ServiceCategory,
            SubCategory = PetGroomerSubCategories.GroomerShop,
            Address = Required(command.Address, nameof(command.Address)),
            Zip = Required(command.Zip, nameof(command.Zip)),
            City = Required(command.City, nameof(command.City)),
            Website = Trim(command.Website),
            GroomerShop = new GroomerShopDetails
            {
                Name = Required(command.GroomerShopName, nameof(command.GroomerShopName)),
                TelephoneCountryCode = Required(command.TelephoneCountryCode, nameof(command.TelephoneCountryCode)),
                TelephoneNumber = Required(command.TelephoneNumber, nameof(command.TelephoneNumber)),
                Email = Required(command.Email, nameof(command.Email)),
                Description = Required(command.Description, nameof(command.Description)),
                ImageUrl = Trim(command.ShopImageUrl),
                License = existingShop?.License,
                Offering = existingShop?.Offering
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

    public async Task<PetGroomerServiceResult> RegisterFreelanceGroomerAsync(
        RegisterFreelanceGroomerCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        var existingFreelance = existing?.SubCategory == PetGroomerSubCategories.Freelance ? existing.Freelance : null;

        var document = new PetGroomerServiceDocument
        {
            Id = command.ProviderId.ToString(),
            ProviderId = command.ProviderId.ToString(),
            ServiceCategory = ServiceCategory,
            SubCategory = PetGroomerSubCategories.Freelance,
            Address = Required(command.Address, nameof(command.Address)),
            Zip = Required(command.Zip, nameof(command.Zip)),
            City = Required(command.City, nameof(command.City)),
            Website = Trim(command.Website),
            GroomerShop = null,
            Freelance = new FreelanceGroomerDetails
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

    public async Task<PetGroomerServiceResult> SaveGroomerShopOfferingAsync(
        SaveGroomerShopOfferingCommand command,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        if (existing is null
            || existing.SubCategory != PetGroomerSubCategories.GroomerShop
            || existing.GroomerShop is null)
        {
            throw new GroomerShopNotRegisteredException(command.ProviderId);
        }

        var (license, offering) = BuildLicenseAndOffering(
            command.LicenseNumber,
            command.LicenseType,
            command.LicenseImageUrl,
            command.Session,
            command.AnimalsHandled,
            command.MaxPetsAtOneTime,
            command.DogTemperaments,
            command.ServiceLocation);

        existing.GroomerShop.License = license;
        existing.GroomerShop.Offering = offering;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await container.UpsertItemAsync(
            existing,
            new PartitionKey(ServiceCategory),
            cancellationToken: cancellationToken);

        return ToResult(existing);
    }

    public async Task<PetGroomerServiceResult> SaveFreelanceGroomerOfferingAsync(
        SaveFreelanceGroomerOfferingCommand command,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        if (existing is null
            || existing.SubCategory != PetGroomerSubCategories.Freelance
            || existing.Freelance is null)
        {
            throw new FreelanceGroomerNotRegisteredException(command.ProviderId);
        }

        var (license, offering) = BuildLicenseAndOffering(
            command.LicenseNumber,
            command.LicenseType,
            command.LicenseImageUrl,
            command.Session,
            command.AnimalsHandled,
            command.MaxPetsAtOneTime,
            command.DogTemperaments,
            command.ServiceLocation);

        existing.Freelance.License = license;
        existing.Freelance.Offering = offering;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await container.UpsertItemAsync(
            existing,
            new PartitionKey(ServiceCategory),
            cancellationToken: cancellationToken);

        return ToResult(existing);
    }

    public async Task<PetGroomerServiceResult?> GetAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var document = await TryReadAsync(container, providerId, cancellationToken);
        return document is null ? null : ToResult(document);
    }

    private static (PetGroomerLicense License, PetGroomerOffering Offering) BuildLicenseAndOffering(
        string licenseNumber,
        string licenseType,
        string licenseImageUrl,
        GroomingOfferingInput session,
        IReadOnlyCollection<string> animalsHandled,
        int maxPetsAtOneTime,
        IReadOnlyCollection<string> dogTemperaments,
        string serviceLocation)
    {
        if (session is null)
        {
            throw new ArgumentException("Session offering is required.", nameof(session));
        }

        var animals = NormalizeSet(animalsHandled, AllowedAnimals, nameof(animalsHandled));
        var temperaments = NormalizeSet(dogTemperaments, AllowedTemperaments, nameof(dogTemperaments));
        var location = NormalizeOne(serviceLocation, AllowedServiceLocations, nameof(serviceLocation));

        if (maxPetsAtOneTime is < 1 or > 10)
        {
            throw new ArgumentException("MaxPetsAtOneTime must be between 1 and 10.", nameof(maxPetsAtOneTime));
        }

        var license = new PetGroomerLicense
        {
            LicenseNumber = Required(licenseNumber, nameof(licenseNumber)),
            LicenseType = Required(licenseType, nameof(licenseType)),
            ImageUrl = Required(licenseImageUrl, nameof(licenseImageUrl))
        };

        var offering = new PetGroomerOffering
        {
            Session = ToGroomingOffering(session, nameof(session)),
            AnimalsHandled = animals,
            MaxPetsAtOneTime = maxPetsAtOneTime,
            DogTemperaments = temperaments,
            ServiceLocation = location
        };

        return (license, offering);
    }

    private static GroomingOffering ToGroomingOffering(GroomingOfferingInput input, string fieldName)
    {
        if (input.LatePickupCharges < 0)
        {
            throw new ArgumentException($"{fieldName}.LatePickupCharges must be non-negative.", fieldName);
        }

        return new GroomingOffering
        {
            Services = NormalizeServices(input.Services, $"{fieldName}.Services"),
            AddOns = NormalizeSet(input.AddOns, AllowedAddOns, $"{fieldName}.AddOns", allowEmpty: true),
            LatePickupCharges = input.LatePickupCharges,
            DropOffTime = input.DropOffTime,
            PickUpTime = input.PickUpTime
        };
    }

    private static List<GroomingServiceItem> NormalizeServices(
        IReadOnlyCollection<GroomingServiceItemInput>? items,
        string fieldName)
    {
        if (items is null || items.Count == 0)
        {
            throw new ArgumentException(
                $"{fieldName} requires at least one grooming service.", fieldName);
        }

        var result = new List<GroomingServiceItem>(items.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var code = item.Code?.Trim();
            if (string.IsNullOrEmpty(code))
            {
                throw new ArgumentException($"{fieldName}: code is required for every entry.", fieldName);
            }

            if (!GroomingServiceCatalog.Codes.Contains(code))
            {
                throw new ArgumentException(
                    $"{fieldName}: '{code}' is not a recognised grooming service code.", fieldName);
            }

            if (!seen.Add(code))
            {
                throw new ArgumentException(
                    $"{fieldName}: '{code}' is listed more than once. Each service code can appear only once per provider.",
                    fieldName);
            }

            if (item.Price < 0)
            {
                throw new ArgumentException(
                    $"{fieldName}: price for '{code}' must be non-negative.", fieldName);
            }

            if (item.DurationMinutes < 5 || item.DurationMinutes > 480)
            {
                throw new ArgumentException(
                    $"{fieldName}: durationMinutes for '{code}' must be between 5 and 480.", fieldName);
            }

            result.Add(new GroomingServiceItem
            {
                Code = code,
                Price = item.Price,
                DurationMinutes = item.DurationMinutes,
                IsActive = item.IsActive
            });
        }

        return result;
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

    private static async Task<PetGroomerServiceDocument?> TryReadAsync(
        Container container,
        Guid providerId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.ReadItemAsync<PetGroomerServiceDocument>(
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

    private static PetGroomerServiceResult ToResult(PetGroomerServiceDocument document)
    {
        return new PetGroomerServiceResult(
            ProviderId: Guid.Parse(document.ProviderId),
            SubCategory: document.SubCategory,
            Address: document.Address,
            Zip: document.Zip,
            City: document.City,
            Website: document.Website,
            GroomerShop: document.GroomerShop is null
                ? null
                : new GroomerShopResult(
                    document.GroomerShop.Name,
                    document.GroomerShop.TelephoneCountryCode,
                    document.GroomerShop.TelephoneNumber,
                    document.GroomerShop.Email,
                    document.GroomerShop.Description,
                    document.GroomerShop.ImageUrl,
                    ToLicenseResult(document.GroomerShop.License),
                    ToOfferingResult(document.GroomerShop.Offering)),
            Freelance: document.Freelance is null
                ? null
                : new FreelanceGroomerResult(
                    document.Freelance.AboutYou,
                    document.Freelance.ImageUrl,
                    ToLicenseResult(document.Freelance.License),
                    ToOfferingResult(document.Freelance.Offering)),
            CreatedAtUtc: document.CreatedAtUtc,
            UpdatedAtUtc: document.UpdatedAtUtc);
    }

    private static PetGroomerLicenseResult? ToLicenseResult(PetGroomerLicense? license)
    {
        return license is null
            ? null
            : new PetGroomerLicenseResult(
                license.LicenseNumber,
                license.LicenseType,
                license.ImageUrl);
    }

    private static PetGroomerOfferingResult? ToOfferingResult(PetGroomerOffering? offering)
    {
        return offering is null
            ? null
            : new PetGroomerOfferingResult(
                ToGroomingOfferingResult(offering.Session),
                offering.AnimalsHandled.ToArray(),
                offering.MaxPetsAtOneTime,
                offering.DogTemperaments.ToArray(),
                offering.ServiceLocation);
    }

    private static GroomingOfferingResult? ToGroomingOfferingResult(GroomingOffering? offering)
    {
        return offering is null
            ? null
            : new GroomingOfferingResult(
                offering.Services
                    .Select(s => new GroomingServiceItemResult(s.Code, s.Price, s.DurationMinutes, s.IsActive))
                    .ToArray(),
                offering.AddOns.ToArray(),
                offering.LatePickupCharges,
                offering.DropOffTime,
                offering.PickUpTime);
    }

    public IReadOnlyList<GroomingServiceCatalogEntry> GetServiceCatalog()
    {
        return GroomingServiceCatalog.Entries
            .Select(e => new GroomingServiceCatalogEntry(e.Code, e.DisplayName))
            .ToArray();
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

    private static class PetGroomerSubCategories
    {
        public const string GroomerShop = "GroomerShop";
        public const string Freelance = "FreelanceGroomer";
    }
}
