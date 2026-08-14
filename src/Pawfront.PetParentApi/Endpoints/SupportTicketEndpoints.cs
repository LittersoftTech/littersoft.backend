using Pawfront.Application.Storage;
using Pawfront.Application.Support;
using Pawfront.Contracts.Support;
using Pawfront.PetParentApi.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// The pet parent's side of support tickets: reporting an incident on a booking, reporting
/// a conversation, and following what happens next.
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
/// per provider. A parent with five bookings from the same provider can report each of them,
/// because each is a different incident; what is refused is a second open report of the same
/// booking, which answers 409 <c>TicketAlreadyOpen</c> carrying the ticket already open so
/// the reporter is pointed at it rather than left at a dead end.
/// </para>
/// <para>
/// Every route sits on the ownership-filtered <c>/pet-parents/{petParentId:guid}</c> group,
/// so the reporter is resolved from the JWT and a caller can only ever raise or read their
/// own tickets. Whether they are party to the booking or conversation they name is settled
/// inside <c>Support.CreateTicket</c>, which is also what derives the counterparty — so a
/// report cannot be aimed at somebody who was never part of it.
/// </para>
/// <para>
/// <b>An open ticket blocks the account and pet deletes</b> (409 <c>PendingTicketsExist</c>)
/// and holds the reported conversation, so neither party can clear the thread or retract a
/// message while support is looking at it.
/// </para>
/// </remarks>
internal static class SupportTicketEndpoints
{
    /// <summary>Matches the review and evidence galleries; a phone camera photo fits.</summary>
    private const long MaxPhotoBytes = 3L * 1024 * 1024;

