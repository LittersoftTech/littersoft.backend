using System.Net;
using Microsoft.Azure.Cosmos;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Domain.Services;
using Pawfront.Domain.Vocabularies;
using Pawfront.Infrastructure.Cosmos.Documents;
using Pawfront.Infrastructure.Cosmos.ProviderServices;

namespace Pawfront.Infrastructure.Cosmos.Services.PetSitter;

internal sealed class CosmosPetSitterServiceRegistry(
    IProviderServicesContainerAccessor containerAccessor) : IPetSitterServiceRegistry
{
    private static readonly string ServiceCategory = ProviderServiceCategory.PetSitter.ToString();

    // Animals + temperaments come from the canonical platform vocabulary
    // (Pawfront.Domain.Vocabularies) — no per-category copy, so no drift.
    private static readonly IReadOnlySet<string> AllowedAnimals = VocabularyCatalog.AnimalCodes;

    private static readonly IReadOnlySet<string> AllowedAddOns = new HashSet<string>(StringComparer.Ordinal)
    {
        "GiveMedicines", "Feeding", "BathAndDry", "Massage", "OutdoorWalks"
    };

    private static readonly IReadOnlySet<string> AllowedTemperaments = VocabularyCatalog.BehaviourCodes;

    private static readonly IReadOnlySet<string> AllowedServiceLocations = new HashSet<string>(StringComparer.Ordinal)
    {
        "PetHotel", "CustomerPlace", "Both"
    };

    public async Task<PetSitterServiceResult> RegisterPetHotelAsync(
        RegisterPetHotelCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        var existingPetHotel = existing?.SubCategory == PetSitterSubCategories.PetHotel ? existing.PetHotel : null;

        var document = new PetSitterServiceDocument
        {
            Id = command.ProviderId.ToString(),
            ProviderId = command.ProviderId.ToString(),
            ServiceCategory = ServiceCategory,
            SubCategory = PetSitterSubCategories.PetHotel,
            Address = Required(command.Address, nameof(command.Address)),
            Zip = Required(command.Zip, nameof(command.Zip)),
            City = Required(command.City, nameof(command.City)),
            Website = Trim(command.Website),
            PetHotel = new PetHotelDetails
            {
                Name = Required(command.PetHotelName, nameof(command.PetHotelName)),
                TelephoneCountryCode = Required(command.TelephoneCountryCode, nameof(command.TelephoneCountryCode)),
                TelephoneNumber = Required(command.TelephoneNumber, nameof(command.TelephoneNumber)),
                Email = Required(command.Email, nameof(command.Email)),
                // Optional — the "about the provider" blurb may be omitted at registration.
                Description = Trim(command.Description) ?? string.Empty,
                ImageUrl = Trim(command.HotelImageUrl),
                License = existingPetHotel?.License,
                Offering = existingPetHotel?.Offering
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

    public async Task<PetSitterServiceResult> RegisterFreelancePetSitterAsync(
        RegisterFreelancePetSitterCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        var existingFreelance = existing?.SubCategory == PetSitterSubCategories.Freelance ? existing.Freelance : null;

        var document = new PetSitterServiceDocument
        {
            Id = command.ProviderId.ToString(),
            ProviderId = command.ProviderId.ToString(),
            ServiceCategory = ServiceCategory,
            SubCategory = PetSitterSubCategories.Freelance,
            Address = Required(command.Address, nameof(command.Address)),
            Zip = Required(command.Zip, nameof(command.Zip)),
            City = Required(command.City, nameof(command.City)),
            Website = Trim(command.Website),
            PetHotel = null,
            Freelance = new FreelancePetSitterDetails
            {
                // Optional — the "about you" blurb may be omitted at registration.
                AboutYou = Trim(command.AboutYou) ?? string.Empty,
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

    public async Task<PetSitterServiceResult> SavePetHotelOfferingAsync(
        SavePetHotelOfferingCommand command,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        if (existing is null
            || existing.SubCategory != PetSitterSubCategories.PetHotel
            || existing.PetHotel is null)
        {
            throw new PetHotelNotRegisteredException(command.ProviderId);
        }

        var (license, offering) = BuildLicenseAndOffering(
            command.LicenseNumber,
            command.LicenseType,
            command.LicenseImageUrl,
            command.DayCare,
            command.NightStay,
            command.AnimalsHandled,
            command.MaxPetsAtOneTime,
            command.DogTemperaments,
            command.ServiceLocation,
            command.AllowParentFood);

        existing.PetHotel.License = license;
        existing.PetHotel.Offering = offering;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await container.UpsertItemAsync(
            existing,
            new PartitionKey(ServiceCategory),
            cancellationToken: cancellationToken);

        return ToResult(existing);
    }

    public async Task<PetSitterServiceResult> SaveFreelancePetSitterOfferingAsync(
        SaveFreelancePetSitterOfferingCommand command,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        if (existing is null
            || existing.SubCategory != PetSitterSubCategories.Freelance
            || existing.Freelance is null)
        {
            throw new FreelancePetSitterNotRegisteredException(command.ProviderId);
        }

        var (license, offering) = BuildLicenseAndOffering(
            command.LicenseNumber,
            command.LicenseType,
            command.LicenseImageUrl,
            command.DayCare,
            command.NightStay,
            command.AnimalsHandled,
            command.MaxPetsAtOneTime,
            command.DogTemperaments,
            command.ServiceLocation,
            command.AllowParentFood);

        existing.Freelance.License = license;
        existing.Freelance.Offering = offering;
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await container.UpsertItemAsync(
            existing,
            new PartitionKey(ServiceCategory),
            cancellationToken: cancellationToken);

        return ToResult(existing);
    }

    public async Task<PetSitterServiceResult?> GetAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var document = await TryReadAsync(container, providerId, cancellationToken);
        return document is null ? null : ToResult(document);
    }

    private static (PetSitterLicense License, PetSitterOffering Offering) BuildLicenseAndOffering(
        string licenseNumber,
        string licenseType,
        string licenseImageUrl,
        BoardingOfferingInput? dayCare,
        BoardingOfferingInput? nightStay,
        IReadOnlyCollection<string> animalsHandled,
        int maxPetsAtOneTime,
        IReadOnlyCollection<string> dogTemperaments,
        string serviceLocation,
        bool allowParentFood)
    {
        if (dayCare is null && nightStay is null)
        {
            throw new ArgumentException("At least one of DayCare or NightStay must be provided.", nameof(dayCare));
        }

        var animals = NormalizeSet(animalsHandled, AllowedAnimals, nameof(animalsHandled));
        var temperaments = NormalizeSet(dogTemperaments, AllowedTemperaments, nameof(dogTemperaments));
        var location = NormalizeOne(serviceLocation, AllowedServiceLocations, nameof(serviceLocation));

        if (maxPetsAtOneTime is < 1 or > 4)
        {
            throw new ArgumentException("MaxPetsAtOneTime must be between 1 and 4.", nameof(maxPetsAtOneTime));
        }

        var license = new PetSitterLicense
        {
            LicenseNumber = Required(licenseNumber, nameof(licenseNumber)),
            LicenseType = Required(licenseType, nameof(licenseType)),
            ImageUrl = Required(licenseImageUrl, nameof(licenseImageUrl))
        };

        var offering = new PetSitterOffering
        {
            DayCare = dayCare is null ? null : ToBoardingOffering(dayCare, nameof(dayCare)),
            NightStay = nightStay is null ? null : ToBoardingOffering(nightStay, nameof(nightStay)),
            AnimalsHandled = animals,
            MaxPetsAtOneTime = maxPetsAtOneTime,
            DogTemperaments = temperaments,
            ServiceLocation = location,
            AllowParentFood = allowParentFood
        };

        return (license, offering);
    }

    private static BoardingOffering ToBoardingOffering(BoardingOfferingInput input, string fieldName)
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

        return new BoardingOffering
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

    private static async Task<PetSitterServiceDocument?> TryReadAsync(
        Container container,
        Guid providerId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.ReadItemAsync<PetSitterServiceDocument>(
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

    private static PetSitterServiceResult ToResult(PetSitterServiceDocument document)
    {
        return new PetSitterServiceResult(
            ProviderId: Guid.Parse(document.ProviderId),
            SubCategory: document.SubCategory,
            Address: document.Address,
            Zip: document.Zip,
            City: document.City,
            Website: document.Website,
            PetHotel: document.PetHotel is null
                ? null
                : new PetHotelResult(
                    document.PetHotel.Name,
                    document.PetHotel.TelephoneCountryCode,
                    document.PetHotel.TelephoneNumber,
                    document.PetHotel.Email,
                    document.PetHotel.Description,
                    document.PetHotel.ImageUrl,
                    ToLicenseResult(document.PetHotel.License),
                    ToOfferingResult(document.PetHotel.Offering)),
            Freelance: document.Freelance is null
                ? null
                : new FreelancePetSitterResult(
                    document.Freelance.AboutYou,
                    document.Freelance.ImageUrl,
                    ToLicenseResult(document.Freelance.License),
                    ToOfferingResult(document.Freelance.Offering)),
            CreatedAtUtc: document.CreatedAtUtc,
            UpdatedAtUtc: document.UpdatedAtUtc);
    }

    private static PetSitterLicenseResult? ToLicenseResult(PetSitterLicense? license)
    {
        return license is null
            ? null
            : new PetSitterLicenseResult(
                license.LicenseNumber,
                license.LicenseType,
                license.ImageUrl);
    }

    private static PetSitterOfferingResult? ToOfferingResult(PetSitterOffering? offering)
    {
        return offering is null
            ? null
            : new PetSitterOfferingResult(
                ToBoardingOfferingResult(offering.DayCare),
                ToBoardingOfferingResult(offering.NightStay),
                offering.AnimalsHandled.ToArray(),
                offering.MaxPetsAtOneTime,
                offering.DogTemperaments.ToArray(),
                offering.ServiceLocation,
                offering.AllowParentFood);
    }

    private static BoardingOfferingResult? ToBoardingOfferingResult(BoardingOffering? offering)
    {
        return offering is null
            ? null
            : new BoardingOfferingResult(
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

    private static class PetSitterSubCategories
    {
        public const string PetHotel = "PetHotel";
        public const string Freelance = "FreelancePetSitter";
    }
}
