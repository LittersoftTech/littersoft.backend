using Pawfront.Contracts.ParentOnboarding;

namespace Pawfront.Application.ParentOnboarding;

/// <summary>
/// Pet-parent account lifecycle operations that span more than one store.
/// </summary>
public interface IParentAccountService
{
    /// <summary>
    /// Deletes a pet-parent account by <b>anonymising</b> it: the personal fields
    /// are scrubbed (the name becomes "Deleted User"), their pets are anonymised
    /// in place, the Firebase login is severed, and the operational data + stored
    /// media (device tokens, OTPs, identity document, photo galleries) are removed.
    /// <para>
    /// The PetParentId and everything referencing it are <b>retained</b> —
    /// bookings, night-stay bookings, the events they organised, their event
    /// tickets, and the payment ledger — so neither party loses their history.
    /// Irreversible, but idempotent (a second call reports <c>WasAlreadyDeleted</c>).
    /// </para>
    /// Throws <see cref="PetParentNotFoundException"/> when the parent row is
    /// missing.
    /// </summary>
    Task<DeletePetParentAccountResponse> DeleteAsync(
        Guid petParentId,
        CancellationToken cancellationToken);
}
