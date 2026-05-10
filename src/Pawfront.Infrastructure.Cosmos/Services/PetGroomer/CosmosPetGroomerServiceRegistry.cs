using System.Net;
using Microsoft.Azure.Cosmos;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Domain.Services;
using Pawfront.Infrastructure.Cosmos.Documents;
using Pawfront.Infrastructure.Cosmos.ProviderServices;

namespace Pawfront.Infrastructure.Cosmos.Services.PetGroomer;

internal sealed class CosmosPetGroomerServiceRegistry(
    IProviderServicesContainerAccessor containerAccessor) : IPetGroomerServiceRegistry
{
    private static readonly string ServiceCategory = ProviderServiceCategory.PetGroomer.ToString();

    private static readonly IReadOnlySet<string> AllowedAnimals = new HashSet<string>(StringComparer.Ordinal)
    {
        "Dogs", "Cats", "GuineaPig", "Hamster"
    };

    private static readonly IReadOnlySet<string> AllowedAddOns = new HashSet<string>(StringComparer.Ordinal)
    {
        "NailTrim", "EarCleaning", "TeethBrushing", "DeShedding", "FleaTreatment"
    };

    private static readonly IReadOnlySet<string> AllowedTemperaments = new HashSet<string>(StringComparer.Ordinal)
    {
        "Friendly", "Anxious", "Aggressive"
    };

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
        if (input.PricePerHour < 0)
        {
            throw new ArgumentException($"{fieldName}.PricePerHour must be non-negative.", fieldName);
        }

        if (input.MinimumBookingHours < 1)
        {
            throw new ArgumentException($"{fieldName}.MinimumBookingHours must be at least 1.", fieldName);
        }

        if (input.LatePickupCharges < 0)
        {
            throw new ArgumentException($"{fieldName}.LatePickupCharges must be non-negative.", fieldName);
        }

        return new GroomingOffering
        {
            PricePerHour = input.PricePerHour,
            AddOns = NormalizeSet(input.AddOns, AllowedAddOns, $"{fieldName}.AddOns", allowEmpty: true),
            MinimumBookingHours = input.MinimumBookingHours,
            LatePickupCharges = input.LatePickupCharges,
            DropOffTime = input.DropOffTime,
            PickUpTime = input.PickUpTime
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
                offering.PricePerHour,
                offering.AddOns.ToArray(),
                offering.MinimumBookingHours,
                offering.LatePickupCharges,
                offering.DropOffTime,
                offering.PickUpTime);
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
