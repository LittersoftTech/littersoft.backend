using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pawfront.Application.ParentOnboarding;
using Pawfront.PetParentApi.Auth;
using Pawfront.PetParentApi.Endpoints;

var reader = new EmailReader();
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddSingleton<IParentEmailStatusReader>(reader);
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddEmailStatusRateLimiting();
await using var app = builder.Build();
// Test-only IP control. The production endpoint uses the connection's observed IP.
app.Use(async (context, next) =>
{
    if (context.Request.Headers.TryGetValue("Test-IP", out var ip))
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapAuthEndpoints();
app.MapGroup("/api/v1").RequireAuthorization().MapAuthEndpoints();
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
var checks = 0;
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    checks++;
}
async Task<HttpResponseMessage> Post(string? email, string ip, bool versioned = false)
{
    using var request = new HttpRequestMessage(HttpMethod.Post,
        versioned ? "/api/v1/auth/email-status" : "/auth/email-status")
    { Content = JsonContent.Create(new { email }) };
    request.Headers.Add("Test-IP", ip);
    return await client.SendAsync(request);
}
foreach (var versioned in new[] { false, true })
{
    using var response = await Post("  PARENT@example.com  ", "192.0.2.1", versioned);
    Check(response.StatusCode == HttpStatusCode.OK, "Email status must allow anonymous callers on both routes");
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Check(body.RootElement.GetProperty("success").GetBoolean()
        && body.RootElement.GetProperty("data").GetProperty("registered").GetBoolean(), "Registered response envelope");
    Check(reader.LastEmail == "PARENT@example.com", "Email is trimmed before lookup");
    Check(response.Headers.CacheControl?.NoStore == true, "Email lookup must not be cached");
}
using (var response = await Post("new@example.com", "192.0.2.2"))
{
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Check(!body.RootElement.GetProperty("data").GetProperty("registered").GetBoolean(), "Unknown email returns false");
}
var readsBefore = reader.Reads;
foreach (var invalid in new string?[] { null, "", "  ", "bad", "user@", "user@localhost", "Name <user@example.com>", "a b@example.com", new string('a', 321) + "@example.com" })
{
    using var response = await Post(invalid, "192.0.2.3");
    Check(response.StatusCode == HttpStatusCode.BadRequest, "Invalid email must return 400");
}
Check(reader.Reads == readsBefore, "Invalid emails must not query the database");
for (var i = 0; i < 10; i++)
{
    using var response = await Post("new@example.com", "192.0.2.4", i % 2 == 0);
    Check(response.StatusCode == HttpStatusCode.OK, "First ten calls allowed");
}
readsBefore = reader.Reads;
using (var response = await Post("different@example.com", "192.0.2.4"))
{
    Check(response.StatusCode == HttpStatusCode.TooManyRequests, "Eleventh call returns 429 across route aliases and emails");
    Check(response.Headers.RetryAfter is not null, "429 includes retry delay");
    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Check(body.RootElement.GetProperty("error").GetProperty("code").GetString() == "RateLimitExceeded", "429 uses error envelope");
}
Check(reader.Reads == readsBefore, "Rate-limited call does not query the database");
using (var response = await Post("new@example.com", "192.0.2.5"))
    Check(response.StatusCode == HttpStatusCode.OK, "A different IP has its own allowance");
if (args.Contains("--sql")) checks += await SqlChecks.RunAsync();
await app.StopAsync();
Console.WriteLine($"Passed {checks} email-status HTTP and booking checks.");

sealed class EmailReader : IParentEmailStatusReader
{
    public string? LastEmail { get; private set; }
    public int Reads { get; private set; }
    public Task<bool> IsRegisteredAsync(string email, CancellationToken cancellationToken)
    {
        Reads++;
        LastEmail = email;
        return Task.FromResult(email.Equals("parent@example.com", StringComparison.OrdinalIgnoreCase));
    }
}
