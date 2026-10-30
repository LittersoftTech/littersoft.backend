using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.TestHost;
using Pawfront.Application.Bookings;
using Pawfront.Application.Notifications;
using Pawfront.Contracts.ProviderOnboarding;
using Pawfront.Infrastructure.Firebase;

var owner = Guid.NewGuid(); var other = Guid.NewGuid(); var bookingId = Guid.NewGuid(); var missing = Guid.NewGuid();
var checks = 0; var handlerCalls = 0;
var bookingOwner = other;
UpdateBookingStatusCommand? cancellation = null;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
var apiAssembly = Assembly.Load("Pawfront.Api");
var builder = WebApplication.CreateBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseTestServer();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
var callerHasProfile = true;
object? Handle(Type type, MethodInfo method, object?[] args)
{
    if (method.Name == "ResolveProviderByFirebaseUidAsync")
        return Task.FromResult(new ResolveProviderByFirebaseUidResponse(Guid.NewGuid(), callerHasProfile ? owner : null,
            "provider-a", "test@example.invalid", true, null, "ProfileCompleted", callerHasProfile, null, null, true));
    if (method.Name == "GetAsync" && type == typeof(IBookingService))
        return Task.FromResult((Guid)args[0]! == missing ? null : Make<BookingResult>(new() { ["BookingId"] = bookingId, ["ProviderId"] = bookingOwner }));
    if (method.Name == "GetAsync" && type == typeof(INightStayBookingService))
        return Task.FromResult((Guid)args[0]! == missing ? null : Make<NightStayBookingResult>(new() { ["NightStayBookingId"] = bookingId, ["ProviderId"] = bookingOwner }));
    if (bookingOwner == owner && method.Name == "ListStatusHistoryAsync")
        return Task.FromResult<IReadOnlyList<BookingStatusHistoryEntry>>([]);
    if (bookingOwner == owner && method.Name == "UpdateStatusAsync" && type == typeof(IBookingService))
    {
        cancellation = (UpdateBookingStatusCommand)args[0]!;
        return Task.FromResult(Make<BookingResult>(new() { ["BookingId"] = bookingId, ["ProviderId"] = owner }));
    }
    handlerCalls++;
    throw new Exception($"Unauthorized request reached {type.Name}.{method.Name}");
}
foreach (var type in typeof(IBookingService).Assembly.GetTypes().Where(t => t.IsInterface && !t.ContainsGenericParameters))
{
    var proxy = (Stub)DispatchProxy.Create(type, typeof(Stub));
    proxy.Handler = (method, args) => Handle(type, method, args);
    builder.Services.AddSingleton(type, proxy);
}
var contextType = apiAssembly.GetType("Pawfront.Api.Auth.ICurrentProviderContext")!;
builder.Services.AddScoped(contextType, apiAssembly.GetType("Pawfront.Api.Auth.CurrentProviderContext")!);
await using var app = builder.Build();
app.Use(async (http, next) =>
{
    http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "provider-a")], "Test"));
    await next(http);
});
app.UseAuthorization();
var api = app.MapGroup("/api/v1").RequireAuthorization();
foreach (var (type, method) in new[] {
    ("BookingEndpoints", "MapBookingEndpoints"), ("NightStayBookingEndpoints", "MapProviderNightStayBookingEndpoints"),
    ("ReviewEndpoints", "MapProviderReviewEndpoints"), ("InvoiceEndpoints", "MapInvoiceEndpoints") })
    apiAssembly.GetType($"Pawfront.Api.Endpoints.{type}")!.GetMethod(method)!.Invoke(null, [api]);
await app.StartAsync();
using var client = app.GetTestClient();
foreach (var path in new[] {
    $"/bookings/{bookingId}", $"/night-stay-bookings/{bookingId}",
    $"/providers/{owner}/night-stay-bookings/{bookingId}",
    $"/providers/{other}/bookings/", $"/providers/{other}/night-stay-bookings/" })
{
    using var response = await client.GetAsync("/api/v1" + path);
    Check(response.StatusCode == HttpStatusCode.Forbidden, $"Cross-account read must return 403: {path}");
}
foreach (var night in new[] { false, true })
foreach (var routeProvider in new[] { owner, other })
{
    var prefix = $"/api/v1/providers/{routeProvider}/{(night ? "night-stay-bookings" : "bookings")}/{bookingId}";
    foreach (var suffix in new[] { "status-history", "terms-changes", "evidence", "invoice" })
    {
        using var response = await client.GetAsync(prefix + "/" + suffix);
        Check(response.StatusCode == HttpStatusCode.Forbidden, $"Cross-account subresource must return 403: {suffix}");
    }
    foreach (var suffix in new[] { "accept", "decline", "start-job", "wait-for-customer", "start-job/verify",
        "complete", "paid", "cancel", "no-show", "modifications", "modifications/accept", "modifications/decline",
        "cash-not-received", "location", "rating" })
    {
        using var response = await client.PostAsJsonAsync(prefix + "/" + suffix, new { });
        Check(response.StatusCode == HttpStatusCode.Forbidden, $"Cross-account action must return 403: {suffix}");
    }
}
using (var response = await client.PostAsJsonAsync($"/api/v1/bookings/{bookingId}/cancel", new { petParentId = owner }))
    Check(response.StatusCode == HttpStatusCode.Forbidden, "Legacy cancel rejects another provider's booking");
