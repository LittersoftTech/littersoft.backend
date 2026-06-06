namespace Pawfront.PetParentApi.Auth;

/// <summary>
/// Resolves the caller's <c>PetParentId</c> from their Firebase JWT and
/// caches the result for the lifetime of the request. Used by the ownership
/// endpoint filters and any handler that needs to act on behalf of the
/// authenticated parent without trusting a route or body id.
/// </summary>
internal interface ICurrentPetParentContext
{
    /// <summary>
    /// Returns the caller's <c>PetParentId</c>, or null when:
    /// <list type="bullet">
    ///   <item>the auth identity row doesn't exist (token doesn't correspond to a parent), or</item>
    ///   <item>the auth identity exists but the parent hasn't completed
    ///         <c>POST /parent-onboarding/profile</c> yet (PetParentId is unset).</item>
    /// </list>
    /// Callers translate null into the appropriate 403 response.
    /// </summary>
    Task<Guid?> GetPetParentIdAsync(CancellationToken cancellationToken);
}
