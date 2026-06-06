using Microsoft.AspNetCore.Authentication;

namespace Pawfront.PetParentApi.Auth;

internal sealed class GoogleIdTokenAuthenticationOptions : AuthenticationSchemeOptions
{
    public IReadOnlyCollection<string> Audiences { get; set; } = [];
}
