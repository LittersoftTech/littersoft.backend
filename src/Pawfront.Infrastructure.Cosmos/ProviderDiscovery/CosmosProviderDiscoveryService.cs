using Microsoft.Azure.Cosmos;
using Pawfront.Application.Providers;
using Pawfront.Domain.Services;
using Pawfront.Infrastructure.Cosmos.Documents;
using Pawfront.Infrastructure.Cosmos.ProviderServices;

namespace Pawfront.Infrastructure.Cosmos.ProviderDiscovery;

/// <summary>
/// Parent-facing provider discovery. Queries each category's partition in
/// <c>ProviderServices</c>, maps offering docs to <see cref="ProviderSummary"/>
/// cards, and applies the animals filter in C# (the relevant array path
/// differs per category, so an in-Cosmos predicate would need a five-arm
/// switch).
///
/// Performance note: this currently fetches every doc in the matched
/// category partition before applying the animals filter / skip / take.
/// Fine for the small dev catalogue; revisit when the provider count per
/// category gets large (denormalise animals into the SQL filter table or
/// add a per-partition projection query).
/// </summary>
internal sealed class CosmosProviderDiscoveryService(
    IProviderServicesContainerAccessor containerAccessor) : IProviderDiscoveryService
{
    private static readonly string[] AllCategories =
    [
        nameof(ProviderServiceCategory.PetSitter),
        nameof(ProviderServiceCategory.PetGroomer),
        nameof(ProviderServiceCategory.PetTrainer),
        nameof(ProviderServiceCategory.PetAdoptionAndSale),
        nameof(ProviderServiceCategory.Vet)
    ];

    public async Task<IReadOnlyList<ProviderSummary>> ListAsync(
        ProviderDiscoveryFilter filter,
        CancellationToken cancellationToken)
    {
        var animalsFilter = NormaliseAnimals(filter.Animals);
        var cityFilter = string.IsNullOrWhiteSpace(filter.City) ? null : filter.City.Trim();
        var locationFilter = string.IsNullOrWhiteSpace(filter.ServiceLocation) ? null : filter.ServiceLocation;
        var categories = string.IsNullOrWhiteSpace(filter.ServiceCategory)
            ? AllCategories
            : new[] { filter.ServiceCategory };

        var container = await containerAccessor.GetContainerAsync(cancellationToken);

        var all = new List<ProviderSummary>();
        foreach (var category in categories)
        {
            // PetAdoptionAndSale has no offering and therefore no animal or
            // service-location data — either filter excludes every shelter/shop.
            if (string.Equals(category, nameof(ProviderServiceCategory.PetAdoptionAndSale), StringComparison.Ordinal)
                && (animalsFilter is { Count: > 0 } || locationFilter is not null))
            {
                continue;
            }

            var summaries = category switch
            {
                nameof(ProviderServiceCategory.PetSitter) =>
                    await QueryAsync<PetSitterServiceDocument>(
                        container, category, ToPetSitterSummary, animalsFilter, cityFilter,
                        locationFilter is null ? null : doc => MatchesPetSitterLocation(doc, locationFilter),
                        cancellationToken),
                nameof(ProviderServiceCategory.PetGroomer) =>
                    await QueryAsync<PetGroomerServiceDocument>(
                        container, category, ToPetGroomerSummary, animalsFilter, cityFilter,
                        locationFilter is null ? null : doc => MatchesPetGroomerLocation(doc, locationFilter),
                        cancellationToken),
                nameof(ProviderServiceCategory.PetTrainer) =>
                    await QueryAsync<PetTrainerServiceDocument>(
                        container, category, ToPetTrainerSummary, animalsFilter, cityFilter,
                        locationFilter is null ? null : doc => MatchesPetTrainerLocation(doc, locationFilter),
                        cancellationToken),
                nameof(ProviderServiceCategory.PetAdoptionAndSale) =>
                    await QueryAsync<PetAdoptionSaleServiceDocument>(
                        container, category, ToPetAdoptionSaleSummary, animalsFilter, cityFilter,
                        locationPredicate: null,
                        cancellationToken),
                nameof(ProviderServiceCategory.Vet) =>
                    await QueryAsync<VetServiceDocument>(
                        container, category, ToVetSummary, animalsFilter, cityFilter,
                        locationFilter is null ? null : doc => MatchesVetLocation(doc, locationFilter),
                        cancellationToken),
                _ => new List<ProviderSummary>()
            };
            all.AddRange(summaries);
        }

        return all
            .Skip(Math.Max(0, filter.Skip))
            .Take(filter.Take <= 0 ? 50 : filter.Take)
            .ToList();
    }

    private static async Task<List<ProviderSummary>> QueryAsync<TDoc>(
        Container container,
        string category,
        Func<TDoc, ProviderSummary> map,
        HashSet<string>? animalsFilter,
        string? cityFilter,
        Func<TDoc, bool>? locationPredicate,
        CancellationToken cancellationToken)
    {
        var iterator = container.GetItemQueryIterator<TDoc>(
            new QueryDefinition("SELECT * FROM c"),
            requestOptions: new QueryRequestOptions
            {
                PartitionKey = new PartitionKey(category)
            });

        var matched = new List<ProviderSummary>();
        while (iterator.HasMoreResults)
        {
            var page = await iterator.ReadNextAsync(cancellationToken);
            foreach (var doc in page)
            {
                if (locationPredicate is not null && !locationPredicate(doc))
                {
                    continue;
                }
                var summary = map(doc);
                if (animalsFilter is { Count: > 0 } && !MatchesAnimals(summary.AnimalsHandled, animalsFilter))
                {
                    continue;
                }
                if (cityFilter is not null
                    && !string.Equals(summary.City, cityFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                matched.Add(summary);
            }
        }
        return matched;
    }

    // ---------------------------------------------------------------------
    // Service-location matching. The wire filter speaks ParentsPlace /
    // ProvidersPlace; each category stores its own vocabulary (see the
    // AllowedServiceLocations set in the matching Cosmos registry). A stored
    // "Both" matches either side. Docs without an offering yet have no
    // location data and never match a location filter.
    // ---------------------------------------------------------------------

    private static bool MatchesPetSitterLocation(PetSitterServiceDocument doc, string locationFilter)
    {
        var stored = doc.PetHotel?.Offering?.ServiceLocation ?? doc.Freelance?.Offering?.ServiceLocation;
        return locationFilter switch
        {
            ProviderServiceLocationFilters.ParentsPlace => stored is "CustomerPlace" or "Both",
            ProviderServiceLocationFilters.ProvidersPlace => stored is "PetHotel" or "Both",
            _ => false
        };
    }

    private static bool MatchesPetGroomerLocation(PetGroomerServiceDocument doc, string locationFilter)
    {
        var stored = doc.GroomerShop?.Offering?.ServiceLocation ?? doc.Freelance?.Offering?.ServiceLocation;
        return locationFilter switch
        {
            ProviderServiceLocationFilters.ParentsPlace => stored is "CustomerPlace" or "Both",
            ProviderServiceLocationFilters.ProvidersPlace => stored is "GroomerShop" or "Both",
            _ => false
        };
    }

    private static bool MatchesPetTrainerLocation(PetTrainerServiceDocument doc, string locationFilter)
    {
        // Trainer stores a multi-select; NatureOrParks / UrbanOrCity are
        // neutral venues that count as neither side of the filter.
        var stored = doc.TrainingSchool?.Offering?.ServiceLocations
            ?? doc.Freelance?.Offering?.ServiceLocations;
        if (stored is null)
        {
            return false;
        }

        return locationFilter switch
        {
            ProviderServiceLocationFilters.ParentsPlace =>
                stored.Contains("CustomerLocation"),
            ProviderServiceLocationFilters.ProvidersPlace =>
                stored.Contains("TrainerLocation") || stored.Contains("TrainingSchool"),
            _ => false
        };
    }

    private static bool MatchesVetLocation(VetServiceDocument doc, string locationFilter)
    {
        var stored = doc.VetClinic?.Offering?.ServiceLocation ?? doc.Freelance?.Offering?.ServiceLocation;
        return locationFilter switch
        {
            ProviderServiceLocationFilters.ParentsPlace => stored is "CustomerLocation" or "Both",
            ProviderServiceLocationFilters.ProvidersPlace => stored is "VetClinic" or "Both",
            _ => false
        };
    }

    private static bool MatchesAnimals(
        IReadOnlyCollection<string> providerAnimals,
        HashSet<string> filterAnimals)
    {
        foreach (var animal in providerAnimals)
        {
            if (filterAnimals.Contains(animal))
            {
                return true;
            }
        }
        return false;
    }

    private static HashSet<string>? NormaliseAnimals(IReadOnlyCollection<string>? animals)
    {
        if (animals is null || animals.Count == 0)
        {
            return null;
        }

        var normalised = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var animal in animals)
        {
            var trimmed = animal?.Trim();
            if (!string.IsNullOrEmpty(trimmed))
            {
                normalised.Add(trimmed);
            }
        }
        return normalised.Count == 0 ? null : normalised;
    }

    // ---------------------------------------------------------------------
    // Per-category mapping: doc → summary card. AnimalsHandled is pulled
    // from whichever offering branch is populated (Hotel vs Freelance, etc.).
    // ---------------------------------------------------------------------

    private static ProviderSummary ToPetSitterSummary(PetSitterServiceDocument doc)
    {
        var animals = doc.PetHotel?.Offering?.AnimalsHandled
            ?? doc.Freelance?.Offering?.AnimalsHandled
            ?? new List<string>();

        return new ProviderSummary(
            ProviderId: Guid.Parse(doc.ProviderId),
            ServiceCategory: nameof(ProviderServiceCategory.PetSitter),
            SubCategory: doc.SubCategory,
            DisplayName: doc.PetHotel?.Name,
            ImageUrl: doc.PetHotel?.ImageUrl ?? doc.Freelance?.ImageUrl,
            City: doc.City,
            About: doc.PetHotel?.Description ?? doc.Freelance?.AboutYou,
            AnimalsHandled: animals);
    }

    private static ProviderSummary ToPetGroomerSummary(PetGroomerServiceDocument doc)
    {
        var animals = doc.GroomerShop?.Offering?.AnimalsHandled
            ?? doc.Freelance?.Offering?.AnimalsHandled
            ?? new List<string>();

        return new ProviderSummary(
            ProviderId: Guid.Parse(doc.ProviderId),
            ServiceCategory: nameof(ProviderServiceCategory.PetGroomer),
            SubCategory: doc.SubCategory,
            DisplayName: doc.GroomerShop?.Name,
            ImageUrl: doc.GroomerShop?.ImageUrl ?? doc.Freelance?.ImageUrl,
            City: doc.City,
            About: doc.GroomerShop?.Description ?? doc.Freelance?.AboutYou,
            AnimalsHandled: animals);
    }

    private static ProviderSummary ToPetTrainerSummary(PetTrainerServiceDocument doc)
    {
        // PetTrainer's animal field is named PetsTrained in the doc; we
        // surface it under the AnimalsHandled key so the wire shape stays
        // uniform across categories.
        var animals = doc.TrainingSchool?.Offering?.PetsTrained
            ?? doc.Freelance?.Offering?.PetsTrained
            ?? new List<string>();

        return new ProviderSummary(
            ProviderId: Guid.Parse(doc.ProviderId),
            ServiceCategory: nameof(ProviderServiceCategory.PetTrainer),
            SubCategory: doc.SubCategory,
            DisplayName: doc.TrainingSchool?.Name,
            ImageUrl: doc.TrainingSchool?.ImageUrl ?? doc.Freelance?.ImageUrl,
            City: doc.City,
            About: doc.TrainingSchool?.Description ?? doc.Freelance?.AboutYou,
            AnimalsHandled: animals);
    }

    private static ProviderSummary ToPetAdoptionSaleSummary(PetAdoptionSaleServiceDocument doc)
    {
        // PetAdoptionAndSale has no offering and therefore no animal data —
        // surface an empty list so the response shape stays uniform.
        var displayName = doc.PetShelter?.Name ?? doc.PetShop?.Name;
        var imageUrl = doc.PetShelter?.ImageUrl ?? doc.PetShop?.ImageUrl ?? doc.Freelance?.ImageUrl;
        var about = doc.PetShelter?.Description ?? doc.PetShop?.Description ?? doc.Freelance?.AboutYou;

        return new ProviderSummary(
            ProviderId: Guid.Parse(doc.ProviderId),
            ServiceCategory: nameof(ProviderServiceCategory.PetAdoptionAndSale),
            SubCategory: doc.SubCategory,
            DisplayName: displayName,
            ImageUrl: imageUrl,
            City: doc.City,
            About: about,
            AnimalsHandled: Array.Empty<string>());
    }

    private static ProviderSummary ToVetSummary(VetServiceDocument doc)
    {
        // Vet's animal field is AnimalsTreated; surfaced under AnimalsHandled
        // for wire uniformity.
        var animals = doc.VetClinic?.Offering?.AnimalsTreated
            ?? doc.Freelance?.Offering?.AnimalsTreated
            ?? new List<string>();

        return new ProviderSummary(
            ProviderId: Guid.Parse(doc.ProviderId),
            ServiceCategory: nameof(ProviderServiceCategory.Vet),
            SubCategory: doc.SubCategory,
            DisplayName: doc.VetClinic?.Name,
            ImageUrl: doc.VetClinic?.ImageUrl ?? doc.Freelance?.ImageUrl,
            City: doc.City,
            About: doc.VetClinic?.Description ?? doc.Freelance?.AboutYou,
            AnimalsHandled: animals);
    }
}
