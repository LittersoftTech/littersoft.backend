namespace Pawfront.Application.ParentOnboarding;

/// <summary>
/// Narrow SQL reader used by the pet-parent host's ownership filters to map
/// JWT identity → owning <c>PetParentId</c>, and to resolve which parent owns
/// a given pet. Returning <c>null</c> means "no row" in both cases — the
/// callers translate that into the appropriate HTTP response.
/// </summary>
public interface IPetParentOwnershipReader
{
    /// <summary>
    /// Resolves the caller's Firebase user id (sub/user_id claim) to their
    /// linked <c>PetParentId</c>. Returns null when the auth identity row
    /// doesn't exist (token doesn't correspond to a parent in our system)
    /// or when the auth identity exists but the parent hasn't completed
    /// their profile yet (PetParentId is still null).
    /// </summary>
    Task<Guid?> GetPetParentIdByFirebaseUserIdAsync(
        string firebaseUserId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the <c>PetParentId</c> that owns the given pet, or null if
    /// the pet row doesn't exist. Used by the per-pet ownership filter to
    /// distinguish "pet not found" (404) from "wrong owner" (403).
    /// </summary>
    Task<Guid?> GetOwningPetParentIdByPetIdAsync(
        Guid petId,
        CancellationToken cancellationToken);
}
