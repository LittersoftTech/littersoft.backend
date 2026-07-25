namespace Pawfront.Application.Providers;

/// <summary>
/// Narrow SQL reader for a provider's personal name (FirstName + LastName from
/// <c>Provider.Providers</c>). Used to fill the parent-facing search cards and
/// the booking-list provider block for FREELANCE providers, whose Cosmos
/// offering doc carries no business name — so the display name falls back to the
/// person's name. Providers absent from the underlying table are simply omitted
/// from the returned dictionary.
/// </summary>
public interface IProviderNameReader
{
    /// <summary>
    /// Per-provider display name (<c>"FirstName LastName"</c>) for the requested
    /// ids. Providers with no profile row are absent from the dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetProviderDisplayNamesAsync(
        IReadOnlyCollection<Guid> providerIds,
        CancellationToken cancellationToken);
}
