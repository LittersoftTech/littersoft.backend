using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Pawfront.Application.Chat;

namespace Pawfront.ChatApi.Auth;

/// <summary>
/// Authentication for the chat host, which is the only host that has to accept
/// BOTH mobile apps.
///
/// A conversation spans a provider and a pet parent, and those two sign in
/// against different Firebase projects (<c>littersoftprovider</c> and
/// <c>pawfrontparent-89296</c>). Since one hub has to serve both ends of the same
/// thread, one host has to validate both projects' tokens — hence two named
/// <see cref="JwtBearerDefaults.AuthenticationScheme"/> registrations behind a
/// single policy, rather than the single-project setup the two CRUD hosts use.
///
/// The policy lists both schemes, so ASP.NET Core authenticates against each in
/// turn and merges the results. A parent's token simply fails issuer validation
/// on the provider scheme and contributes no identity; the parent scheme then
/// succeeds. <b>Expect one "issuer validation failed" debug line per request</b> —
/// it is the mechanism working, not a misconfiguration.
/// </summary>
internal static class AuthServiceCollectionExtensions
{
    public const string ChatUserPolicy = "ChatUser";

    public const string ProviderScheme = "ProviderFirebase";
    public const string ParentScheme = "ParentFirebase";

    /// <summary>
    /// Stamped onto the validated identity by whichever scheme succeeded, so no
    /// downstream code ever has to ask which one fired — it reads one claim.
    /// </summary>
    public const string AudienceClaimType = "pawfront_audience";

    /// <summary>
    /// Everything under here is a SignalR hub, and therefore the only place a
    /// query-string token is honoured. See <see cref="ReadTokenFromQueryString"/>.
    /// </summary>
    public const string HubPathPrefix = "/hubs";

    public static IServiceCollection AddChatAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var providerProjectId = configuration["Firebase:ProviderProjectId"];
        if (string.IsNullOrWhiteSpace(providerProjectId))
        {
            throw new InvalidOperationException(
                "Firebase:ProviderProjectId is required to validate provider-app ID tokens.");
        }

        var parentProjectId = configuration["Firebase:ParentProjectId"];
        if (string.IsNullOrWhiteSpace(parentProjectId))
        {
            throw new InvalidOperationException(
                "Firebase:ParentProjectId is required to validate pet-parent-app ID tokens.");
        }

        if (string.Equals(providerProjectId, parentProjectId, StringComparison.Ordinal))
        {
            // Both schemes would accept the same tokens and the audience claim
            // would be decided by scheme ordering rather than by who the caller
            // actually is — a silent authorisation bug. Fail at startup instead.
            throw new InvalidOperationException(
                "Firebase:ProviderProjectId and Firebase:ParentProjectId must be different projects.");
        }

        services
            .AddAuthentication()
            .AddJwtBearer(
                ProviderScheme,
                options => ConfigureFirebaseScheme(options, providerProjectId, ChatParticipantType.Provider))
            .AddJwtBearer(
                ParentScheme,
                options => ConfigureFirebaseScheme(options, parentProjectId, ChatParticipantType.PetParent));

        services.AddAuthorization(options =>
        {
            options.AddPolicy(ChatUserPolicy, policy =>
            {
                policy.AddAuthenticationSchemes(ProviderScheme, ParentScheme);
                policy.RequireAuthenticatedUser();
                // The uid claim is what every downstream lookup keys off, and the
                // audience claim is what says which table to look in. Requiring
                // both here means no handler has to re-check them.
                policy.RequireAssertion(context =>
                    context.User.HasClaim(claim => claim.Type is "user_id" or "sub")
                    && context.User.HasClaim(claim => claim.Type == AudienceClaimType));
            });
        });

        return services;
    }

    private static void ConfigureFirebaseScheme(
        JwtBearerOptions options,
        string firebaseProjectId,
        ChatParticipantType participantType)
    {
        options.Authority = $"https://securetoken.google.com/{firebaseProjectId}";
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"https://securetoken.google.com/{firebaseProjectId}",
            ValidateAudience = true,
            ValidAudience = firebaseProjectId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = "user_id"
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ReadTokenFromQueryString,
            OnTokenValidated = context =>
            {
                // The whole point of two schemes: record WHICH one accepted this
                // token, so identity resolution downstream is a claim read rather
                // than a guess or a double lookup against both tables.
                if (context.Principal?.Identity is ClaimsIdentity identity)
                {
                    identity.AddClaim(new Claim(AudienceClaimType, participantType.ToSqlValue()));
                }

                return Task.CompletedTask;
            }
        };
    }

    /// <summary>
    /// Accepts the bearer token from an <c>access_token</c> query parameter, but
    /// only on hub paths.
    ///
    /// A WebSocket handshake cannot carry an <c>Authorization</c> header, so every
    /// SignalR client — including <c>signalr_netcore</c>, which the Flutter apps
    /// use — falls back to the query string. Without this, the hub is unreachable
    /// while the REST endpoints work fine, which is a confusing failure to debug.
    ///
    /// Scoped to <see cref="HubPathPrefix"/> on purpose: a token in a URL ends up
    /// in access logs and browser history, so the REST surface stays header-only.
    /// </summary>
    private static Task ReadTokenFromQueryString(MessageReceivedContext context)
    {
        if (!context.HttpContext.Request.Path.StartsWithSegments(HubPathPrefix))
        {
            return Task.CompletedTask;
        }

        var accessToken = context.Request.Query["access_token"];
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            context.Token = accessToken;
        }

        return Task.CompletedTask;
    }
}
