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