    private static readonly HashSet<string> AllowedPhotoContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/webp"
    };

    public static IEndpointRouteBuilder MapParentSupportTicketEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder
            .MapGroup("/pet-parents/{petParentId:guid}/support-tickets")
            .RequireOwnedPetParent();

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

    private static Task<IResult> ReportIncident(
        Guid petParentId,
        ReportBookingIncidentRequest request,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Task.FromResult(ApiResults.BadRequest("InvalidRequest", "A report is required."));
        }

        return CreateAsync(
            new CreateSupportTicketCommand(
                SupportTicketTypes.BookingIncident,
                SupportRaisedByTypes.PetParent,
                petParentId,
                request.BookingType,
                request.BookingId,
                ConversationId: null,
                request.Comment,
                request.Category,
                request.Reason),
            petParentId,
            ticketService,
            cancellationToken);
    }

    private static Task<IResult> ReportChat(
        Guid petParentId,
        ReportChatIncidentRequest request,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Task.FromResult(ApiResults.BadRequest("InvalidRequest", "A report is required."));
        }

        return CreateAsync(
            new CreateSupportTicketCommand(
                SupportTicketTypes.ChatIncident,
                SupportRaisedByTypes.PetParent,
                petParentId,
                BookingType: null,
                BookingId: null,
                request.ConversationId,
                request.Comment,
                request.Category,
                request.Reason),
            petParentId,
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
        Guid petParentId,
        [FromForm] string bookingType,
        [FromForm] Guid bookingId,
        [FromForm] string comment,
        [FromForm] string? category,
        [FromForm] string? reason,
        IFormFileCollection photos,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        // Typed form binding rather than reading HttpRequest.Form by hand, so the
        // multipart shape lands in the OpenAPI document — otherwise the generated
        // Postman request would be a body-less POST nobody could fill in.
        var command = new CreateSupportTicketCommand(
            SupportTicketTypes.BookingIncident,
            SupportRaisedByTypes.PetParent,
            petParentId,
            bookingType,
            bookingId,
            ConversationId: null,
            comment,
            category,
            reason);

        return await CreateWithPhotosAsync(
            command, photos, petParentId, ticketService, cancellationToken);
    }

    /// <summary>
    /// Opens each uploaded file, runs the combined flow, and disposes the streams
    /// whatever happens. Shared by both hosts' handlers in shape, not in code — the two
    /// differ only in the actor they pin.
    /// </summary>
    private static async Task<IResult> CreateWithPhotosAsync(
        CreateSupportTicketCommand command,
        IFormFileCollection files,
        Guid petParentId,
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
                command, uploads, petParentId, ticketService, cancellationToken);
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
        Guid petParentId,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ticketService.CreateWithPhotosAsync(command, uploads, cancellationToken);
            var ticket = SupportTicketMapping.ToResponse(result.Ticket, SupportRaisedByTypes.PetParent);

            if (result.Outcome == CreateSupportTicketOutcome.TicketAlreadyOpen)
            {
                // Nothing was created and nothing was uploaded.
                return ApiResults.Conflict(
                    "TicketAlreadyOpen", AlreadyOpenMessage(command, ticket.TicketRef), ticket);
            }

            return ApiResults.Created(
                $"/api/v1/pet-parents/{petParentId}/support-tickets/{result.Ticket.TicketId}",
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
    /// per booking (or per conversation), so "already open with this provider" would tell
    /// the reporter the wrong thing about why they were refused — and invite them to
    /// conclude they cannot report that provider again at all.
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
        Guid petParentId,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ticketService.CreateAsync(command, cancellationToken);
            var response = SupportTicketMapping.ToResponse(result.Ticket, SupportRaisedByTypes.PetParent);

            if (result.Outcome == CreateSupportTicketOutcome.TicketAlreadyOpen)
            {
                // The conflict carries the open ticket, so the app can take the reporter
                // straight to it instead of reporting a dead end.
                return ApiResults.Conflict(
                    "TicketAlreadyOpen", AlreadyOpenMessage(command, response.TicketRef), response);
            }

            return ApiResults.Created(
                $"/api/v1/pet-parents/{petParentId}/support-tickets/{result.Ticket.TicketId}", response);
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
        Guid petParentId,
        string? status,
        string? sortBy,
        string? sortDirection,
        int? skip,
        int? take,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        SupportTicketQuery query;
        try
        {
            query = new SupportTicketQuery(
                SupportRaisedByTypes.PetParent,
                petParentId,
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
        return ApiResults.Ok(SupportTicketMapping.ToListResponse(page, SupportRaisedByTypes.PetParent));
    }

    private static async Task<IResult> GetTicket(
        Guid petParentId,
        Guid ticketId,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        var detail = await ticketService.GetAsync(
            ticketId, SupportRaisedByTypes.PetParent, petParentId, cancellationToken);

        // Unknown ticket and "not yours" are one answer, so an id cannot be probed.
        return detail is null
            ? ApiResults.NotFound("TicketNotFound", $"Ticket '{ticketId}' was not found.")
            : ApiResults.Ok(SupportTicketMapping.ToDetailResponse(
                detail, SupportRaisedByTypes.PetParent, petParentId));
    }

    private static async Task<IResult> UploadPhoto(
        Guid petParentId,
        Guid ticketId,
        IFormFile? file,
        ISupportTicketService ticketService,
        IPawfrontBlobStorage blobStorage,
        CancellationToken cancellationToken)
    {
        var validation = ValidatePhotoFile(file);
        if (validation is not null)
        {
            return validation;
        }

        // Resolve the ticket FIRST: the blob path is keyed by its id, and uploading before
        // knowing it exists would orphan a file on every stray call. This also gets the cap
        // and the wrong-kind cases answered without paying for an upload.
        var existing = await ticketService.GetAsync(
            ticketId, SupportRaisedByTypes.PetParent, petParentId, cancellationToken);

        if (existing is null || !existing.Ticket.WasRaisedBy(SupportRaisedByTypes.PetParent))
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
                ticketId, SupportRaisedByTypes.PetParent, petParentId, url, cancellationToken);

            return ApiResults.Ok(SupportTicketMapping.ToResponse(ticket, SupportRaisedByTypes.PetParent));
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
        Guid petParentId,
        Guid ticketId,
        SubmitTicketClarificationRequest request,
        ISupportTicketService ticketService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("InvalidRequest", "A reply is required.");
        }

        try
        {
            var detail = await ticketService.RecordClarificationAsync(
                ticketId, SupportRaisedByTypes.PetParent, petParentId, request.Reply, cancellationToken);

            return ApiResults.Ok(SupportTicketMapping.ToDetailResponse(
                detail, SupportRaisedByTypes.PetParent, petParentId));
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
