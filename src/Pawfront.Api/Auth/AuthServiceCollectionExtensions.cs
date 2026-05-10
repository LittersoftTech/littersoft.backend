using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Pawfront.Api.Auth;

internal static class AuthServiceCollectionExtensions
{
    public const string FirebaseUserPolicy = "FirebaseUser";

    public static IServiceCollection AddPawfrontAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var firebaseProjectId = configuration["Firebase:ProjectId"];
        if (string.IsNullOrWhiteSpace(firebaseProjectId))
        {
            throw new InvalidOperationException("Firebase:ProjectId is required to validate Firebase ID tokens.");
        }

        services
            .AddAuthentication()
            .AddJwtBearer(options =>
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
            })
            .AddScheme<GoogleIdTokenAuthenticationOptions, GoogleIdTokenAuthenticationHandler>(
                GoogleIdTokenAuthenticationDefaults.AuthenticationScheme,
                options =>
                {
                    options.Audiences = configuration
                        .GetSection("Firebase:GoogleClientIds")
                        .Get<string[]>() ?? [];
                });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(FirebaseUserPolicy, policy =>
            {
                policy.AddAuthenticationSchemes(
                    JwtBearerDefaults.AuthenticationScheme,
                    GoogleIdTokenAuthenticationDefaults.AuthenticationScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireAssertion(context =>
                    context.User.HasClaim(claim => claim.Type is "user_id" or "sub"));
            });
        });

        return services;
    }
}
