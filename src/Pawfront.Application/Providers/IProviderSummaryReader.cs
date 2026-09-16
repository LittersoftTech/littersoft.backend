namespace Pawfront.Application.Providers;

/// <summary>
/// A provider's business identity — name, address, city, zip, image — read
/// straight from their Cosmos offering document with no discovery filters
/// applied.
///
/// It exists because <see cref="IProviderDiscoveryService"/> is deliberately
/// registered WRAPPED (active-status and block filters), and both wrappers need
/// SQL-backed services. A host that only wants to know who a provider IS — the
/// invoice renderer in <c>Pawfront.Functions</c> — should not have to take a
/// dependency on the whole SQL infrastructure to find out.
///
/// The absence of those filters is the point, not an oversight: an invoice for a
/// job that already happened must still name its provider, whether or not that
/// provider has since switched themselves off, deleted their account, or been
/// blocked by the parent. Do NOT use this on a discovery or search surface, where
/// the filters are exactly what you want.
///
/// Same narrow-reader shape as <see cref="IProviderNameReader"/> and
/// <see cref="IProviderContactReader"/>.
/// </summary>
public interface IProviderSummaryReader
{
    /// <summary>
    /// Null when the provider has no offering document in that category — which
    /// is legitimate for a provider still mid-onboarding, and for one whose
    /// account delete removed their listing. Callers treat it as best-effort.
    /// </summary>
    Task<ProviderSummary?> GetAsync(
        Guid providerId,
        string serviceCategory,
        CancellationToken cancellationToken);
}
