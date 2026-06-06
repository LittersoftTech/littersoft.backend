using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Pawfront.PetParentApi.Telemetry;

internal static class TelemetryServiceCollectionExtensions
{
    public const string ConnectionStringConfigKey = "ApplicationInsights:ConnectionString";

    public static IServiceCollection AddPetParentTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var connectionString = configuration[ConnectionStringConfigKey];
        var hasConnectionString = !string.IsNullOrWhiteSpace(connectionString);
        var useDevFallback = environment.IsDevelopment() && !hasConnectionString;

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: PetParentTelemetry.ServiceName,
                    serviceVersion: PetParentTelemetry.ServiceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(PetParentTelemetry.ActivitySource.Name)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = true;
                    })
                    .AddHttpClientInstrumentation()
                    .AddSqlClientInstrumentation(options =>
                    {
                        options.RecordException = true;
                    });

                if (useDevFallback)
                {
                    tracing.AddConsoleExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(PetParentTelemetry.Meter.Name)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                if (useDevFallback)
                {
                    metrics.AddConsoleExporter();
                }
            });

        services.AddLogging(logging =>
        {
            logging.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
                options.ParseStateValues = true;

                if (useDevFallback)
                {
                    options.AddConsoleExporter();
                }
            });
        });

        if (hasConnectionString)
        {
            otel.UseAzureMonitor(options => options.ConnectionString = connectionString);
        }

        return services;
    }
}
