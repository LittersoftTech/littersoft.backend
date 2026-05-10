using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Pawfront.Api.Auth;

internal sealed class GoogleIdTokenAuthenticationHandler(
    IOptionsMonitor<GoogleIdTokenAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<GoogleIdTokenAuthenticationOptions>(options, logger, encoder)
{
    private static readonly string[] GoogleIssuers =
    [
        "https://accounts.google.com",
        "accounts.google.com"
    ];

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authorizationValues))
        {
            return AuthenticateResult.NoResult();
        }

        var authorizationHeader = authorizationValues.ToString();
        if (string.IsNullOrWhiteSpace(authorizationHeader))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorizationHeader["Bearer ".Length..].Trim()
            : authorizationHeader.Trim();

        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        if (Options.Audiences.Count == 0)
        {
            return AuthenticateResult.NoResult();
        }

        if (!IsGoogleIssuedToken(token))
        {
            return AuthenticateResult.NoResult();
        }

        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(
                token,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = Options.Audiences
                });

            if (payload is null)
            {
                return AuthenticateResult.Fail("Invalid token.");
            }

            var claims = BuildClaims(payload);
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return AuthenticateResult.Success(ticket);
        }
        catch (Exception exception)
        {
            return AuthenticateResult.Fail($"Invalid token: {exception.Message}");
        }
    }

    private static bool IsGoogleIssuedToken(string token)
    {
        if (token.Count(character => character == '.') != 2)
        {
            return false;
        }

        try
        {
            var jwt = new JwtSecurityToken(token);
            return GoogleIssuers.Contains(jwt.Issuer, StringComparer.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static Claim[] BuildClaims(GoogleJsonWebSignature.Payload payload)
    {
        var claims = new List<Claim>
        {
            new("sub", payload.Subject),
            new("user_id", payload.Subject),
            new("firebase", """{"sign_in_provider":"google.com"}"""),
            new(ClaimTypes.NameIdentifier, payload.Subject)
        };

        AddIfPresent(claims, "email", payload.Email);
        AddIfPresent(claims, ClaimTypes.Email, payload.Email);
        AddIfPresent(claims, ClaimTypes.Name, payload.Email);
        AddIfPresent(claims, "name", payload.Name);
        AddIfPresent(claims, "picture", payload.Picture);
        claims.Add(new Claim("email_verified", payload.EmailVerified.ToString()));

        return claims.ToArray();
    }

    private static void AddIfPresent(List<Claim> claims, string type, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            claims.Add(new Claim(type, value));
        }
    }
}
