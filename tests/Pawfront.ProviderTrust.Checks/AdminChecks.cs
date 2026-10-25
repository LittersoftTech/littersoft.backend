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
using Pawfront.Application.ProviderTrust;


static class AdminChecks
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
        builder.Services.AddSingleton<IProviderTrustService>(service);
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        var assembly = System.Reflection.Assembly.Load("Pawfront.Api");
        assembly.GetType("Pawfront.Api.Endpoints.ProviderTrustEndpoints")!
            .GetMethod("MapProviderTrustAdminEndpoints")!.Invoke(null, [app.MapGroup("/api/v1")]);
        await app.StartAsync();
        var client = app.GetTestClient();
        const string path = "/api/v1/admin/provider-trust";
        foreach (var (authorization, expected) in new[] { ("", HttpStatusCode.Unauthorized), ("Bearer parent", HttpStatusCode.Forbidden), ("Bearer provider", HttpStatusCode.Forbidden), ("Google admin", HttpStatusCode.Unauthorized), ("Bearer admin", HttpStatusCode.OK) })
        {
            client.DefaultRequestHeaders.Remove("Authorization");
            if (authorization.Length > 0) client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
            Check((await client.GetAsync(path + "?admin=true")).StatusCode == expected, "Review queue requires Firebase admin claim: " + authorization);
            var response = await client.PostAsJsonAsync(path + "/" + Guid.NewGuid() + "/review", new ReviewProviderDocumentRequest(Guid.NewGuid(), "Accept"));
            Check(response.StatusCode == (expected == HttpStatusCode.OK ? HttpStatusCode.OK : expected), "Review mutation requires Firebase admin claim: " + authorization);
            var medreg = await client.PostAsJsonAsync(path + "/" + Guid.NewGuid() + "/medreg", new RecordMedRegRequest(Guid.NewGuid(), true, "Lookup reference"));
            Check(medreg.StatusCode == expected, "MedReg mutation requires Firebase admin claim: " + authorization);
        }
        Check(service.Reads == 1 && service.Writes == 2, "Only admin reaches identity document storage");
        service.Conflict = true;
        Check((await client.PostAsJsonAsync(path + "/" + Guid.NewGuid() + "/review", new ReviewProviderDocumentRequest(Guid.NewGuid(), "Accept"))).StatusCode == HttpStatusCode.Conflict, "Stale review returns 409");
        await app.StopAsync();
        return checks;
    }
    sealed class ReviewService : IProviderTrustService
    {
        public Task SyncOfferingDocumentAsync(Guid id,string? url,CancellationToken ct)=>Task.CompletedTask;
        public int Reads, Writes; public bool Conflict;
        public Task<IReadOnlyDictionary<Guid,string>> GetBadgesAsync(IReadOnlyCollection<Guid> ids,CancellationToken ct)=>Task.FromResult<IReadOnlyDictionary<Guid,string>>(new Dictionary<Guid,string>());
        public Task<ProviderTrustRecord?> GetAsync(Guid id,CancellationToken ct)=>Task.FromResult<ProviderTrustRecord?>(null);
        public Task<IReadOnlyList<ProviderTrustRecord>> ListPendingAsync(CancellationToken ct) { Reads++; return Task.FromResult<IReadOnlyList<ProviderTrustRecord>>([]); }
        public Task<ProviderTrustRecord> ChangeAsync(Guid id,string action,string? url,Guid? revision,string? reason,string? reference,CancellationToken ct)
        { if (Conflict) throw new ProviderTrustConflictException("Stale revision"); Writes++; return Task.FromResult(new ProviderTrustRecord(id,Guid.NewGuid(),"PetSitter","Document","Added","https://private.invalid/document",null,null,DateTimeOffset.UtcNow)); }

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
