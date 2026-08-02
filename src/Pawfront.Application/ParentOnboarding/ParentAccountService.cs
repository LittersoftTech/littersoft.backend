using Microsoft.Extensions.Logging;
using Pawfront.Application.Storage;
using Pawfront.Contracts.ParentOnboarding;

namespace Pawfront.Application.ParentOnboarding;

/// <summary>
/// Orchestrates a pet-parent account delete across SQL and Blob Storage. The
/// delete is an ANONYMISE + DISABLE: the parent row, their pets, and every record
/// referencing the PetParentId (bookings, events, the payment ledger) are
/// retained, the personal fields are scrubbed, and the account is permanently
/// disabled. See <c>Parent.DeletePetParent</c> for exactly what is kept versus
/// cleared.
///
/// SQL is the authority and goes first (one transaction); it hands back the blob
/// URLs it captured — the profile photo, the parent and pet galleries, and the
/// identity document — which are deleted here. There is no Cosmos leg: a pet
/// parent owns no Cosmos document.
///
/// Blob cleanup is deliberately best-effort. The SQL scrub has already committed,
/// so a failure there must not fail the request and leave the caller thinking the
/// account survived; leftovers are logged as warnings for a later sweep. The rows
/// pointing at them are gone either way, so an orphaned blob is unreachable
/// through the API.
/// </summary>
internal sealed class ParentAccountService(
    IParentOnboardingService onboardingService,
    IPawfrontBlobStorage blobStorage,
    ILogger<ParentAccountService> logger) : IParentAccountService
{
    public async Task<DeletePetParentAccountResponse> DeleteAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        var result = await onboardingService.DeletePetParentAccountAsync(petParentId, cancellationToken);

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
                    "Anonymised pet parent {PetParentId} in SQL but could not delete its blob {BlobUrl}.",
                    petParentId,
                    blobUrl);
            }
        }

        return result.Summary;
    }
}
