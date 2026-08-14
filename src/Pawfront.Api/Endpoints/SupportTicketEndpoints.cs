using Pawfront.Api.Auth;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Application.Storage;
using Pawfront.Application.Support;
using Pawfront.Contracts.Support;
using Microsoft.AspNetCore.Mvc;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// The provider's side of support tickets: reporting an incident on a booking, reporting a
/// conversation, and following what happens next.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reporting somebody does not block them.</b> A ticket is a message to support, not a
/// sanction the reporter applies themselves: nothing is written to
/// <c>Chat.BlockedParticipants</c>, so the two can still message, book and find each other
/// while support looks at it. Blocking stays the separate, deliberate action it always was.
/// </para>
/// <para>
/// <b>One open ticket per BOOKING</b> (and per conversation), in either direction — not one
/// per customer. A provider with several bookings from the same parent can report each of
/// them, because each is a different incident; what is refused is a second open report of
/// the same booking, which answers 409 <c>TicketAlreadyOpen</c> carrying the ticket already
/// open so the reporter is pointed at it rather than left at a dead end.
/// </para>
/// <para>
/// Every route resolves the caller's own ProviderId from the JWT and rejects a mismatch
/// with 403, joining earnings, ratings and account-delete as the exceptions to this host's
/// usual "trust the route id" posture. It matters more here than almost anywhere: the
/// procedure's party check compares the actor against the booking's or conversation's
/// ProviderId, so trusting the route would let anyone who knew a provider id file a report
/// <i>as</i> that provider.
/// </para>
/// </remarks>
internal static class SupportTicketEndpoints
{
    /// <summary>Matches the review and evidence galleries; a phone camera photo fits.</summary>
    private const long MaxPhotoBytes = 3L * 1024 * 1024;

