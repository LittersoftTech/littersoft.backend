using Pawfront.Contracts.ProviderOnboarding;

namespace Pawfront.Application.ProviderOnboarding;

/// <summary>
/// The outcome of the SQL half of a provider account delete (an anonymise +
/// disable, not a row delete). SQL cannot reach Cosmos or Blob Storage, so the
/// sproc also hands back what the provider still owns in those stores; the
/// <see cref="IProviderAccountService"/> orchestrator finishes the cleanup.
///
/// Both collections are empty when the account was already deleted — the sproc is
/// idempotent and there is nothing left to clean up.
/// </summary>
public sealed record ProviderAccountDeletionResult(
    DeleteProviderAccountResponse Summary,
    // Partition keys of the provider's Cosmos ProviderServices documents (the doc
    // id is the ProviderId). That document is the provider's public service
    // LISTING, so it must not outlive the account. One per registered category —
    // today at most one, since a provider may offer only one category.
    IReadOnlyList<string> ServiceCategories,
    // Provider banner, gallery photos and per-service banners. Event banners and
    // booking evidence are deliberately absent: those belong to records the
    // delete retains. Deleted best-effort — the SQL rows are already gone.
    IReadOnlyList<string> BlobUrls);
