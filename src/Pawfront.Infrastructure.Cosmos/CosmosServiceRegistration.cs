using Pawfront.Application.Blocks;
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
        // The unfiltered reader, under its own narrow interface. Same instance the
        // wrappers below compose, so it can never disagree with them about who a
        // provider is - it simply skips the two filters. See
        // IProviderSummaryReader for when that is the right thing to want.
        services.TryAddSingleton<IProviderSummaryReader, CosmosProviderSummaryReader>();
        services.TryAddScoped<IProviderDiscoveryService>(sp =>
            // Two wrappers, innermost first: drop the providers who switched
            // themselves off, then drop the ones this caller is blocked from.
            // Order is not load-bearing -- both are filters over the same list --
            // but blocking sits outermost because it is the caller-specific one,
            // and it is the layer that pages the result.
            new BlockAwareProviderDiscoveryService(
                new ActiveOnlyProviderDiscoveryService(
                    sp.GetRequiredService<CosmosProviderDiscoveryService>(),
                    sp.GetRequiredService<IProviderActiveStatusReader>()),
                sp.GetRequiredService<ICurrentBlockParty>(),
                sp.GetRequiredService<IMyBlockLookup>()));

        services.AddHostedService<CosmosBootstrapper>();

        return services;
    }

    /// <summary>
    /// Registers ONLY the provider-offering lookup slice: the ProviderServices
    /// container accessor and the raw Cosmos discovery reader.
    ///
    /// For a host that needs a provider's business identity (name, address, city,
    /// zip - none of which are in SQL) but has no discovery surface and no
    /// reference to Pawfront.Infrastructure.Sql. The invoice renderer in
    /// Pawfront.Functions is the case this exists for.
    ///
    /// Deliberately NOT the full <see cref="AddPawfrontCosmosInfrastructure"/>:
    /// that registers <see cref="IProviderDiscoveryService"/> wrapped in the
    /// active-status and block filters, both of which need SQL-backed services
    /// this host does not have. It also skips
    /// <see cref="Provisioning.CosmosBootstrapper"/> - a background worker has no
    /// business creating containers.
    ///
    /// It returns the RAW reader on purpose, so the two filters are not applied.
    /// That is correct here and would be wrong on an API host: an invoice for a
    /// job that already happened must still name its provider, whether or not
    /// that provider has since switched themselves off, deleted their account, or
    /// been blocked by the parent.
    /// </summary>
    public static IServiceCollection AddPawfrontCosmosProviderLookup(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<CosmosOptions>(configuration.GetSection("Cosmos"));
        services.TryAddSingleton<IProviderServicesContainerAccessor, ProviderServicesContainerAccessor>();
        services.TryAddSingleton<CosmosProviderDiscoveryService>();
        services.TryAddSingleton<IProviderSummaryReader, CosmosProviderSummaryReader>();

        return services;
    }
}
