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
using Pawfront.Application.Storage;
using Pawfront.Functions.Invoices;
using Pawfront.Infrastructure.Azure;
using Pawfront.Infrastructure.Cosmos;
using Pawfront.Infrastructure.Firebase;
using QuestPDF.Infrastructure;

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

// --- Invoice generation ------------------------------------------------------
// QuestPDF's Community licence: free for organisations under $1M annual revenue.
// It must be set before the first render or the library throws.
QuestPDF.Settings.License = LicenseType.Community;

// The provider's BUSINESS name and address live in their Cosmos offering
// document, not SQL, and an invoice has to name its issuer. Only the narrow
// provider-lookup slice is registered - the full AddPawfrontCosmosInfrastructure
// would pull in the discovery wrappers, which need SQL-backed services this host
// deliberately does not have, and a container bootstrapper a background worker
// has no business running.
builder.Services.AddPawfrontCosmosProviderLookup(builder.Configuration);

builder.Services.Configure<InvoiceOptions>(
    builder.Configuration.GetSection(InvoiceOptions.SectionName));
// The SAME fee percentage the API hosts use, so an invoice cannot quote a
// different commission from the app that produced the booking.
builder.Services.Configure<PawfrontFeeOptions>(builder.Configuration.GetSection("Payments"));

// The renderer's side of Billing.Invoices - claim, complete, sweep. Talks to SQL
// directly, exactly as the booking sweeps do, so this host still has no reference
// to Pawfront.Infrastructure.Sql.
builder.Services.AddSingleton<IInvoiceGenerationStore>(provider =>
    new SqlInvoiceGenerationStore(
        builder.Configuration.GetConnectionString("SqlServer"),
        provider.GetService<IPawfrontSecretProvider>(),
        provider.GetRequiredService<ILogger<SqlInvoiceGenerationStore>>()));

builder.Services.AddSingleton<InvoiceGenerator>();

if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING")))
{
    builder.Services.AddOpenTelemetry()
        .UseFunctionsWorkerDefaults()
        .UseAzureMonitorExporter();
}

builder.Build().Run();
