using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using Pawfront.Application.Configuration;
using Pawfront.Application.Notifications;
using Pawfront.Functions.Notifications;
using Pawfront.Infrastructure.Azure;
using Pawfront.Infrastructure.Firebase;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// local.settings.json is a local-only file — it is NEVER part of the published
// payload — so the deployed app reads its configuration from appsettings.json
// (copied into the payload) exactly like the two API hosts do. Loaded from
// AppContext.BaseDirectory rather than the content root, because the Functions
// host does not guarantee the two are the same directory. AddEnvironmentVariables
// is re-applied last so an Azure Application Setting still overrides the file.
builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

// Gives IPawfrontSecretProvider (Key Vault or local, by AzureKeyVault:Enabled) —
// the same registration the two API hosts use, so the booking-sweep Function
// resolves its SQL connection string exactly the way they do.
builder.Services.AddPawfrontAzureInfrastructure(builder.Configuration, builder.Environment);

// --- Notification dispatch ---------------------------------------------------
// FCM sending, bound from the "Notifications" configuration section. Registers
// one Firebase app per audience, because the provider and pet-parent mobile apps
// live in separate Firebase projects and a token from one is invalid in the other.
builder.Services.AddPawfrontFirebaseMessaging(builder.Configuration);

// The dispatcher's side of the outbox — a local store (see Notifications/) that
// talks to SQL directly, exactly as the three booking sweeps do, so this host
// doesn't take a dependency on the API-side infrastructure project. Prefers the
// configured connection string and falls back to the secret provider, matching
// how BookingSweepFunction resolves its own.
builder.Services.AddSingleton<INotificationOutboxStore>(provider =>
    new SqlNotificationOutboxStore(
        builder.Configuration.GetConnectionString("SqlServer"),
        provider.GetService<IPawfrontSecretProvider>(),
        provider.GetRequiredService<ILogger<SqlNotificationOutboxStore>>()));

builder.Services.AddSingleton<NotificationDispatcher>();

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

builder.Build().Run();
