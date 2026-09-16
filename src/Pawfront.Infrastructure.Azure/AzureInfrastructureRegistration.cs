using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pawfront.Application.Billing;
using Pawfront.Application.Configuration;
using Pawfront.Application.Storage;
using Pawfront.Infrastructure.Azure.Configuration;
using Pawfront.Infrastructure.Azure.KeyVault;
using Pawfront.Infrastructure.Azure.Queues;
using Pawfront.Infrastructure.Azure.Storage;

namespace Pawfront.Infrastructure.Azure;

public static class AzureInfrastructureRegistration
{
    public static IServiceCollection AddPawfrontAzureInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.Configure<AzureKeyVaultOptions>(configuration.GetSection("AzureKeyVault"));
        services.Configure<BlobStorageOptions>(configuration.GetSection("BlobStorage"));

        var keyVaultEnabled = configuration.GetValue("AzureKeyVault:Enabled", true);

        if (environment.IsDevelopment() || !keyVaultEnabled)
        {
            services.TryAddSingleton<IPawfrontSecretProvider, LocalDevelopmentSecretProvider>();
        }
        else
        {
            services.TryAddSingleton<TokenCredential, DefaultAzureCredential>();

            services.TryAddSingleton(provider =>
            {
                var options = provider.GetRequiredService<IOptions<AzureKeyVaultOptions>>().Value;
                if (string.IsNullOrWhiteSpace(options.VaultUri))
                {
                    throw new InvalidOperationException("AzureKeyVault:VaultUri is required.");
                }

                return new SecretClient(new Uri(options.VaultUri), provider.GetRequiredService<TokenCredential>());
            });

            services.TryAddSingleton<IPawfrontSecretProvider, AzureKeyVaultSecretProvider>();
        }

        services.TryAddSingleton<IPawfrontBlobStorage, AzureBlobStorageService>();

        // Invoice-generation queue. Registered BEFORE AddPawfrontApplication()'s
        // TryAdd of the no-op publisher wins, which is the same first-wins pattern
        // the chat host uses for ICurrentBlockParty. It resolves its credential
        // from the blob storage connection string by default, since the queue
        // lives on the same account.
        services.Configure<InvoiceQueueOptions>(configuration.GetSection(InvoiceQueueOptions.SectionName));
        services.TryAddSingleton<IInvoiceQueuePublisher, AzureInvoiceQueuePublisher>();

        return services;
    }
}
