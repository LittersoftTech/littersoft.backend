namespace Pawfront.Application.Offerings;

public interface IProviderOfferingResolver
{
    /// <summary>
    /// Resolves a provider's registered service category, sub-category, and offering capacity +
    /// duration rule by combining the SQL service-registration row with the matching Cosmos
    /// offering document. Used by the slot service and the booking service so they share the
    /// same definition of "what does this provider offer".
    /// </summary>
    Task<OfferingResolution> ResolveAsync(Guid providerId, CancellationToken cancellationToken);
}

public abstract record OfferingResolution
{
    public sealed record NotRegistered : OfferingResolution;

    public sealed record NotConfigured(string ServiceCategory, string SubCategory) : OfferingResolution;

    public sealed record Resolved(
        string ServiceCategory,
        string SubCategory,
        int Capacity,
        decimal DurationHours,
        bool IsDurationFixed) : OfferingResolution;
}