    private static readonly HashSet<string> AllowedPhotoContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp"
    };

    public static IEndpointRouteBuilder MapProviderSupportTicketEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/support-tickets");

        // Two dedicated routes rather than one with a discriminator field, following the
        // booking transitions' convention: each takes exactly the fields its kind needs,
        // so neither carries a conditionally-required subject.
        group.MapPost("/report-incident", ReportIncident);
        group.MapPost("/report-chat", ReportChat);
        // Same report, multipart, with the evidence in the same call. A separate route
        // rather than content-negotiating on /report-incident: one route cannot carry two
        // request shapes in OpenAPI, and the app knows at the tap which one it is sending.
        group.MapPost("/report-incident-with-photos", ReportIncidentWithPhotos).DisableAntiforgery();

        group.MapGet("/", ListTickets);
        group.MapGet("/{ticketId:guid}", GetTicket);
        group.MapPost("/{ticketId:guid}/photos", UploadPhoto).DisableAntiforgery();
        group.MapPost("/{ticketId:guid}/clarification", SubmitClarification);

        return builder;
    }

    private static async Task<IResult> ReportIncident(
        Guid providerId,
        ReportBookingIncidentRequest request,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("InvalidRequest", "A report is required.");
        }

        var denied = await EnsureCallerOwnsProviderAsync(
            providerId, httpContext, onboardingService, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        return await CreateAsync(
            new CreateSupportTicketCommand(
                SupportTicketTypes.BookingIncident,
                SupportRaisedByTypes.Provider,
                providerId,
                request.BookingType,
                request.BookingId,
                ConversationId: null,
                request.Comment,
                request.Category,
                request.Reason),
            providerId,
            ticketService,
            cancellationToken);
    }

    private static async Task<IResult> ReportChat(
        Guid providerId,
        ReportChatIncidentRequest request,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("InvalidRequest", "A report is required.");
        }

        var denied = await EnsureCallerOwnsProviderAsync(
            providerId, httpContext, onboardingService, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        return await CreateAsync(
            new CreateSupportTicketCommand(
                SupportTicketTypes.ChatIncident,
                SupportRaisedByTypes.Provider,
                providerId,
                BookingType: null,
                BookingId: null,
                request.ConversationId,
                request.Comment,
                request.Category,
                request.Reason),
            providerId,
            ticketService,
            cancellationToken);
    }

    /// <summary>
    /// "Report Incident" with its evidence in one call. Fields arrive as form text, the
    /// files as repeated <c>photos</c> parts.
    /// </summary>
    /// <remarks>
    /// The two-call form still exists and is unchanged — the per-photo endpoint is also
    /// what a client retries against when one file here fails.
    /// </remarks>
    private static async Task<IResult> ReportIncidentWithPhotos(
        Guid providerId,
        [FromForm] string bookingType,
        [FromForm] Guid bookingId,
        [FromForm] string comment,
        [FromForm] string? category,
        [FromForm] string? reason,
        IFormFileCollection photos,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        var denied = await EnsureCallerOwnsProviderAsync(
            providerId, httpContext, onboardingService, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        // Typed form binding rather than reading HttpRequest.Form by hand, so the
        // multipart shape lands in the OpenAPI document — otherwise the generated
        // Postman request would be a body-less POST nobody could fill in.
        var command = new CreateSupportTicketCommand(
            SupportTicketTypes.BookingIncident,
            SupportRaisedByTypes.Provider,
            providerId,
            bookingType,
            bookingId,
            ConversationId: null,
            comment,
            category,
            reason);

        return await CreateWithPhotosAsync(
            command, photos, providerId, ticketService, cancellationToken);
    }

    /// <summary>
    /// Opens each uploaded file, runs the combined flow, and disposes the streams
    /// whatever happens.
    /// </summary>
    private static async Task<IResult> CreateWithPhotosAsync(
        CreateSupportTicketCommand command,
        IFormFileCollection files,
        Guid providerId,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        var uploads = new List<SupportTicketPhotoUpload>(files.Count);
        var streams = new List<Stream>(files.Count);
        try
        {
            foreach (var file in files)
            {
                var stream = file.OpenReadStream();
                streams.Add(stream);
                uploads.Add(new SupportTicketPhotoUpload(
                    file.FileName, file.ContentType ?? string.Empty, file.Length, stream));
            }

            return await CreateWithPhotosAsync(
                command, uploads, providerId, ticketService, cancellationToken);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private static async Task<IResult> CreateWithPhotosAsync(
        CreateSupportTicketCommand command,
        IReadOnlyList<SupportTicketPhotoUpload> uploads,
        Guid providerId,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ticketService.CreateWithPhotosAsync(command, uploads, cancellationToken);
            var ticket = SupportTicketMapping.ToResponse(result.Ticket, SupportRaisedByTypes.Provider);

            if (result.Outcome == CreateSupportTicketOutcome.TicketAlreadyOpen)
            {
                // Nothing was created and nothing was uploaded.
                return ApiResults.Conflict(
                    "TicketAlreadyOpen", AlreadyOpenMessage(command, ticket.TicketRef), ticket);
            }

            return ApiResults.Created(
                $"/api/v1/providers/{providerId}/support-tickets/{result.Ticket.TicketId}",
                new CreateSupportTicketWithPhotosResponse(
                    ticket,
                    ticket.Photos.Count,
                    [.. result.PhotoFailures.Select(f =>
                        new SupportTicketPhotoErrorResponse(f.FileName, f.Code, f.Message))]));
        }
        catch (SupportBookingNotFoundException exception)
        {
            return ApiResults.NotFound(
                command.BookingType == Application.Bookings.BookingTypes.NightStay
                    ? "NightStayBookingNotFound"
                    : "BookingNotFound",
                exception.Message);
        }
        catch (SupportForbiddenException exception)
        {
            return ApiResults.Forbidden("Forbidden", exception.Message);
        }
        catch (SupportNotAppBookingException exception)
        {
            return ApiResults.BadRequest("ReportNotAppBooking", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    /// <summary>
    /// The 409 names the SUBJECT rather than the counterparty. The rule is one open ticket
    /// per booking (or per conversation), so "already open with this customer" would tell
    /// the reporter the wrong thing about why they were refused — and invite them to
    /// conclude they cannot report that customer again at all.
    /// </summary>
    private static string AlreadyOpenMessage(CreateSupportTicketCommand command, string ticketRef) =>
        command.TicketType == SupportTicketTypes.ChatIncident
            ? $"Support ticket {ticketRef} is already open on this conversation. "
              + "It must be closed before another can be raised on it."
            : $"Support ticket {ticketRef} is already open on this booking. "
              + "It must be closed before another can be raised on it.";

    /// <summary>Shared create for both kinds — only the command differs.</summary>
    private static async Task<IResult> CreateAsync(
        CreateSupportTicketCommand command,
        Guid providerId,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ticketService.CreateAsync(command, cancellationToken);
            var response = SupportTicketMapping.ToResponse(result.Ticket, SupportRaisedByTypes.Provider);

            if (result.Outcome == CreateSupportTicketOutcome.TicketAlreadyOpen)
            {
                // The conflict carries the open ticket, so the app can take the reporter
                // straight to it instead of reporting a dead end.
                return ApiResults.Conflict(
                    "TicketAlreadyOpen", AlreadyOpenMessage(command, response.TicketRef), response);
            }

            return ApiResults.Created(
                $"/api/v1/providers/{providerId}/support-tickets/{result.Ticket.TicketId}", response);
        }
        catch (SupportBookingNotFoundException exception)
        {
            return ApiResults.NotFound(
                command.BookingType == Application.Bookings.BookingTypes.NightStay
                    ? "NightStayBookingNotFound"
                    : "BookingNotFound",
                exception.Message);
        }
        catch (SupportConversationNotFoundException exception)
        {
            return ApiResults.NotFound("ConversationNotFound", exception.Message);
        }
        catch (SupportForbiddenException exception)
        {
            return ApiResults.Forbidden("Forbidden", exception.Message);
        }
        catch (SupportNotAppBookingException exception)
        {
            return ApiResults.BadRequest("ReportNotAppBooking", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> ListTickets(
        Guid providerId,
        string? status,
        string? sortBy,
        string? sortDirection,
        int? skip,
        int? take,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        var denied = await EnsureCallerOwnsProviderAsync(
            providerId, httpContext, onboardingService, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        SupportTicketQuery query;
        try
        {
            query = new SupportTicketQuery(
                SupportRaisedByTypes.Provider,
                providerId,
                SupportTicketQueryParsing.ParseStatuses(status),
                SupportTicketQueryParsing.ParseSortBy(sortBy),
                Application.Earnings.EarningsQueryParsing.ParseSortDirection(sortDirection),
                skip ?? 0,
                // 0 lets the service apply its own page cap rather than duplicating it here.
                take ?? 0);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }

        var page = await ticketService.ListAsync(query, cancellationToken);
        return ApiResults.Ok(SupportTicketMapping.ToListResponse(page, SupportRaisedByTypes.Provider));
    }

    private static async Task<IResult> GetTicket(
        Guid providerId,
        Guid ticketId,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        var denied = await EnsureCallerOwnsProviderAsync(
            providerId, httpContext, onboardingService, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        var detail = await ticketService.GetAsync(
            ticketId, SupportRaisedByTypes.Provider, providerId, cancellationToken);

        // Unknown ticket and "not yours" are one answer, so an id cannot be probed.
        return detail is null
            ? ApiResults.NotFound("TicketNotFound", $"Ticket '{ticketId}' was not found.")
            : ApiResults.Ok(SupportTicketMapping.ToDetailResponse(
                detail, SupportRaisedByTypes.Provider, providerId));
    }

    private static async Task<IResult> UploadPhoto(
        Guid providerId,
        Guid ticketId,
        IFormFile? file,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        ISupportTicketService ticketService,
        IPawfrontBlobStorage blobStorage,
        CancellationToken cancellationToken)
    {
        var denied = await EnsureCallerOwnsProviderAsync(
            providerId, httpContext, onboardingService, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        var validation = ValidatePhotoFile(file);
        if (validation is not null)
        {
            return validation;
        }

        // Resolve the ticket FIRST: the blob path is keyed by its id, and uploading before
        // knowing it exists would orphan a file on every stray call. This also gets the
        // cap and the wrong-kind cases answered without paying for an upload.
        var existing = await ticketService.GetAsync(
            ticketId, SupportRaisedByTypes.Provider, providerId, cancellationToken);

        if (existing is null || !existing.Ticket.WasRaisedBy(SupportRaisedByTypes.Provider))
        {
            // Evidence belongs to the ticket's CREATOR, so the counterparty gets the same
            // "not found" a stranger does — the procedure scopes it the same way.
            return ApiResults.NotFound("TicketNotFound", $"Ticket '{ticketId}' was not found.");
        }

        if (existing.Ticket.TicketType != SupportTicketTypes.BookingIncident)
        {
            return ApiResults.BadRequest(
                "TicketPhotoNotBookingIncident",
                "Only a reported booking incident can carry photos.");
        }

        if (!existing.Ticket.IsOpen)
        {
            return ApiResults.Conflict("TicketClosed", $"Ticket '{ticketId}' is closed.");
        }

        if (existing.Ticket.Photos.Count >= SupportTicketLimits.MaxPhotos)
        {
            return ApiResults.Conflict(
                "TicketPhotoLimitReached",
                $"A ticket can carry at most {SupportTicketLimits.MaxPhotos} photos.");
        }

        await using var stream = file!.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            BlobUploadKind.IncidentPhoto,
            ticketId,
            file.FileName,
            stream,
            file.ContentType,
            cancellationToken);

        try
        {
            var ticket = await ticketService.AddPhotoAsync(
                ticketId, SupportRaisedByTypes.Provider, providerId, url, cancellationToken);

            return ApiResults.Ok(SupportTicketMapping.ToResponse(ticket, SupportRaisedByTypes.Provider));
        }
        catch (SupportTicketNotFoundException exception)
        {
            return ApiResults.NotFound("TicketNotFound", exception.Message);
        }
        catch (SupportTicketPhotoLimitReachedException exception)
        {
            return ApiResults.Conflict("TicketPhotoLimitReached", exception.Message);
        }
        catch (SupportTicketPhotoNotBookingIncidentException exception)
        {
            return ApiResults.BadRequest("TicketPhotoNotBookingIncident", exception.Message);
        }
        catch (SupportTicketClosedException exception)
        {
            return ApiResults.Conflict("TicketClosed", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> SubmitClarification(
        Guid providerId,
        Guid ticketId,
        SubmitTicketClarificationRequest request,
        HttpContext httpContext,
        IProviderOnboardingService onboardingService,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("InvalidRequest", "A reply is required.");
        }

        var denied = await EnsureCallerOwnsProviderAsync(
            providerId, httpContext, onboardingService, cancellationToken);
        if (denied is not null)
        {
            return denied;
        }

        try
        {
            var detail = await ticketService.RecordClarificationAsync(
                ticketId, SupportRaisedByTypes.Provider, providerId, request.Reply, cancellationToken);

            return ApiResults.Ok(SupportTicketMapping.ToDetailResponse(
                detail, SupportRaisedByTypes.Provider, providerId));
        }
        catch (SupportTicketNotFoundException exception)
        {
            return ApiResults.NotFound("TicketNotFound", exception.Message);
        }
        catch (SupportTicketClosedException exception)
        {
            return ApiResults.Conflict("TicketClosed", exception.Message);
        }
        catch (SupportNoClarificationRequestedException exception)
        {
            return ApiResults.Conflict("NoClarificationRequested", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    /// <summary>
    /// Same shape as the earnings, rating and account-delete checks: resolve the caller's
    /// own ProviderId from the JWT and compare it to the route.
    /// </summary>
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
            : ApiResults.Forbidden("Forbidden", "You can only manage your own support tickets.");
    }

    private static IResult? ValidatePhotoFile(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            return ApiResults.BadRequest("InvalidFile", "An image file is required.");
        }

        if (file.Length > MaxPhotoBytes)
        {
            return ApiResults.BadRequest(
                "ImageTooLarge", $"Photo must be {MaxPhotoBytes / (1024 * 1024)} MB or smaller.");
        }

        if (string.IsNullOrWhiteSpace(file.ContentType)
            || !AllowedPhotoContentTypes.Contains(file.ContentType))
        {
            return ApiResults.BadRequest(
                "UnsupportedImageFormat", "Photo must be a JPEG, PNG, or WebP image.");
        }

        return null;
    }
}
