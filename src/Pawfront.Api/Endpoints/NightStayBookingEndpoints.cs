using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Storage;
using Pawfront.Contracts.Bookings;

namespace Pawfront.Api.Endpoints;

/// <summary>
/// Provider-host endpoints for multi-night boarding bookings. The pet-parent host
/// owns create / list-mine / parent-side transitions; this host adds the
/// PROVIDER-side management surface (accept, decline, start-with-OTP, evidence,
/// complete, provider-initiated modifications, cancel). The provider is the route
/// segment; the sprocs re-check that the booking belongs to them.
/// </summary>
internal static class NightStayBookingEndpoints
{
    public static IEndpointRouteBuilder MapProviderNightStayBookingEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder.MapGroup("/providers/{providerId:guid}/night-stay-bookings");
        group.MapGet("/", ListByProvider);
        group.MapGet("/{bookingId:guid}", GetBooking);
        group.MapGet("/{bookingId:guid}/status-history", GetStatusHistory);

        group.MapPost("/{bookingId:guid}/accept", AcceptBooking);
        group.MapPost("/{bookingId:guid}/decline", DeclineBooking);
        group.MapPost("/{bookingId:guid}/start", StartBooking);
        group.MapPost("/{bookingId:guid}/complete", CompleteBooking);
        group.MapPost("/{bookingId:guid}/cancel", CancelBooking);
        group.MapPost("/{bookingId:guid}/modifications", RequestModification);
        group.MapPost("/{bookingId:guid}/modifications/accept", AcceptModification);
        group.MapPost("/{bookingId:guid}/modifications/decline", DeclineModification);
        group.MapPost("/{bookingId:guid}/evidence", UploadEvidence).DisableAntiforgery();
        group.MapGet("/{bookingId:guid}/evidence", ListEvidence);

