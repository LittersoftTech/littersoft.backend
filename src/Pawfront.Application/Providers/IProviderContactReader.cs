namespace Pawfront.Application.Providers;

/// <summary>
/// How to reach a provider, read from their account rather than their offering:
/// the sign-in e-mail on <c>Provider.ProviderAuthIdentities</c> plus the verified
/// mobile on <c>Provider.Providers</c>.
///
/// This is the contact for FREELANCE sub-categories, whose registration captures
/// no separate business e-mail or telephone — the person is the business — and the
/// fallback for a business that left the (now optional) telephone blank.
/// </summary>
public sealed record ProviderContactDetails(
    string? Email,
    string? MobileCountryCode,
    string? MobileNumber);

/// <summary>
/// Narrow SQL reader for <see cref="ProviderContactDetails"/>. Returns null when
/// the provider has no profile row (a registration without a completed profile).
/// </summary>
public interface IProviderContactReader
{
    Task<ProviderContactDetails?> GetAsync(Guid providerId, CancellationToken cancellationToken);
}
