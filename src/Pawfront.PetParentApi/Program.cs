using Pawfront.Application;
using Pawfront.Application.Configuration;
using Pawfront.Infrastructure.Azure;
using Pawfront.Infrastructure.Cosmos;
using Pawfront.Infrastructure.Sql;
using Pawfront.PetParentApi;
using Pawfront.PetParentApi.Auth;
using Pawfront.PetParentApi.Endpoints;
using Pawfront.PetParentApi.Telemetry;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPetParentTelemetry(builder.Configuration, builder.Environment);
builder.Services.AddOpenApi();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();

builder.Services
    .AddPawfrontAzureInfrastructure(builder.Configuration, builder.Environment)
    .AddPawfrontApplication()
    .AddPawfrontSqlInfrastructure(builder.Configuration)
    .AddPawfrontCosmosInfrastructure(builder.Configuration)
    .AddPetParentAuthentication(builder.Configuration);

// Platform fee (% of booking total) surfaced on the booking-detail payment block.
builder.Services.Configure<PawfrontFeeOptions>(
    builder.Configuration.GetSection(PawfrontFeeOptions.SectionName));

// Per-request resolver that caches the caller's PetParentId after the first
// lookup. Required by the ownership filters applied to /pet-parents/* and
// /pets/* route groups.
builder.Services.AddScoped<ICurrentPetParentContext, CurrentPetParentContext>();
// Endpoint filters are resolved as transient by the framework.
builder.Services.AddTransient<OwnedPetParentFilter>();
builder.Services.AddTransient<OwnedPetFilter>();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Configuration.GetValue("Api:UseHttpsRedirection", false))
{
    app.UseHttpsRedirection();
}

app.MapOpenApi();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<PetParentTelemetryEnrichmentMiddleware>();

var api = app.MapGroup("/api/v1").RequireAuthorization(AuthServiceCollectionExtensions.PetParentUserPolicy);

api.MapHealthEndpoints();
api.MapMetadataEndpoints();
api.MapParentOnboardingEndpoints();
api.MapPetParentEndpoints();
api.MapEventEndpoints();
api.MapEventBookingEndpoints();
api.MapNightStayBookingEndpoints();
api.MapProviderDetailsEndpoints();
api.MapProviderSearchEndpoints();
api.MapBlobImageEndpoints();
api.MapAvailabilitySlotsEndpoints();

app.Run();