        return builder;
    }

    private static async Task<IResult> ListByProvider(
        Guid providerId, DateOnly? date, INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        var results = await bookingService.ListByProviderAsync(providerId, date, cancellationToken);
        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> GetBooking(
        Guid providerId, Guid bookingId, INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        var detail = await bookingService.GetDetailAsync(bookingId, cancellationToken);
        if (detail is null || detail.Row.ProviderId != providerId)
        {
            return ApiResults.NotFound("NightStayBookingNotFound", $"Night stay booking '{bookingId}' was not found.");
        }

        // Surface the staged proposal so the provider can see a parent's proposed
        // change before accepting/declining (start-OTP is parent-only → null here).
        NightStayBookingModificationResponse? pending = null;
        if (BookingStatuses.ModificationRequested.Contains(detail.Row.Status))
        {
            var mod = await bookingService.GetPendingModificationAsync(bookingId, cancellationToken);
            pending = mod is null
                ? null
                : new NightStayBookingModificationResponse(
                    mod.NightStayBookingModificationId, mod.NightStayBookingId, mod.RequestedByActor, mod.RequestedByActorId,
                    mod.ProposedCheckInDate, mod.ProposedCheckOutDate, mod.Note, mod.CreatedAtUtc);
        }

        return ApiResults.Ok(ToDetailResponse(detail, startOtp: null, pending));
    }

    /// <summary>
    /// Maps the enriched night-stay detail into the sectioned response (the
    /// night-stay analog of the single-day booking-detail mapping).
    /// </summary>
    private static NightStayBookingDetailResponse ToDetailResponse(
        NightStayBookingDetailResult detail,
        StartOtpResponse? startOtp,
        NightStayBookingModificationResponse? pendingModification)
    {
        var row = detail.Row;
        return new NightStayBookingDetailResponse(
            new NightStayBookingDetailsSection(
                row.NightStayBookingId,
                detail.JobId,
                row.ProviderId,
                row.ServiceId,
                row.ServiceCategory,
                row.SubCategory,
                row.CheckInDate,
                row.CheckOutDate,
                row.DropOffTime,
                row.PickUpTime,
                detail.Nights,
                row.Status,
                detail.ServiceLocation,
                row.CreatedAtUtc,
                row.UpdatedAtUtc,
                row.CancelledAtUtc),
            new ParentDetailsSection(
                row.PetParentId,
                CombineName(row.ParentFirstName, row.ParentLastName),
                row.ParentMobileCountryCode,
                row.ParentMobileNumber,
                row.ParentGender,
                row.ParentPhotoUrl),
            new PetDetailsSection(
                row.PetId,
                row.PetProfileName,
                row.PetType,
                row.PetGender,
                row.PetPhotoUrl),
            new NightStayPaymentDetailsSection(
                detail.PricePerNight,
                detail.TotalAmount,
                detail.PawfrontFee,
                detail.FeePercentage,
                row.PayoutStatus,
                row.PayoutId),
            new CancellationPolicyDetailsSection(detail.MinimumHoursBeforeCancellation),
            startOtp,
            pendingModification);
    }

    private static string? CombineName(string? first, string? last)
    {
        var name = $"{first} {last}".Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static async Task<IResult> GetStatusHistory(
        Guid providerId, Guid bookingId, INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        var booking = await bookingService.GetAsync(bookingId, cancellationToken);
        if (booking is null || booking.ProviderId != providerId)
        {
            return ApiResults.NotFound("NightStayBookingNotFound", $"Night stay booking '{bookingId}' was not found.");
        }

        var history = await bookingService.ListStatusHistoryAsync(bookingId, cancellationToken);
        return ApiResults.Ok(history.Select(ToHistoryResponse).ToArray());
    }

    private static Task<IResult> AcceptBooking(Guid providerId, Guid bookingId, INightStayBookingService s, CancellationToken ct)
        => SetStatusAsync(providerId, bookingId, BookingStatuses.Confirmed, s, ct);

    private static Task<IResult> DeclineBooking(Guid providerId, Guid bookingId, INightStayBookingService s, CancellationToken ct)
        => SetStatusAsync(providerId, bookingId, BookingStatuses.ProviderDeclined, s, ct);

    private static Task<IResult> CompleteBooking(Guid providerId, Guid bookingId, INightStayBookingService s, CancellationToken ct)
        => SetStatusAsync(providerId, bookingId, BookingStatuses.Completed, s, ct);

    private static Task<IResult> CancelBooking(Guid providerId, Guid bookingId, INightStayBookingService s, CancellationToken ct)
        => SetStatusAsync(providerId, bookingId, BookingStatuses.ProviderCancelled, s, ct);

    private static async Task<IResult> SetStatusAsync(
        Guid providerId, Guid bookingId, string status, INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.UpdateStatusAsync(
                new UpdateNightStayBookingStatusCommand(bookingId, status, BookingStatusActor.Provider, providerId, null),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    private static async Task<IResult> StartBooking(
        Guid providerId, Guid bookingId, StartBookingRequest request, INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.OtpCode))
        {
            return ApiResults.BadRequest("InvalidRequest", "An OTP code is required to start the job.");
        }

        try
        {
            var result = await bookingService.StartWithOtpAsync(
                new StartBookingCommand(bookingId, providerId, request.OtpCode), cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    private static async Task<IResult> RequestModification(
        Guid providerId, Guid bookingId, RequestNightStayBookingModificationRequest request,
        INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("InvalidRequest", "Request body is required.");
        }

        try
        {
            var result = await bookingService.RequestModificationAsync(
                new RequestNightStayBookingModificationCommand(
                    bookingId, BookingStatusActor.Provider, providerId, request.CheckInDate, request.CheckOutDate, request.Note),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    private static Task<IResult> AcceptModification(
        Guid providerId, Guid bookingId, RespondBookingModificationRequest? request, INightStayBookingService s, CancellationToken ct)
        => RespondModificationAsync(providerId, bookingId, accept: true, request?.Note, s, ct);

    private static Task<IResult> DeclineModification(
        Guid providerId, Guid bookingId, RespondBookingModificationRequest? request, INightStayBookingService s, CancellationToken ct)
        => RespondModificationAsync(providerId, bookingId, accept: false, request?.Note, s, ct);

    private static async Task<IResult> RespondModificationAsync(
        Guid providerId, Guid bookingId, bool accept, string? note, INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.RespondModificationAsync(
                new RespondBookingModificationCommand(bookingId, BookingStatusActor.Provider, providerId, accept, note),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    private static async Task<IResult> UploadEvidence(
        Guid providerId,
        Guid bookingId,
        IFormFile file,
        IPawfrontBlobStorage blobStorage,
        INightStayBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var validation = ValidatePhotoFile(file);
        if (validation is not null)
        {
            return validation;
        }

        await using var stream = file.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            BlobUploadKind.BookingEvidence, bookingId, file.FileName, stream, file.ContentType, cancellationToken);

        try
        {
            var result = await bookingService.AddEvidenceAsync(bookingId, providerId, url, cancellationToken);
            return ApiResults.Created($"/api/v1/providers/{providerId}/night-stay-bookings/{bookingId}/evidence/{result.BookingEvidenceId}",
                new BookingEvidenceResponse(result.BookingEvidenceId, result.BookingId, result.PhotoUrl, result.CreatedAtUtc));
        }
        catch (NightStayBookingNotFoundException exception)
        {
            return ApiResults.NotFound("NightStayBookingNotFound", exception.Message);
        }
    }

    private static async Task<IResult> ListEvidence(
        Guid providerId, Guid bookingId, INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        var booking = await bookingService.GetAsync(bookingId, cancellationToken);
        if (booking is null || booking.ProviderId != providerId)
        {
            return ApiResults.NotFound("NightStayBookingNotFound", $"Night stay booking '{bookingId}' was not found.");
        }

        var evidence = await bookingService.ListEvidenceAsync(bookingId, cancellationToken);
        return ApiResults.Ok(evidence
            .Select(e => new BookingEvidenceResponse(e.BookingEvidenceId, e.BookingId, e.PhotoUrl, e.CreatedAtUtc))
            .ToArray());
    }

    private const long MaxEvidenceBytes = 3L * 1024 * 1024;

    private static readonly HashSet<string> AllowedEvidenceContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/jpg", "image/png", "image/webp"
    };

    private static IResult? ValidatePhotoFile(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            return ApiResults.BadRequest("InvalidFile", "An image file is required.");
        }
        if (file.Length > MaxEvidenceBytes)
        {
            return ApiResults.BadRequest("ImageTooLarge", $"Photo must be {MaxEvidenceBytes / (1024 * 1024)} MB or smaller.");
        }
        if (string.IsNullOrWhiteSpace(file.ContentType) || !AllowedEvidenceContentTypes.Contains(file.ContentType))
        {
            return ApiResults.BadRequest("UnsupportedImageFormat", "Photo must be a JPEG, PNG, or WebP image.");
        }
        return null;
    }

    private static bool IsBookingError(Exception ex) => ex is
        NightStayBookingNotFoundException or BookingStatusForbiddenException or BookingNotStartableException
        or InvalidStartOtpException or StartOtpExpiredException or BookingNotModifiableException
        or BookingModificationConflictException or NoPendingModificationException
        or BookingModificationCapacityException or InvalidNightStayDatesException
        or ProviderClosedOnDateException or BookingOfferingNotConfiguredException
        or BookingStatusNotAllowedException or BookingStatusTerminalException
        or BookingStatusUnchangedException or UnsupportedBookingStatusException or ArgumentException;

    private static IResult MapBookingError(Exception ex) => ex switch
    {
        NightStayBookingNotFoundException e => ApiResults.NotFound("NightStayBookingNotFound", e.Message),
        BookingStatusForbiddenException e => ApiResults.Forbidden("Forbidden", e.Message),
        BookingNotStartableException e => ApiResults.Conflict("BookingNotStartable", e.Message),
        InvalidStartOtpException e => ApiResults.BadRequest("InvalidStartOtp", e.Message),
        StartOtpExpiredException e => ApiResults.Conflict("StartOtpExpired", e.Message),
        BookingNotModifiableException e => ApiResults.Conflict("BookingNotModifiable", e.Message),
        BookingModificationConflictException e => ApiResults.Conflict("ModificationAlreadyPending", e.Message),
        NoPendingModificationException e => ApiResults.Conflict("NoPendingModification", e.Message),
        BookingModificationCapacityException e => ApiResults.Conflict("CapacityExceeded", e.Message),
        InvalidNightStayDatesException e => ApiResults.BadRequest("InvalidNightStayDates", e.Message),
        ProviderClosedOnDateException e => ApiResults.Conflict("ServiceClosed", e.Message),
        BookingOfferingNotConfiguredException e => ApiResults.BadRequest("OfferingNotConfigured", e.Message),
        BookingStatusNotAllowedException e => ApiResults.BadRequest("BookingStatusNotAllowed", e.Message),
        BookingStatusTerminalException e => ApiResults.Conflict("BookingStatusTerminal", e.Message),
        BookingStatusUnchangedException e => ApiResults.Conflict("BookingStatusUnchanged", e.Message),
        UnsupportedBookingStatusException e => ApiResults.BadRequest("UnsupportedBookingStatus", e.Message),
        _ => ApiResults.BadRequest("InvalidRequest", ex.Message)
    };

    private static BookingStatusHistoryEntryResponse ToHistoryResponse(BookingStatusHistoryEntry entry) =>
        new(entry.BookingStatusHistoryId,
            entry.BookingId,
            entry.FromStatus,
            entry.ToStatus,
            entry.ChangedByActor,
            entry.ChangedByActorId,
            entry.Note,
            entry.ChangedAtUtc);

    private static NightStayBookingResponse ToResponse(NightStayBookingResult result) =>
        new(result.NightStayBookingId,
            result.ProviderId,
            result.PetParentId,
            result.ServiceId,
            result.ServiceCategory,
            result.SubCategory,
            result.CheckInDate,
            result.CheckOutDate,
            result.DropOffTime,
            result.PickUpTime,
            result.Status,
            result.CreatedAtUtc,
            result.UpdatedAtUtc,
            result.CancelledAtUtc,
            result.PetId);
}
