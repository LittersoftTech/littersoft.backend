using Pawfront.Api.Auth;
using Pawfront.Application.Billing;
using Pawfront.Application.Bookings;
using Pawfront.Application.ProviderOnboarding;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// Downloads the provider's own invoice for a paid booking — the fee invoice
/// Littersoft GmbH issues them for the Pawfront commission on that one job.
/// </summary>
/// <remarks>
/// The recipient is PINNED to <see cref="InvoiceRecipients.Provider"/> here and to
/// <see cref="InvoiceRecipients.PetParent"/> on the parent host. It is never taken
/// from the client: a paid booking raises two documents that name different
/// parties and carry different money, so letting a caller choose which one to pull
/// would be the whole vulnerability.
///
/// Like earnings, ratings and account-delete — and unlike most of this host, which
/// trusts the route's <c>providerId</c> — these routes resolve the caller's OWN
/// ProviderId from the JWT and reject a mismatch with 403. An invoice is financial
/// data about a specific pair of parties; trusting the route would let anyone who
/// knew a provider id and one of their booking ids pull that provider's invoice.
///
/// The response is the PDF ITSELF, not the standard ApiResponse envelope — the
/// client saves or opens a file. Failures still use the envelope, matching how
/// <c>/blob-images</c> already behaves.
/// </remarks>
internal static class InvoiceEndpoints
{
    public static IEndpointRouteBuilder MapInvoiceEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}");

        group.MapGet("/bookings/{bookingId:guid}/invoice", (
            Guid providerId,
            Guid bookingId,
            HttpContext httpContext,
            IProviderOnboardingService onboardingService,
            IInvoiceService invoiceService,
            CancellationToken cancellationToken)
            => DownloadAsync(
                providerId, BookingTypes.SingleDay, bookingId,
                httpContext, onboardingService, invoiceService, cancellationToken));

        // The two booking kinds live in separate tables and share no id space, so
        // they need separate routes rather than a discriminator on one.
        group.MapGet("/night-stay-bookings/{bookingId:guid}/invoice", (
            Guid providerId,
            Guid bookingId,
            HttpContext httpContext,
            IProviderOnboardingService onboardingService,
            IInvoiceService invoiceService,
            CancellationToken cancellationToken)
            => DownloadAsync(
                providerId, BookingTypes.NightStay, bookingId,
                httpContext, onboardingService, invoiceService, cancellationToken));

        return builder;
    }

    private static async Task<IResult> DownloadAsync(
        Guid providerId,
        string bookingType,
        Guid bookingId,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        IInvoiceService invoiceService,
        CancellationToken cancellationToken)
    {
        var denied = await EnsureCallerOwnsProviderAsync(
            providerId, httpContext, onboardingService, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            var download = await invoiceService.GetDownloadAsync(
                new InvoiceDownloadQuery(
                    bookingType,
                    bookingId,
                    InvoiceRecipients.Provider,
                    ProviderId: providerId,
                    PetParentId: null),
                cancellationToken);

            // fileDownloadName sets Content-Disposition, so the saved file is named
            // after the invoice rather than being one of many "invoice.pdf".
            return Results.File(
                download.Content,
                download.ContentType,
                fileDownloadName: download.FileName);
        }
        catch (InvoiceNotFoundException exception)
        {
            // Unknown booking and "not your invoice" are one answer on purpose —
            // an id must not be probeable.
            return ApiResults.NotFound("InvoiceNotFound", exception.Message);
        }
        catch (InvoiceNotReadyException exception)
        {
            // Raised but still rendering. 409 rather than 404 because this one is
            // worth retrying in a moment and a 404 never is.
            return ApiResults.Conflict("InvoiceNotReady", exception.Message);
        }
        catch (InvoiceFileMissingException exception)
        {
            return ApiResults.NotFound("InvoiceFileNotFound", exception.Message);
        }
    }

    private static async Task<IResult?> EnsureCallerOwnsProviderAsync(
        Guid providerId,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        Guid? callerProviderId;
        try
        {
            var firebaseUserId = FirebaseClaims.GetFirebaseUserId(httpContext.User);
            var caller = await onboardingService.ResolveProviderByFirebaseUidAsync(
                firebaseUserId, cancellationToken);
            callerProviderId = caller.ProviderId;
        }
        catch (ProviderAuthIdentityForFirebaseUserNotFoundException)
        {
            callerProviderId = null;
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }

        return callerProviderId == providerId
            ? null
            : ApiResults.Forbidden("Forbidden", "You can only download your own invoices.");
    }
}
