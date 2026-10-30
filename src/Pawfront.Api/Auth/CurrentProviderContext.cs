using Pawfront.Application.ProviderOnboarding;

namespace Pawfront.Api.Auth;

internal interface ICurrentProviderContext
{
    Task<Guid?> GetProviderIdAsync(CancellationToken cancellationToken);
}

/// <summary>Resolves the authenticated provider once per HTTP request.</summary>
internal sealed class CurrentProviderContext(
    IHttpContextAccessor accessor,
    IProviderOnboardingService onboarding) : ICurrentProviderContext
{
    private bool resolved;
    private Guid? providerId;

    public async Task<Guid?> GetProviderIdAsync(CancellationToken cancellationToken)
    {
        if (resolved) return providerId;
        var user = accessor.HttpContext?.User
            ?? throw new InvalidOperationException("No current HTTP request.");
        try
        {
            var identity = await onboarding.ResolveProviderByFirebaseUidAsync(
                FirebaseClaims.GetFirebaseUserId(user), cancellationToken);
            providerId = identity.HasProfile ? identity.ProviderId : null;
        }
        catch (ProviderAuthIdentityForFirebaseUserNotFoundException)
        {
            providerId = null;
        }
        resolved = true;
        return providerId;
    }
}
