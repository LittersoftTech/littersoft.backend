using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Contracts.ParentOnboarding;

static class AdminApiChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("Bearer")
            .AddScheme<AuthenticationSchemeOptions, Auth>("Bearer", _ => { })
            .AddScheme<AuthenticationSchemeOptions, Auth>("Google", _ => { });
        builder.Services.AddAuthorization();
        var service = new ReviewService();
        builder.Services.AddSingleton<IParentIdentityReviewService>(service);
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        var assembly = System.Reflection.Assembly.Load("Pawfront.Api");
        assembly.GetType("Pawfront.Api.Endpoints.ParentIdentityReviewEndpoints")!
            .GetMethod("MapParentIdentityReviewEndpoints")!.Invoke(null, [app.MapGroup("/api/v1")]);
        await app.StartAsync();
        var client = app.GetTestClient();
        const string path = "/api/v1/admin/parent-identities";
        foreach (var (authorization, expected) in new[] { ("", HttpStatusCode.Unauthorized), ("Bearer parent", HttpStatusCode.Forbidden), ("Bearer provider", HttpStatusCode.Forbidden), ("Google admin", HttpStatusCode.Unauthorized), ("Bearer admin", HttpStatusCode.OK) })
        {
            client.DefaultRequestHeaders.Remove("Authorization");
            if (authorization.Length > 0) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
            Check((await client.GetAsync(path + "?admin=true")).StatusCode == expected, "Review queue requires Firebase admin claim: " + authorization);
            var response = await client.PostAsJsonAsync(path + "/" + Guid.NewGuid() + "/review", new ReviewParentIdentityRequest("Accept", DateTimeOffset.UtcNow));
            Check(response.StatusCode == (expected == HttpStatusCode.OK ? HttpStatusCode.NoContent : expected), "Review mutation requires Firebase admin claim: " + authorization);
        }
        Check(service.Reads == 1 && service.Writes == 1, "Only admin reaches identity document storage");
        service.Conflict = true;
        Check((await client.PostAsJsonAsync(path + "/" + Guid.NewGuid() + "/review", new ReviewParentIdentityRequest("Accept", DateTimeOffset.UtcNow))).StatusCode == HttpStatusCode.Conflict, "Stale review returns 409");
        await app.StopAsync();
        return checks;
    }
    sealed class ReviewService : IParentIdentityReviewService
    {
        public int Reads, Writes; public bool Conflict;
        public Task<IReadOnlyList<ParentIdentityReviewItem>> ListAsync(string status, CancellationToken ct) { Reads++; return Task.FromResult<IReadOnlyList<ParentIdentityReviewItem>>([]); }
        public Task ReviewAsync(Guid id, ReviewParentIdentityRequest request, CancellationToken ct) { if (Conflict) throw new IdentityReviewConflictException(); Writes++; return Task.CompletedTask; }
    }
    sealed class Auth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith(Scheme.Name + " ", StringComparison.Ordinal)) return Task.FromResult(AuthenticateResult.NoResult());
            var name = header[(Scheme.Name.Length + 1)..];
            var claims = new List<Claim> { new("user_id", name) };
            if (name == "admin") claims.Add(new("admin", "true"));
            return Task.FromResult(AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
