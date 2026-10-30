namespace Pawfront.Application.Providers;

public static class ProviderCities
{
    public static async Task<IReadOnlyList<string>> ListAsync(
        IProviderDiscoveryService discovery, CancellationToken cancellationToken)
    {
        var providers = await discovery.ListAsync(
            new ProviderDiscoveryFilter(null, null, null, null, 0, int.MaxValue),
            cancellationToken);
        return providers.Select(p => p.City)
            .Where(city => !string.IsNullOrWhiteSpace(city))
            .Select(city => city.Trim())
            .OrderBy(city => city, StringComparer.Ordinal)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(city => city, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
