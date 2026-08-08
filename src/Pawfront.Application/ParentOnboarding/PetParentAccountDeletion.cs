using Pawfront.Contracts.ParentOnboarding;

namespace Pawfront.Application.ParentOnboarding;

/// <summary>
/// The outcome of the SQL half of a pet-parent account delete (an anonymise +
/// disable, not a row delete). SQL cannot reach Blob Storage, so the sproc also
/// hands back the media the parent still owns there; the
/// <see cref="IParentAccountService"/> orchestrator finishes the cleanup.
///
/// Unlike the provider delete there is no Cosmos leg — a pet parent owns no
/// Cosmos document. The events they organised do have one, and those events (and
/// their documents) are deliberately retained.
///
/// <see cref="BlobUrls"/> is empty when the account was already deleted — the
/// sproc is idempotent and there is nothing left to clean up.
/// </summary>
public sealed record PetParentAccountDeletionResult(
    DeletePetParentAccountResponse Summary,
    // Profile photo, parent gallery, identity document, and the pets' profile +
    // gallery photos. Booking evidence and event banners are deliberately
    // absent: those belong to records the delete retains.
    IReadOnlyList<string> BlobUrls);

/// <summary>
/// One unfinished booking blocking a pet-parent account delete. Flat and
/// booking-kind-agnostic: <see cref="BookingType"/> ('SingleDay' | 'NightStay')
/// discriminates, and for a stay <see cref="ServiceDate"/> is the check-in date
/// with the times being drop-off / pick-up.
/// </summary>
public sealed record PendingParentJob(
    Guid BookingId,
    string BookingType,
    string JobId,
    Guid ProviderId,
    string? ProviderName,
    string ServiceCategory,
    string SubCategory,
    string Status,
    DateOnly ServiceDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? PetName);
