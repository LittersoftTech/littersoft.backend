using System.Net;
using Microsoft.Azure.Cosmos;
using Pawfront.Application.Services.PetAdoptionSale;
using Pawfront.Domain.Services;
using Pawfront.Infrastructure.Cosmos.Documents;
using Pawfront.Infrastructure.Cosmos.ProviderServices;

namespace Pawfront.Infrastructure.Cosmos.Services.PetAdoptionSale;

internal sealed class CosmosPetAdoptionSaleServiceRegistry(
    IProviderServicesContainerAccessor containerAccessor) : IPetAdoptionSaleServiceRegistry
{
    private static readonly string ServiceCategory = ProviderServiceCategory.PetAdoptionAndSale.ToString();

    public async Task<PetAdoptionSaleServiceResult> RegisterPetShelterAsync(
        RegisterPetShelterCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        var document = new PetAdoptionSaleServiceDocument
        {
            Id = command.ProviderId.ToString(),
            ProviderId = command.ProviderId.ToString(),
            ServiceCategory = ServiceCategory,
            SubCategory = PetAdoptionSaleSubCategories.PetShelter,
            Address = Required(command.Address, nameof(command.Address)),
            Zip = Required(command.Zip, nameof(command.Zip)),
            City = Required(command.City, nameof(command.City)),
            Website = Trim(command.Website),
            PetShelter = new PetShelterDetails
            {
                Name = Required(command.PetShelterName, nameof(command.PetShelterName)),
                TelephoneCountryCode = Required(command.TelephoneCountryCode, nameof(command.TelephoneCountryCode)),
                TelephoneNumber = Required(command.TelephoneNumber, nameof(command.TelephoneNumber)),
                Email = Required(command.Email, nameof(command.Email)),
                // Optional — the "about the provider" blurb may be omitted at registration.
                Description = Trim(command.Description) ?? string.Empty,
                ImageUrl = Trim(command.ShelterImageUrl)
            },
            PetShop = null,
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

    public async Task<PetAdoptionSaleServiceResult> RegisterPetShopAsync(
        RegisterPetShopCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        var document = new PetAdoptionSaleServiceDocument
        {
            Id = command.ProviderId.ToString(),
            ProviderId = command.ProviderId.ToString(),
            ServiceCategory = ServiceCategory,
            SubCategory = PetAdoptionSaleSubCategories.PetShop,
            Address = Required(command.Address, nameof(command.Address)),
            Zip = Required(command.Zip, nameof(command.Zip)),
            City = Required(command.City, nameof(command.City)),
            Website = Trim(command.Website),
            PetShelter = null,
            PetShop = new PetShopDetails
            {
                Name = Required(command.PetShopName, nameof(command.PetShopName)),
                TelephoneCountryCode = Required(command.TelephoneCountryCode, nameof(command.TelephoneCountryCode)),
                TelephoneNumber = Required(command.TelephoneNumber, nameof(command.TelephoneNumber)),
                Email = Required(command.Email, nameof(command.Email)),
                // Optional — the "about the provider" blurb may be omitted at registration.
                Description = Trim(command.Description) ?? string.Empty,
                ImageUrl = Trim(command.ShopImageUrl)
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

    public async Task<PetAdoptionSaleServiceResult> RegisterFreelanceAsync(
        RegisterFreelancePetAdoptionSaleCommand command,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var existing = await TryReadAsync(container, command.ProviderId, cancellationToken);

        var document = new PetAdoptionSaleServiceDocument
        {
            Id = command.ProviderId.ToString(),
            ProviderId = command.ProviderId.ToString(),
            ServiceCategory = ServiceCategory,
            SubCategory = PetAdoptionSaleSubCategories.Freelance,
            Address = Required(command.Address, nameof(command.Address)),
            Zip = Required(command.Zip, nameof(command.Zip)),
            City = Required(command.City, nameof(command.City)),
            Website = Trim(command.Website),
            PetShelter = null,
            PetShop = null,
            Freelance = new FreelancePetAdoptionSaleDetails
            {
                // Optional — the "about you" blurb may be omitted at registration.
                AboutYou = Trim(command.AboutYou) ?? string.Empty,
                ImageUrl = Trim(command.ProfileImageUrl)
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

    public async Task<PetAdoptionSaleServiceResult?> GetAsync(Guid providerId, CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var document = await TryReadAsync(container, providerId, cancellationToken);
        return document is null ? null : ToResult(document);
    }

    private static async Task<PetAdoptionSaleServiceDocument?> TryReadAsync(
        Container container,
        Guid providerId,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.ReadItemAsync<PetAdoptionSaleServiceDocument>(
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

    private static PetAdoptionSaleServiceResult ToResult(PetAdoptionSaleServiceDocument document)
    {
        return new PetAdoptionSaleServiceResult(
            ProviderId: Guid.Parse(document.ProviderId),
            SubCategory: document.SubCategory,
            Address: document.Address,
            Zip: document.Zip,
            City: document.City,
            Website: document.Website,
            PetShelter: document.PetShelter is null
                ? null
                : new PetShelterResult(
                    document.PetShelter.Name,
                    document.PetShelter.TelephoneCountryCode,
                    document.PetShelter.TelephoneNumber,
                    document.PetShelter.Email,
                    document.PetShelter.Description,
                    document.PetShelter.ImageUrl),
            PetShop: document.PetShop is null
                ? null
                : new PetShopResult(
                    document.PetShop.Name,
                    document.PetShop.TelephoneCountryCode,
                    document.PetShop.TelephoneNumber,
                    document.PetShop.Email,
                    document.PetShop.Description,
                    document.PetShop.ImageUrl),
            Freelance: document.Freelance is null
                ? null
                : new FreelancePetAdoptionSaleResult(
                    document.Freelance.AboutYou,
                    document.Freelance.ImageUrl),
            CreatedAtUtc: document.CreatedAtUtc,
            UpdatedAtUtc: document.UpdatedAtUtc);
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

    private static class PetAdoptionSaleSubCategories
    {
        public const string PetShelter = "PetShelter";
        public const string PetShop = "PetShop";
        public const string Freelance = "FreelancePetAdoptionSale";
    }
}
