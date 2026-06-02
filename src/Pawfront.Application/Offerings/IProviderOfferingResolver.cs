namespace Pawfront.Application.Offerings;

/// <summary>
/// Resolves a ServiceId into its capacity + duration rule by combining the
/// <c>Provider.ProviderServices</c> row with the matching Cosmos offering sub-branch
/// (DayCare/NightStay/Session/Appointment). Used by both the slot service and the
/// booking service so they share the same definition of "what is this service".
/// </summary>
public interface IProviderOfferingResolver
{
    Task<OfferingResolution> ResolveAsync(Guid serviceId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a specific grooming menu item from the provider's offering.
    /// Used by the booking + slot services for PetGroomer, where duration is
    /// per-item rather than per-service. Returns NotFound when the provider has
    /// no offering, NotOffered when the code isn't on the provider's menu, or
    /// Inactive when the menu item is disabled.
    /// </summary>
    Task<GroomingItemResolution> ResolveGroomingItemAsync(
        Guid providerId,
        string code,
        CancellationToken cancellationToken);
}

public abstract record GroomingItemResolution
{
    public sealed record OfferingMissing : GroomingItemResolution;

    public sealed record NotOffered(string Code) : GroomingItemResolution;

    public sealed record Inactive(string Code) : GroomingItemResolution;

    public sealed record Resolved(
        string Code,
        int DurationMinutes,
        decimal Price) : GroomingItemResolution;
}

public abstract record OfferingResolution
{
    public sealed record NotFound : OfferingResolution;

    public sealed record Inactive(Guid ProviderId, string ServiceCategory, string ServiceType) : OfferingResolution;

    public sealed record NotConfigured(
        Guid ProviderId,
        string ServiceCategory,
        string SubCategory,
        string ServiceType) : OfferingResolution;

    public sealed record Resolved(
        Guid ServiceId,
        Guid ProviderId,
        string ServiceCategory,
        string SubCategory,
        string ServiceType,
        int Capacity,
        decimal DurationHours,
        bool IsDurationFixed) : OfferingResolution;
}
