using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pawfront.Application.Events;
using Pawfront.Application.Providers;
using Pawfront.Application.Services.PetAdoptionSale;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Services.PetTrainer;
using Pawfront.Application.Services.Vet;
using Pawfront.Infrastructure.Cosmos.Events;
using Pawfront.Infrastructure.Cosmos.ProviderDiscovery;
using Pawfront.Infrastructure.Cosmos.ProviderServices;
using Pawfront.Infrastructure.Cosmos.Provisioning;
using Pawfront.Infrastructure.Cosmos.Services.PetAdoptionSale;
using Pawfront.Infrastructure.Cosmos.Services.PetGroomer;
using Pawfront.Infrastructure.Cosmos.Services.PetSitter;
using Pawfront.Infrastructure.Cosmos.Services.PetTrainer;
using Pawfront.Infrastructure.Cosmos.Services.Vet;

namespace Pawfront.Infrastructure.Cosmos;

public static class CosmosServiceRegistration
{
    public static IServiceCollection AddPawfrontCosmosInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<CosmosOptions>(configuration.GetSection("Cosmos"));

        services.TryAddSingleton<IProviderServicesContainerAccessor, ProviderServicesContainerAccessor>();
        services.TryAddSingleton<IEventsContainerAccessor, EventsContainerAccessor>();

        services.TryAddSingleton<IPetSitterServiceRegistry, CosmosPetSitterServiceRegistry>();
        services.TryAddSingleton<IPetGroomerServiceRegistry, CosmosPetGroomerServiceRegistry>();
        services.TryAddSingleton<IPetTrainerServiceRegistry, CosmosPetTrainerServiceRegistry>();
        services.TryAddSingleton<IPetAdoptionSaleServiceRegistry, CosmosPetAdoptionSaleServiceRegistry>();
        services.TryAddSingleton<IVetServiceRegistry, CosmosVetServiceRegistry>();
        services.TryAddSingleton<IEventCosmosStore, CosmosEventStore>();

        services.TryAddSingleton<IProviderDiscoveryService, CosmosProviderDiscoveryService>();

        services.AddHostedService<CosmosBootstrapper>();

        return services;
    }
}
