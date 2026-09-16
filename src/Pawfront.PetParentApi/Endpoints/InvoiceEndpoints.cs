using Pawfront.Application.Billing;
using Pawfront.Application.Bookings;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Downloads the pet parent's own invoice for a paid booking — the one their
/// PROVIDER issues them for the service. Pawfront only delivers it on the
/// provider's behalf, which is what the copy on the document says.
/// </summary>
/// <remarks>
/// The recipient is PINNED to <see cref="InvoiceRecipients.PetParent"/> and never
/// taken from the client: a paid booking raises two documents naming different
/// parties and carrying different money, so a caller choosing between them would
/// be the whole vulnerability. This host therefore never serves the provider's fee
/// invoice, and the provider host never serves this one.
///
/// No JWT check is needed here, unlike the provider host's twin: these routes sit
/// in the <c>RequireOwnedPetParent()</c> group, which already resolves the
/// caller's PetParentId from the JWT and rejects any other id with 403.
///
/// The response is the PDF ITSELF rather than the ApiResponse envelope; failures
/// still use the envelope, matching <c>/blob-images</c>.
/// </remarks>
internal static class InvoiceEndpoints
{
    public static IEndpointRouteBuilder MapInvoiceEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/pet-parents/{petParentId:guid}").RequireOwnedPetParent();

        group.MapGet("/bookings/{bookingId:guid}/invoice", (
            Guid petParentId,
            Guid bookingId,
            IInvoiceService invoiceService,
            CancellationToken cancellationToken)
            => DownloadAsync(petParentId, BookingTypes.SingleDay, bookingId, invoiceService, cancellationToken));

        // Separate route rather than a discriminator: the two booking kinds live in
        // separate tables and share no id space.
        group.MapGet("/night-stay-bookings/{bookingId:guid}/invoice", (
            Guid petParentId,
            Guid bookingId,
            IInvoiceService invoiceService,
            CancellationToken cancellationToken)
            => DownloadAsync(petParentId, BookingTypes.NightStay, bookingId, invoiceService, cancellationToken));

        return builder;
    }

    private static async Task<IResult> DownloadAsync(
        Guid petParentId,
        string bookingType,
        Guid bookingId,
        IInvoiceService invoiceService,
        CancellationToken cancellationToken)
    {
        try
        {
            var download = await invoiceService.GetDownloadAsync(
                new InvoiceDownloadQuery(
                    bookingType,
                    bookingId,
                    InvoiceRecipients.PetParent,
                    ProviderId: null,
                    PetParentId: petParentId),
                cancellationToken);

            return Results.File(
                download.Content,
                download.ContentType,
                fileDownloadName: download.FileName);
        }
        catch (InvoiceNotFoundException exception)
        {
            return ApiResults.NotFound("InvoiceNotFound", exception.Message);
        }
        catch (InvoiceNotReadyException exception)
        {
            return ApiResults.Conflict("InvoiceNotReady", exception.Message);
        }
        catch (InvoiceFileMissingException exception)
        {
            return ApiResults.NotFound("InvoiceFileNotFound", exception.Message);
        }
    }
}
