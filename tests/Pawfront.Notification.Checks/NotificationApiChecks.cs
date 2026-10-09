using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pawfront.Application.Notifications;
using Pawfront.Infrastructure.Firebase;

internal static class NotificationApiChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception(name);
            checks++;
        }
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication("Bearer")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Bearer", _ => { })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Google", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("test", policy =>
            policy.AddAuthenticationSchemes("Bearer", "Google").RequireAuthenticatedUser()));
        var reader = new TestInboxReader();
        builder.Services.AddSingleton<INotificationInboxReader>(reader);
        builder.Services.AddSingleton<INotificationReadStateStore>(reader);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        Pawfront.Api.Endpoints.NotificationEndpoints.MapNotificationEndpoints(
            app.MapGroup("/provider/api/v1").RequireAuthorization("test"));
        Pawfront.PetParentApi.Endpoints.NotificationEndpoints.MapNotificationEndpoints(
            app.MapGroup("/parent/api/v1").RequireAuthorization("test"));
        await app.StartAsync();
        var client = app.GetTestClient();
        foreach (var (host, audience) in new[] { ("provider", NotificationAudience.Provider), ("parent", NotificationAudience.PetParent) })
        {
            var path = $"/{host}/api/v1/notifications";
            client.DefaultRequestHeaders.Authorization = null;
            Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized, "Anonymous request rejected");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "raw-google");
            Check((await client.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized, "Raw Google subject cannot select Firebase inbox");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "user-a");
            var response = await client.GetAsync(path + "?firebaseUid=user-b&audience=other");
            Check(response.IsSuccessStatusCode && reader.Uid == "user-a" && reader.Audience == audience && reader.Take == 20,
                "Authenticated UID and fixed host audience override query parameters");
            Check(response.Headers.CacheControl?.NoStore == true, "Private inbox response is not cached");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Check(body.RootElement.GetProperty("success").GetBoolean()
                && body.RootElement.GetProperty("data").GetProperty("items").GetArrayLength() == 0
                && body.RootElement.GetProperty("data").GetProperty("nextCursor").ValueKind == JsonValueKind.Null,
                "Empty inbox response envelope");
            foreach (var query in new[] { "?take=0", "?take=101", "?take=-1", "?take=abc", "?cursor=invalid" })
                Check((await client.GetAsync(path + query)).StatusCode == HttpStatusCode.BadRequest, "Invalid pagination rejected");
            var position = new FirestoreNotificationReader.InboxCursor(audience, "user-a", DateTime.UtcNow.Ticks, "document-id");
            var cursor = FirestoreNotificationReader.EncodeCursor(position);
            Check(FirestoreNotificationReader.DecodeCursor(cursor, audience, "user-a") == position, "Cursor preserves timestamp and tie-breaker");
            Check((await client.GetAsync(path + "?take=5&cursor=" + cursor)).IsSuccessStatusCode
                && reader.Take == 5 && reader.Cursor == cursor, "Page size and cursor forwarded");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "user-b");
            Check((await client.GetAsync(path + "?cursor=" + cursor)).StatusCode == HttpStatusCode.BadRequest, "Cursor cannot cross users");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "user-a");
            reader.Failure = new IOException("private service configuration");
            response = await client.GetAsync(path);
            Check(response.StatusCode == HttpStatusCode.ServiceUnavailable
                && !(await response.Content.ReadAsStringAsync()).Contains("private service configuration"), "Storage failure returns safe 503");
            reader.Failure = null;
            foreach (var suffix in new[] { "/one/read", "/read-all" })
            {
                client.DefaultRequestHeaders.Authorization = null;
                Check((await client.PostAsync(path + suffix, null)).StatusCode == HttpStatusCode.Unauthorized, "Anonymous write rejected");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "raw-google");
                Check((await client.PostAsync(path + suffix, null)).StatusCode == HttpStatusCode.Unauthorized, "Google write rejected");
            }
            reader.Items[(audience, "user-a", "one")] = false;
            reader.Items[(audience, "user-a", "two")] = false;
            reader.Items[(audience, "user-b", "one")] = false;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "user-a");
            for (var attempt = 0; attempt < 2; attempt++)
                Check((await client.PostAsync(path + "/one/read?firebaseUid=user-b", null)).StatusCode == HttpStatusCode.NoContent, "Single read is idempotent");
            using var updated = JsonDocument.Parse(await (await client.GetAsync(path)).Content.ReadAsStringAsync());
            Check(updated.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("isRead").GetBoolean(), "GET returns persisted read status");
            Check(!reader.Items[(audience, "user-b", "one")] && !reader.Items[(audience, "user-a", "two")], "Single read only updates caller's selected notification");
            Check((await client.PostAsync(path + "/missing/read", null)).StatusCode == HttpStatusCode.NotFound, "Missing notification returns 404");
            Check((await client.PostAsync(path + "/read-all", null)).StatusCode == HttpStatusCode.NoContent, "Read all succeeds");
            Check(reader.Items[(audience, "user-a", "two")] && !reader.Items[(audience, "user-b", "one")], "Read all isolates caller");
            reader.Failure = new IOException("private service configuration");
            foreach (var suffix in new[] { "/one/read", "/read-all" })
            {
                var failed = await client.PostAsync(path + suffix, null);
                Check(failed.StatusCode == HttpStatusCode.ServiceUnavailable
                    && !(await failed.Content.ReadAsStringAsync()).Contains("private service configuration"), "Write failure returns safe 503");
            }
            reader.Failure = null;
        }
        await app.StopAsync();
        return checks;
    }

    private sealed class TestInboxReader : INotificationInboxReader, INotificationReadStateStore
    {
        public Dictionary<(NotificationAudience Audience, string Uid, string Id), bool> Items = [];
        public Task<bool> MarkReadAsync(NotificationAudience audience, string uid, string id, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            if (!Items.ContainsKey((audience, uid, id))) return Task.FromResult(false);
            Items[(audience, uid, id)] = true;
            return Task.FromResult(true);
        }
        public Task MarkAllReadAsync(NotificationAudience audience, string uid, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            foreach (var key in Items.Keys.Where(k => k.Audience == audience && k.Uid == uid).ToArray()) Items[key] = true;
            return Task.CompletedTask;
        }
        public NotificationAudience Audience;
        public string? Uid, Cursor;
        public int Take;
        public Exception? Failure;
        public Task<NotificationInboxPage> ListAsync(NotificationAudience audience, string firebaseUserId, int take,
            string? cursor, CancellationToken cancellationToken)
        {
            FirestoreNotificationReader.ValidateRequest(firebaseUserId, take);
            FirestoreNotificationReader.DecodeCursor(cursor, audience, firebaseUserId);
            if (Failure is not null) throw Failure;
            (Audience, Uid, Take, Cursor) = (audience, firebaseUserId, take, cursor);
            return Task.FromResult(new NotificationInboxPage(Items.Where(x => x.Key.Audience == audience && x.Key.Uid == firebaseUserId)
                .Select(x => new NotificationInboxItem(x.Key.Id, "Title", "Body", "type", new Dictionary<string, string>(), DateTimeOffset.UtcNow, x.Value)).ToArray(), null));
        }
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var token = Request.Headers.Authorization.ToString().Replace("Bearer ", "");
            var valid = Scheme.Name == "Google" ? token == "raw-google" : token is "user-a" or "user-b";
            return Task.FromResult(valid
                ? AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim("user_id", token)], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
        }
    }
}
