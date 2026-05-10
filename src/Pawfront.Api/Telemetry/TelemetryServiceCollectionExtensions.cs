using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Pawfront.Api.Telemetry;

internal static class TelemetryServiceCollectionExtensions
{
    public const string ConnectionStringConfigKey = "ApplicationInsights:ConnectionString";

    /// <summary>
    /// Wires OpenTelemetry into Pawfront with:
    ///  - Auto-instrumentation for ASP.NET Core requests, outbound HttpClient calls,
    ///    SQL Client commands, and the Azure SDK (Cosmos, Blob, Key Vault).
    ///  - Pawfront's domain <see cref="PawfrontTelemetry.ActivitySource"/> and
    ///    <see cref="PawfrontTelemetry.Meter"/>.
    ///  - Logs forwarded into the OpenTelemetry pipeline.
    ///  - Azure Monitor (Application Insights) exporter when
    ///    <c>ApplicationInsights:ConnectionString</c> is configured.
    ///  - Console exporter as a dev fallback when no AI connection string is set,
    ///    so traces and metrics stay visible locally.
    /// </summary>
    public static IServiceCollection AddPawfrontTelemetry(
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
                    serviceName: PawfrontTelemetry.ServiceName,
                    serviceVersion: PawfrontTelemetry.ServiceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(PawfrontTelemetry.ActivitySource.Name)
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
                    .AddMeter(PawfrontTelemetry.Meter.Name)
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