using (var response = await client.PostAsJsonAsync($"/api/v1/providers/{owner}/bookings/bulk-cancel",
    new { bookings = new[] { new { bookingId, bookingType = "SingleDay" } } }))
    Check(response.StatusCode == HttpStatusCode.Forbidden, "Bulk cancel rejects foreign bookings before any mutation");
using (var response = await client.GetAsync($"/api/v1/bookings/{missing}"))
    Check(response.StatusCode == HttpStatusCode.NotFound, "Unknown booking remains 404");
callerHasProfile = false;
using (var response = await client.GetAsync($"/api/v1/bookings/{bookingId}"))
    Check(response.StatusCode == HttpStatusCode.Forbidden, "Missing provider profile is forbidden");
Check(handlerCalls == 0, "Forbidden requests never reach detail or action services");
callerHasProfile = true;
bookingOwner = owner;
foreach (var path in new[] { "bookings", "night-stay-bookings" })
{
    using var response = await client.GetAsync($"/api/v1/providers/{owner}/{path}/{bookingId}/status-history");
    Check(response.StatusCode == HttpStatusCode.OK, "Owner can still read their own booking history");
}
using (var response = await client.PostAsJsonAsync($"/api/v1/bookings/{bookingId}/cancel", new { petParentId = other }))
{
    Check(response.StatusCode == HttpStatusCode.OK, "Owner can use the legacy cancellation route");
    Check(cancellation is { Actor: BookingStatusActor.Provider, NewStatus: BookingStatuses.ProviderCancelled }
        && cancellation.ActorId == owner, "Legacy cancellation uses signed-in provider actor and ignores body owner");
}
await app.StopAsync();

var tokens = new TokenReader(); var push = new CapturingSender();
var guarded = new ProviderOwnedPushSender(push, tokens);
var message = new PushMessage(NotificationAudience.Provider, ["old-token", "current-token"], "Title", "Body", new Dictionary<string, string>(), null, "provider-a");
tokens.Tokens = ["current-token"];
await guarded.SendAsync(message, default);
Check(push.Last!.Tokens.SequenceEqual(["current-token"]), "Queued stale tokens are filtered immediately before send");
var sent = push.Calls;
tokens.Tokens = [];
await guarded.SendAsync(message, default);
Check(push.Calls == sent, "Deleted account or transferred device receives no push");
await guarded.SendAsync(message with { FirebaseUserId = null }, default);
Check(push.Calls == sent, "A provider push without verified recipient identity is suppressed");
tokens.Fail = true;
var failed = await guarded.SendAsync(message, default);
Check(failed.IsFailure && push.Calls == sent, "Ownership-check failure does not send a stale push");
await guarded.SendAsync(message with { Audience = NotificationAudience.PetParent }, default);
Check(push.Calls == sent + 1, "Parent push path remains functional");
if (args.Contains("--sql"))
{
    checks += await SqlChecks.RunAsync();
    checks += await AccountDeletionSqlChecks.RunAsync();
}
Console.WriteLine($"Passed {checks} provider booking access and token ownership checks.");

static T Make<T>(Dictionary<string, object?> values)
{
    var constructor = typeof(T).GetConstructors().Single();
    return (T)constructor.Invoke(constructor.GetParameters().Select(p => values.GetValueOrDefault(p.Name!)
        ?? (p.HasDefaultValue ? p.DefaultValue : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)).ToArray());
}
public class Stub : DispatchProxy
{
    public Func<MethodInfo, object?[], object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args!);
}
sealed class TokenReader : IProviderPushTokenReader
{
    public IReadOnlyList<string> Tokens = [];
    public bool Fail;
    public Task<IReadOnlyList<string>> GetCurrentTokensAsync(string uid, CancellationToken ct)
        => Fail ? throw new InvalidOperationException("Unavailable") : Task.FromResult(Tokens);
}
sealed class CapturingSender : IPushSender
{
    public PushMessage? Last;
    public int Calls;
    public Task<PushSendResult> SendAsync(PushMessage message, CancellationToken ct)
    { Calls++; Last = message; return Task.FromResult(new PushSendResult(message.Tokens.Count, [], null)); }
}
