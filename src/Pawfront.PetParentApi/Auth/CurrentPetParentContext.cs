using Pawfront.Application.ParentOnboarding;

namespace Pawfront.PetParentApi.Auth;

/// <summary>
/// Scoped-per-request resolver. The first call hits SQL to map the JWT's
/// <c>sub</c>/<c>user_id</c> claim → <c>Parent.ParentAuthIdentities</c> →
/// <c>PetParentId</c>; subsequent calls in the same request return the
/// cached value so multiple ownership filters in one pipeline pay the cost
/// once.
/// </summary>
internal sealed class CurrentPetParentContext(
    IHttpContextAccessor httpContextAccessor,
    IPetParentOwnershipReader ownershipReader) : ICurrentPetParentContext
{
    private Guid? cachedPetParentId;
    private bool resolved;

    public async Task<Guid?> GetPetParentIdAsync(CancellationToken cancellationToken)
    {
        if (resolved)
        {
            return cachedPetParentId;
        }

        var httpContext = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "Cannot resolve the current pet parent — no HttpContext is available.");

        var firebaseUserId = FirebaseClaims.GetFirebaseUserId(httpContext.User);
        cachedPetParentId = await ownershipReader
            .GetPetParentIdByFirebaseUserIdAsync(firebaseUserId, cancellationToken);
        resolved = true;
        return cachedPetParentId;
    }
}
