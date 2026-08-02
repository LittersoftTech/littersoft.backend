using Pawfront.Contracts.ProviderOnboarding;

namespace Pawfront.Application.ProviderOnboarding;

/// <summary>
/// Provider account lifecycle operations that span more than one store.
/// </summary>
public interface IProviderAccountService
{
    /// <summary>
    /// Deletes a provider account by <b>anonymising</b> it: the personal fields
    /// are scrubbed (the name becomes "Deleted Provider"), the Firebase login is
    /// severed, the account is permanently disabled so no new bookings can be
    /// made, the bookable services are deactivated, and the provider's public
    /// Cosmos listing + profile blobs are removed.
    /// <para>
    /// The ProviderId and everything referencing it are <b>retained</b> —
    /// bookings, night-stay bookings, the events they organised, and the payment
    /// ledger — so neither party loses their history. Irreversible, but
    /// idempotent (a second call reports <c>WasAlreadyDeleted</c>).
    /// </para>
    /// Throws <see cref="ProviderProfileNotFoundException"/> when the provider
    /// row is missing.
    /// </summary>
    Task<DeleteProviderAccountResponse> DeleteAsync(
        Guid providerId,
        CancellationToken cancellationToken);
}
