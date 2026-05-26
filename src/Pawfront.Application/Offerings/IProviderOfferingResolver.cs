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
