using Pawfront.Api;
using Pawfront.Api.Auth;
using Pawfront.Api.Endpoints;
using Pawfront.Api.Telemetry;
using Pawfront.Application;
using Pawfront.Application.Configuration;
using Pawfront.Infrastructure.Azure;
using Pawfront.Infrastructure.Cosmos;
using Pawfront.Infrastructure.Sql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPawfrontTelemetry(builder.Configuration, builder.Environment);
builder.Services.AddOpenApi();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services
    .AddPawfrontAzureInfrastructure(builder.Configuration, builder.Environment)
    .AddPawfrontApplication()
    .AddPawfrontSqlInfrastructure(builder.Configuration)
    .AddPawfrontCosmosInfrastructure(builder.Configuration)
    .AddPawfrontAuthentication(builder.Configuration);

// Platform fee (% of booking total) surfaced on the booking-detail payment block.
builder.Services.Configure<PawfrontFeeOptions>(
    builder.Configuration.GetSection(PawfrontFeeOptions.SectionName));

var app = builder.Build();

app.UseExceptionHandler();

if (app.Configuration.GetValue("Api:UseHttpsRedirection", false))
{
    app.UseHttpsRedirection();
}

app.MapOpenApi();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<ProviderTelemetryEnrichmentMiddleware>();

var api = app.MapGroup("/api/v1").RequireAuthorization(AuthServiceCollectionExtensions.FirebaseUserPolicy);

api.MapHealthEndpoints();
api.MapMetadataEndpoints();
api.MapProviderOnboardingEndpoints();
api.MapProviderEndpoints();
api.MapProviderServiceCatalogEndpoints();
api.MapProviderPolicyEndpoints();
api.MapProviderAvailabilityEndpoints();
api.MapPetSitterEndpoints();
api.MapPetGroomerEndpoints();
api.MapPetTrainerEndpoints();
api.MapPetAdoptionSaleEndpoints();
api.MapVetEndpoints();
api.MapEventEndpoints();
api.MapEventBookingEndpoints();
api.MapEventDashboardEndpoints();
api.MapBookingEndpoints();
api.MapProviderNightStayBookingEndpoints();
api.MapBlobImageEndpoints();
api.MapProviderClosureEndpoints();
api.MapProviderActiveStatusEndpoints();
api.MapProviderPhotoEndpoints();

app.Run();
