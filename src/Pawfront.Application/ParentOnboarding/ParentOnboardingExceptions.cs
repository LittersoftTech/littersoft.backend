namespace Pawfront.Application.ParentOnboarding;

public sealed class UnsupportedParentAuthProviderException(string authProvider)
    : Exception($"Auth provider '{authProvider}' is not supported.");

/// <summary>
/// Thrown when no <c>Parent.ParentAuthIdentities</c> row matches the lookup
/// key — either a Firebase user id (sub/user_id) or a raw auth-identity GUID
/// depending on which lookup path raised it.
/// </summary>
public sealed class ParentAuthIdentityNotFoundException(string identifier)
    : Exception($"Parent auth identity '{identifier}' was not found.");

public sealed class PetParentMobileNumberAlreadyExistsException(string mobileCountryCode, string mobileNumber)
    : Exception($"Mobile number '{mobileCountryCode}{mobileNumber}' is already registered.");

public sealed class UnsupportedPetParentGenderException(string gender)
    : Exception($"Gender '{gender}' is not supported.");

public sealed class PetParentNotFoundException(Guid petParentId)
    : Exception($"Pet parent '{petParentId}' was not found.");

public sealed class ParentMobileOtpNotFoundException(Guid parentMobileOtpId)
    : Exception($"Pet parent mobile OTP entry '{parentMobileOtpId}' was not found.");

public sealed class UnsupportedPetParentIdentityTypeException(string identityType)
    : Exception($"Identity type '{identityType}' is not supported.");

public sealed class PetParentIdentityNotFoundException(Guid petParentId)
    : Exception($"No identity document is on file for pet parent '{petParentId}'.");

/// <summary>
/// The account delete was refused: the parent still has unfinished jobs. Carries
/// the list so the endpoint can hand it straight back in the 409 body — the
/// parent has to cancel or see each one through before the account can go.
///
/// It is an exception rather than a discriminated outcome (which is how the
/// provider-side deactivation models the same "existing bookings block you"
/// situation) because the refusal is decided deep inside the SQL delete, and
/// every layer between there and the endpoint would otherwise have to thread a
/// success-shaped result that isn't one.
/// </summary>
public sealed class PetParentPendingJobsException(
    Guid petParentId,
    IReadOnlyList<PendingParentJob> pendingJobs)
    : Exception(
        $"Pet parent '{petParentId}' still has {pendingJobs.Count} unfinished " +
        "job(s). Cancel or complete them before deleting the account.")
{
    public Guid PetParentId { get; } = petParentId;

    public IReadOnlyList<PendingParentJob> PendingJobs { get; } = pendingJobs;
}

/// <summary>
/// The account has been deleted — anonymised and permanently disabled by
/// <c>Parent.DeletePetParent</c>. Raised by the flows that would undo that
/// (today, the profile edit). The delete is not reversible: signing up again
/// creates a brand-new account.
/// </summary>
public sealed class PetParentAccountDeletedException(Guid petParentId)
    : Exception($"Pet parent account '{petParentId}' has been deleted and can no longer be changed.");
