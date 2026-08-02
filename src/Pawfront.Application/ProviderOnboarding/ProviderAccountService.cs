using Microsoft.Extensions.Logging;
using Pawfront.Application.ProviderServices;
using Pawfront.Application.Storage;
using Pawfront.Contracts.ProviderOnboarding;

namespace Pawfront.Application.ProviderOnboarding;

/// <summary>
/// Orchestrates a provider account delete across all three stores. The delete is
/// an ANONYMISE + DISABLE: the provider row and every record referencing the
/// ProviderId (bookings, events, the payment ledger) are retained, the personal
/// fields are scrubbed, and the account is permanently disabled. See
/// <c>Provider.DeleteProvider</c> for exactly what is kept versus cleared.
///
/// SQL is the authority and goes first (one transaction); it hands back the
/// Cosmos keys and blob URLs it captured, which are cleaned up here — the
/// provider's Cosmos offering document (their public service listing) and their
/// profile/gallery/banner blobs. The Cosmos documents of the events they
/// organised are NOT removed: the events themselves are retained.
///
/// Cosmos and Blob cleanup is deliberately best-effort. The SQL scrub has already
/// committed, so a failure there must not fail the request and leave the caller
/// thinking the account survived; leftovers are logged as warnings for a later
/// sweep. The one user-visible consequence of a failed Cosmos delete is that the
/// stale listing lingers in discovery — bookings are still blocked, because that
/// gate is the SQL <c>IsActive</c> flag.
/// </summary>
internal sealed class ProviderAccountService(
    IProviderOnboardingService onboardingService,
    IProviderServiceCosmosStore providerServiceCosmosStore,
    IPawfrontBlobStorage blobStorage,
    ILogger<ProviderAccountService> logger) : IProviderAccountService
{
    public async Task<DeleteProviderAccountResponse> DeleteAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        var result = await onboardingService.DeleteProviderAccountAsync(providerId, cancellationToken);

        foreach (var serviceCategory in result.ServiceCategories)
        {
            try
            {
                await providerServiceCosmosStore.DeleteAsync(providerId, serviceCategory, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Anonymised provider {ProviderId} in SQL but could not delete its {ServiceCategory} Cosmos service listing; it may still appear in discovery.",
                    providerId,
                    serviceCategory);
            }
        }

        foreach (var blobUrl in result.BlobUrls)
        {
            try
            {
                await blobStorage.DeleteAsync(blobUrl, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Anonymised provider {ProviderId} in SQL but could not delete its blob {BlobUrl}.",
                    providerId,
                    blobUrl);
            }
        }

        return result.Summary;
    }
}
