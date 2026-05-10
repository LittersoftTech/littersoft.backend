using Microsoft.AspNetCore.Authentication;

namespace Pawfront.Api.Auth;

internal sealed class GoogleIdTokenAuthenticationOptions : AuthenticationSchemeOptions
{
    public IReadOnlyCollection<string> Audiences { get; set; } = [];
}
