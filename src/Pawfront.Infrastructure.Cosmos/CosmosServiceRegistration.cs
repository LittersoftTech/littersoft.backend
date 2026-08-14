using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pawfront.Application.Chat;
using Pawfront.Application.Events;
using Pawfront.Application.Providers;
using Pawfront.Application.ProviderServices;
using Pawfront.Application.Services.PetAdoptionSale;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Services.PetTrainer;
using Pawfront.Application.Services.Vet;
using Pawfront.Application.Support;
using Pawfront.Infrastructure.Cosmos.Chat;
using Pawfront.Infrastructure.Cosmos.Events;
using Pawfront.Infrastructure.Cosmos.ProviderDiscovery;
using Pawfront.Infrastructure.Cosmos.ProviderServices;
using Pawfront.Infrastructure.Cosmos.Provisioning;
using Pawfront.Infrastructure.Cosmos.Services.PetAdoptionSale;
using Pawfront.Infrastructure.Cosmos.Services.PetGroomer;
using Pawfront.Infrastructure.Cosmos.Services.PetSitter;
using Pawfront.Infrastructure.Cosmos.Services.PetTrainer;
using Pawfront.Infrastructure.Cosmos.Services.Vet;
using Pawfront.Infrastructure.Cosmos.Support;

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
        services.TryAddSingleton<IChatMessagesContainerAccessor, ChatMessagesContainerAccessor>();
        services.TryAddSingleton<ISupportTicketsContainerAccessor, SupportTicketsContainerAccessor>();

        services.TryAddSingleton<IPetSitterServiceRegistry, CosmosPetSitterServiceRegistry>();
        services.TryAddSingleton<IPetGroomerServiceRegistry, CosmosPetGroomerServiceRegistry>();
        services.TryAddSingleton<IPetTrainerServiceRegistry, CosmosPetTrainerServiceRegistry>();
        services.TryAddSingleton<IPetAdoptionSaleServiceRegistry, CosmosPetAdoptionSaleServiceRegistry>();
        services.TryAddSingleton<IVetServiceRegistry, CosmosVetServiceRegistry>();
        services.TryAddSingleton<IEventCosmosStore, CosmosEventStore>();
        services.TryAddSingleton<IProviderServiceCosmosStore, CosmosProviderServiceStore>();
        services.TryAddSingleton<IChatMessageStore, CosmosChatMessageStore>();
        services.TryAddSingleton<ISupportTicketNarrativeStore, CosmosSupportTicketNarrativeStore>();

        // Discovery is registered WRAPPED, so nothing can resolve the raw Cosmos
        // reader by interface. The wrapper drops providers whose SQL master
        // Active switch is off (or who deleted their account) — a fact the
        // offering document doesn't carry, which is why the parent apps used to
        // list providers that then rejected every booking with 409
        // ProviderInactive. Scoped, because the SQL reader it composes is.
        services.TryAddSingleton<CosmosProviderDiscoveryService>();
        services.TryAddScoped<IProviderDiscoveryService>(sp =>
            new ActiveOnlyProviderDiscoveryService(
                sp.GetRequiredService<CosmosProviderDiscoveryService>(),
                sp.GetRequiredService<IProviderActiveStatusReader>()));

        services.AddHostedService<CosmosBootstrapper>();

        return services;
    }
}
