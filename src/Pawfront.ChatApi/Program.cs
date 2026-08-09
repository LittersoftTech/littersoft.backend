using Pawfront.Application;
using Pawfront.Application.Chat;
using Pawfront.ChatApi;
using Pawfront.ChatApi.Auth;
using Pawfront.ChatApi.Delivery;
using Pawfront.ChatApi.Endpoints;
using Pawfront.ChatApi.Hubs;
using Pawfront.ChatApi.RateLimiting;
using Pawfront.ChatApi.Telemetry;
using Pawfront.Infrastructure.Firebase;
using Pawfront.Infrastructure.Azure;
using Pawfront.Infrastructure.Cosmos;
using Pawfront.Infrastructure.Sql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddChatTelemetry(builder.Configuration, builder.Environment);
builder.Services.AddOpenApi();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();

// Per-account limits. Chat is open — anybody may message anybody — so nothing
// else bounds how many strangers one account can reach.
builder.Services.AddChatRateLimiting();

// Registered BEFORE AddPawfrontApplication on purpose: Application TryAdds a
// no-op publisher so hosts without a hub still resolve, and TryAdd is first-wins.
// Putting the real one in first is what makes this host use the socket.
builder.Services.AddScoped<IChatRealtimePublisher, SignalRChatRealtimePublisher>();

// Instant push. The queue IS the dispatcher the Application layer sees, so the
// same singleton is registered under both — the worker reads what ChatService
// writes. Registered before AddPawfrontApplication for the same first-wins
// TryAdd reason as the publisher above.
builder.Services.AddSingleton<ChatPushQueue>();
builder.Services.AddSingleton<IChatPushDispatcher>(sp => sp.GetRequiredService<ChatPushQueue>());
builder.Services.AddHostedService<ChatPushWorker>();

// FCM sending, one Firebase app per audience — the provider and pet-parent apps
// are separate Firebase projects and a token from one is rejected by the other.
builder.Services.AddPawfrontFirebaseMessaging(builder.Configuration);

builder.Services
    .AddPawfrontAzureInfrastructure(builder.Configuration, builder.Environment)
    .AddPawfrontApplication()
    .AddPawfrontSqlInfrastructure(builder.Configuration)
    // AddPawfrontApplication registers the WHOLE Application graph — bookings,
    // events, availability, discovery — and much of it depends on the Cosmos
    // registries, so the Cosmos registration is not optional here even though
    // this host only reads one container of its own. Leaving it out fails DI
    // validation at startup rather than lazily, which is the good outcome.
    .AddPawfrontCosmosInfrastructure(builder.Configuration)
    // Two Firebase projects behind one policy — see AuthServiceCollectionExtensions
    // for why this host is the only one that needs that.
    .AddChatAuthentication(builder.Configuration);

// Identity resolution. The resolver is context-free so the hub can call it with
// Hub.Context.User (hub methods run outside any HTTP request); the scoped wrapper
// caches the lookup for the duration of a REST request.
builder.Services.AddScoped<IChatParticipantResolver, ChatParticipantResolver>();
builder.Services.AddScoped<ICurrentChatParticipant, CurrentChatParticipant>();

// SignalR. With a connection string this runs in Azure SignalR's DEFAULT mode:
// the hub stays here and the service proxies connections, which is what lets one
// hub reach both apps. Without one it falls back to self-hosted SignalR, so a
// developer can run chat end to end without provisioning anything — the same
// "configured or local" switch AzureKeyVault:Enabled and the Application Insights
// connection string already use.
var signalR = builder.Services.AddSignalR(options =>
{
    // Surface the real message on a HubException instead of "An unexpected error
    // occurred". The hub only ever throws it with text meant for the caller.
    options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

var azureSignalRConnectionString = builder.Configuration["Azure:SignalR:ConnectionString"];
if (!string.IsNullOrWhiteSpace(azureSignalRConnectionString))
{
    signalR.AddAzureSignalR(azureSignalRConnectionString);
}

var app = builder.Build();

app.UseExceptionHandler();

if (app.Configuration.GetValue("Api:UseHttpsRedirection", false))
{
    app.UseHttpsRedirection();
}

app.MapOpenApi();
app.UseAuthentication();
app.UseAuthorization();
// AFTER authentication: the limiter partitions on the caller's uid claim, which
// does not exist until the token has been validated.
app.UseRateLimiter();
app.UseMiddleware<ChatTelemetryEnrichmentMiddleware>();

var api = app.MapGroup("/api/v1").RequireAuthorization(AuthServiceCollectionExtensions.ChatUserPolicy);

api.MapHealthEndpoints();
api.MapChatIdentityEndpoints();
api.MapConversationEndpoints();
api.MapChatMessageEndpoints();
api.MapChatAttachmentEndpoints();
api.MapChatBlockEndpoints();
api.MapBlobImageEndpoints();

// Outside the /api/v1 group: the path prefix is what AuthServiceCollectionExtensions
// keys its query-string token fallback on, and a WebSocket handshake cannot carry
// an Authorization header. Authorisation is applied here rather than inherited.
app.MapHub<ChatHub>("/hubs/chat")
    .RequireAuthorization(AuthServiceCollectionExtensions.ChatUserPolicy);

app.Run();
