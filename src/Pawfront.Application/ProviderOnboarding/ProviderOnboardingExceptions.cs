namespace Pawfront.Application.ProviderOnboarding;

public sealed class ProviderAuthIdentityNotFoundException(Guid providerAuthIdentityId)
    : Exception($"Provider auth identity '{providerAuthIdentityId}' was not found.");

public sealed class MobileNumberAlreadyExistsException(string mobileCountryCode, string mobileNumber)
    : Exception($"Mobile number '{mobileCountryCode}{mobileNumber}' is already registered.");

public sealed class UnsupportedAuthProviderException(string authProvider)
    : Exception($"Auth provider '{authProvider}' is not supported.");

public sealed class UnsupportedGenderException(string gender)
    : Exception($"Gender '{gender}' is not supported.");

public sealed class ProviderProfileNotFoundException(Guid providerId)
    : Exception($"Provider profile '{providerId}' was not found.");

public sealed class ProviderMobileOtpNotFoundException(Guid providerMobileOtpId)
    : Exception($"Provider mobile OTP entry '{providerMobileOtpId}' was not found.");

public sealed class ProviderAuthIdentityForFirebaseUserNotFoundException(string firebaseUserId)
    : Exception($"No provider auth identity is registered for Firebase user '{firebaseUserId}'.");

/// <summary>
/// The provider account has been deleted (anonymised + permanently disabled by
/// <c>Provider.DeleteProvider</c>). Editing the profile would undo the
/// anonymisation and reactivating would make the account bookable again, so both
/// are refused. SQL THROW 51115.
/// </summary>
public sealed class ProviderAccountDeletedException(Guid providerId)
    : Exception($"Provider account '{providerId}' has been deleted.");

/// <summary>
/// The account delete was refused: an open support ticket names this provider. Part of
/// the legal hold — an open ticket is a live dispute, and anonymising one of its two
/// parties while support is still looking at it would erase what the ticket is about.
/// </summary>
/// <remarks>
/// The provider-side twin of <c>PetParentOpenTicketsException</c>. Note this is the only
/// thing that refuses a provider delete: unlike the parent's, it is not additionally
/// blocked by unfinished jobs. The provider cannot clear it themselves — only support
/// closing the ticket lifts it.
/// </remarks>
public sealed class ProviderOpenTicketsException(
    Guid providerId,
    IReadOnlyList<Support.BlockingSupportTicket> openTickets)
    : Exception(
        $"Provider '{providerId}' has {openTickets.Count} open support ticket(s). " +
        "The account cannot be deleted until support closes them.")
{
    public Guid ProviderId { get; } = providerId;

    public IReadOnlyList<Support.BlockingSupportTicket> OpenTickets { get; } = openTickets;
}
