using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Storage;
using Pawfront.Contracts.Bookings;

namespace Pawfront.Api.Endpoints;

internal static class BookingEndpoints
{
    public static IEndpointRouteBuilder MapBookingEndpoints(this IEndpointRouteBuilder builder)
    {
        var providerScoped = builder.MapGroup("/providers/{providerId:guid}/bookings");
        providerScoped.MapPost("/", CreateBooking);
        providerScoped.MapPost("/custom", CreateCustomBooking);
        providerScoped.MapGet("/", ListByProvider);
        // Legacy generic status setter — kept as a back-compat shim. The discrete
        // per-transition endpoints below are the preferred surface.
        providerScoped.MapPost("/{bookingId:guid}/status", UpdateStatus);
        providerScoped.MapGet("/{bookingId:guid}/status-history", GetStatusHistory);

        // Per-transition endpoints (provider actor). Each pins its target status.
        providerScoped.MapPost("/{bookingId:guid}/accept", AcceptBooking);
        providerScoped.MapPost("/{bookingId:guid}/decline", DeclineBooking);
        providerScoped.MapPost("/{bookingId:guid}/start", StartBooking);
        providerScoped.MapPost("/{bookingId:guid}/complete", CompleteBooking);
        providerScoped.MapPost("/{bookingId:guid}/cancel", ProviderCancelBooking);
        providerScoped.MapPost("/{bookingId:guid}/modifications", RequestModification);
        providerScoped.MapPost("/{bookingId:guid}/modifications/accept", AcceptModification);
        providerScoped.MapPost("/{bookingId:guid}/modifications/decline", DeclineModification);
        providerScoped.MapPost("/{bookingId:guid}/evidence", UploadEvidence).DisableAntiforgery();
        providerScoped.MapGet("/{bookingId:guid}/evidence", ListEvidence);

        builder.MapGet("/bookings/{bookingId:guid}", GetBooking);
        builder.MapPost("/bookings/{bookingId:guid}/cancel", CancelBooking);

        builder.MapGet("/pet-parents/{petParentId:guid}/bookings", ListByPetParent);

        return builder;
    }

    private static async Task<IResult> CreateBooking(
        Guid providerId,
        CreateBookingRequest request,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.CreateAsync(
                new CreateBookingCommand(
                    providerId,
                    request.PetParentId,
                    request.ServiceId,
                    request.BookingDate,
                    request.StartTime,
                    request.EndTime,
                    request.ServiceItemCode,
                    request.JobNotes),
                cancellationToken);

            return ApiResults.Created($"/api/v1/bookings/{result.BookingId}", ToResponse(result));
        }
        catch (BookingServiceInvalidException exception)
        {
            return ApiResults.BadRequest("InvalidServiceId", exception.Message);
        }
        catch (BookingProviderNotRegisteredException exception)
        {
            return ApiResults.NotFound("ServiceNotRegistered", exception.Message);
        }
        catch (BookingOfferingNotConfiguredException exception)
        {
            return ApiResults.BadRequest("OfferingNotConfigured", exception.Message);
        }
        catch (BookingProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderNotFound", exception.Message);
        }
        catch (BookingProviderInactiveException exception)
        {
            return ApiResults.Conflict("ProviderInactive", exception.Message);
        }
        catch (BookingGroomingItemCodeRequiredException exception)
        {
            return ApiResults.BadRequest("ServiceItemCodeRequired", exception.Message);
        }
        catch (BookingGroomingItemNotOfferedException exception)
        {
            return ApiResults.BadRequest("ServiceItemNotOffered", exception.Message);
        }
        catch (BookingGroomingItemInactiveException exception)
        {
            return ApiResults.Conflict("ServiceItemInactive", exception.Message);
        }
        catch (BookingPetParentNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }
        catch (BookingCapacityExceededException exception)
        {
            return ApiResults.Conflict("CapacityExceeded", exception.Message);
        }
        catch (ProviderClosedOnDateException exception)
        {
            return ApiResults.Conflict("ServiceClosed", exception.Message);
        }
        catch (InvalidBookingTimeException exception)
        {
            return ApiResults.BadRequest("InvalidBookingTime", exception.Message);
        }
        catch (BookingNightStayUseDedicatedEndpointException exception)
        {
            return ApiResults.BadRequest("UseNightStayEndpoint", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> CreateCustomBooking(
        Guid providerId,
        CreateCustomBookingRequest request,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("InvalidRequest", "Request body is required.");
        }

        try
        {
            var result = await bookingService.CreateCustomAsync(
                new CreateCustomBookingCommand(
                    providerId,
                    request.ServiceId,
                    request.CustomerName,
                    request.CustomerMobileCountryCode,
                    request.CustomerMobile,
                    request.AnimalType,
                    request.PetName,
                    request.BookingDate,
                    request.StartTime,
                    request.EndTime,
                    request.ServiceLocation,
                    request.CustomerLocation,
                    request.PricePerHour,
                    request.JobNotes),
                cancellationToken);

            return ApiResults.Created($"/api/v1/bookings/{result.BookingId}", ToResponse(result));
        }
        catch (BookingServiceInvalidException exception)
        {
            return ApiResults.BadRequest("InvalidServiceId", exception.Message);
        }
        catch (BookingOfferingNotConfiguredException exception)
        {
            return ApiResults.BadRequest("OfferingNotConfigured", exception.Message);
        }
        catch (BookingProviderNotFoundException exception)
        {
            return ApiResults.NotFound("ProviderNotFound", exception.Message);
        }
        catch (BookingProviderInactiveException exception)
        {
            return ApiResults.Conflict("ProviderInactive", exception.Message);
        }
        catch (BookingCapacityExceededException exception)
        {
            return ApiResults.Conflict("CapacityExceeded", exception.Message);
        }
        catch (ProviderClosedOnDateException exception)
        {
            return ApiResults.Conflict("ServiceClosed", exception.Message);
        }
        catch (InvalidBookingTimeException exception)
        {
            return ApiResults.BadRequest("InvalidBookingTime", exception.Message);
        }
        catch (BookingNightStayUseDedicatedEndpointException exception)
        {
            return ApiResults.BadRequest("UseNightStayEndpoint", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> GetBooking(
        Guid bookingId,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var detail = await bookingService.GetDetailAsync(bookingId, cancellationToken);
        if (detail is null)
        {
            return ApiResults.NotFound("BookingNotFound", $"Booking '{bookingId}' was not found.");
        }

        // Surface the staged proposal so the provider can see a parent's proposed
        // change before accepting/declining (start-OTP is parent-only → null here).
        BookingModificationResponse? pending = null;
        if (BookingStatuses.ModificationRequested.Contains(detail.Row.Status))
        {
            var mod = await bookingService.GetPendingModificationAsync(bookingId, cancellationToken);
            pending = ToModificationResponse(mod);
        }

        return ApiResults.Ok(ToBookingDetailResponse(detail, startOtp: null, pending));
    }

    private static BookingModificationResponse? ToModificationResponse(BookingModificationResult? mod) =>
        mod is null
            ? null
            : new BookingModificationResponse(
                mod.BookingModificationId, mod.BookingId, mod.RequestedByActor, mod.RequestedByActorId,
                mod.ProposedBookingDate, mod.ProposedStartTime, mod.ProposedEndTime, mod.Note, mod.CreatedAtUtc);

    private static async Task<IResult> CancelBooking(
        Guid bookingId,
        CancelBookingRequest request,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.CancelAsync(bookingId, request.PetParentId, cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (BookingNotFoundException exception)
        {
            return ApiResults.NotFound("BookingNotFound", exception.Message);
        }
        catch (BookingCancellationForbiddenException exception)
        {
            return ApiResults.BadRequest("BookingCancellationForbidden", exception.Message);
        }
        catch (BookingAlreadyCancelledException exception)
        {
            return ApiResults.Conflict("BookingAlreadyCancelled", exception.Message);
        }
    }

    private static async Task<IResult> UpdateStatus(
        Guid providerId,
        Guid bookingId,
        UpdateBookingStatusRequest request,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Status))
        {
            return ApiResults.BadRequest("InvalidRequest", "A status is required.");
        }

        try
        {
            var result = await bookingService.UpdateStatusAsync(
                new UpdateBookingStatusCommand(
                    bookingId,
                    request.Status,
                    BookingStatusActor.Provider,
                    providerId,
                    request.Note),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (UnsupportedBookingStatusException exception)
        {
            return ApiResults.BadRequest("UnsupportedBookingStatus", exception.Message);
        }
        catch (BookingNotFoundException exception)
        {
            return ApiResults.NotFound("BookingNotFound", exception.Message);
        }
        catch (BookingStatusForbiddenException exception)
        {
            return ApiResults.Forbidden("Forbidden", exception.Message);
        }
        catch (BookingStatusNotAllowedException exception)
        {
            return ApiResults.BadRequest("BookingStatusNotAllowed", exception.Message);
        }
        catch (BookingStatusTerminalException exception)
        {
            return ApiResults.Conflict("BookingStatusTerminal", exception.Message);
        }
        catch (BookingStatusUnchangedException exception)
        {
            return ApiResults.Conflict("BookingStatusUnchanged", exception.Message);
        }
    }

    private static async Task<IResult> GetStatusHistory(
        Guid providerId,
        Guid bookingId,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        // Don't leak other providers' bookings: confirm the booking belongs to
        // this provider before returning its audit trail.
        var booking = await bookingService.GetAsync(bookingId, cancellationToken);
        if (booking is null || booking.ProviderId != providerId)
        {
            return ApiResults.NotFound("BookingNotFound", $"Booking '{bookingId}' was not found.");
        }

        var history = await bookingService.ListStatusHistoryAsync(bookingId, cancellationToken);
        return ApiResults.Ok(history.Select(ToHistoryResponse).ToArray());
    }

    private static async Task<IResult> ListByProvider(
        Guid providerId,
        DateOnly? date,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var results = await bookingService.ListByProviderAsync(providerId, date, cancellationToken);
        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> ListByPetParent(
        Guid petParentId,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var results = await bookingService.ListByPetParentAsync(petParentId, cancellationToken);
        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    // --- Per-transition handlers (provider actor) ---------------------------

    private static Task<IResult> AcceptBooking(Guid providerId, Guid bookingId, IBookingService s, CancellationToken ct)
        => SetStatusAsync(providerId, bookingId, BookingStatuses.Confirmed, s, ct);

    private static Task<IResult> DeclineBooking(Guid providerId, Guid bookingId, IBookingService s, CancellationToken ct)
        => SetStatusAsync(providerId, bookingId, BookingStatuses.ProviderDeclined, s, ct);

    private static Task<IResult> CompleteBooking(Guid providerId, Guid bookingId, IBookingService s, CancellationToken ct)
        => SetStatusAsync(providerId, bookingId, BookingStatuses.Completed, s, ct);

    private static Task<IResult> ProviderCancelBooking(Guid providerId, Guid bookingId, IBookingService s, CancellationToken ct)
        => SetStatusAsync(providerId, bookingId, BookingStatuses.ProviderCancelled, s, ct);

    private static async Task<IResult> SetStatusAsync(
        Guid providerId, Guid bookingId, string status, IBookingService bookingService, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.UpdateStatusAsync(
                new UpdateBookingStatusCommand(bookingId, status, BookingStatusActor.Provider, providerId, null),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    private static async Task<IResult> StartBooking(
        Guid providerId, Guid bookingId, StartBookingRequest request, IBookingService bookingService, CancellationToken cancellationToken)
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
        Guid providerId, Guid bookingId, RequestBookingModificationRequest request, IBookingService bookingService, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("InvalidRequest", "Request body is required.");
        }

        try
        {
            var result = await bookingService.RequestModificationAsync(
                new RequestBookingModificationCommand(
                    bookingId, BookingStatusActor.Provider, providerId,
                    request.BookingDate, request.StartTime, request.EndTime, request.Note),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    private static Task<IResult> AcceptModification(
        Guid providerId, Guid bookingId, RespondBookingModificationRequest? request, IBookingService s, CancellationToken ct)
        => RespondModificationAsync(providerId, bookingId, accept: true, request?.Note, s, ct);

    private static Task<IResult> DeclineModification(
        Guid providerId, Guid bookingId, RespondBookingModificationRequest? request, IBookingService s, CancellationToken ct)
        => RespondModificationAsync(providerId, bookingId, accept: false, request?.Note, s, ct);

    private static async Task<IResult> RespondModificationAsync(
        Guid providerId, Guid bookingId, bool accept, string? note, IBookingService bookingService, CancellationToken cancellationToken)
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
        IBookingService bookingService,
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
            return ApiResults.Created($"/api/v1/bookings/{bookingId}/evidence/{result.BookingEvidenceId}",
                new BookingEvidenceResponse(result.BookingEvidenceId, result.BookingId, result.PhotoUrl, result.CreatedAtUtc));
        }
        catch (BookingNotFoundException exception)
        {
            return ApiResults.NotFound("BookingNotFound", exception.Message);
        }
    }

    private static async Task<IResult> ListEvidence(
        Guid providerId, Guid bookingId, IBookingService bookingService, CancellationToken cancellationToken)
    {
        // Don't leak other providers' bookings.
        var booking = await bookingService.GetAsync(bookingId, cancellationToken);
        if (booking is null || booking.ProviderId != providerId)
        {
            return ApiResults.NotFound("BookingNotFound", $"Booking '{bookingId}' was not found.");
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
        BookingNotFoundException or BookingStatusForbiddenException or BookingNotStartableException
        or InvalidStartOtpException or StartOtpExpiredException or BookingNotModifiableException
        or BookingModificationConflictException or NoPendingModificationException
        or BookingModificationCapacityException or BookingServiceInvalidException
        or BookingOfferingNotConfiguredException or BookingGroomingItemCodeRequiredException
        or BookingGroomingItemNotOfferedException or BookingGroomingItemInactiveException
        or ProviderClosedOnDateException or InvalidBookingTimeException
        or BookingNightStayUseDedicatedEndpointException or BookingStatusNotAllowedException
        or BookingStatusTerminalException or BookingStatusUnchangedException
        or UnsupportedBookingStatusException or ArgumentException;

    private static IResult MapBookingError(Exception ex) => ex switch
    {
        BookingNotFoundException e => ApiResults.NotFound("BookingNotFound", e.Message),
        BookingStatusForbiddenException e => ApiResults.Forbidden("Forbidden", e.Message),
        BookingNotStartableException e => ApiResults.Conflict("BookingNotStartable", e.Message),
        InvalidStartOtpException e => ApiResults.BadRequest("InvalidStartOtp", e.Message),
        StartOtpExpiredException e => ApiResults.Conflict("StartOtpExpired", e.Message),
        BookingNotModifiableException e => ApiResults.Conflict("BookingNotModifiable", e.Message),
        BookingModificationConflictException e => ApiResults.Conflict("ModificationAlreadyPending", e.Message),
        NoPendingModificationException e => ApiResults.Conflict("NoPendingModification", e.Message),
        BookingModificationCapacityException e => ApiResults.Conflict("CapacityExceeded", e.Message),
        BookingServiceInvalidException e => ApiResults.BadRequest("InvalidServiceId", e.Message),
        BookingOfferingNotConfiguredException e => ApiResults.BadRequest("OfferingNotConfigured", e.Message),
        BookingGroomingItemCodeRequiredException e => ApiResults.BadRequest("ServiceItemCodeRequired", e.Message),
        BookingGroomingItemNotOfferedException e => ApiResults.BadRequest("ServiceItemNotOffered", e.Message),
        BookingGroomingItemInactiveException e => ApiResults.Conflict("ServiceItemInactive", e.Message),
        ProviderClosedOnDateException e => ApiResults.Conflict("ServiceClosed", e.Message),
        InvalidBookingTimeException e => ApiResults.BadRequest("InvalidBookingTime", e.Message),
        BookingNightStayUseDedicatedEndpointException e => ApiResults.BadRequest("UseNightStayEndpoint", e.Message),
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

    /// <summary>
    /// Maps the enriched booking-detail result into the four-section response.
    /// App bookings draw Parent/Pet details from the joined records; Custom
    /// walk-ins from the booking's own free-text fields.
    /// </summary>
    private static BookingDetailResponse ToBookingDetailResponse(
        BookingDetailResult detail,
        StartOtpResponse? startOtp,
        BookingModificationResponse? pendingModification)
    {
        var row = detail.Row;
        var isCustom = string.Equals(row.Source, "Custom", StringComparison.Ordinal);

        return new BookingDetailResponse(
            new BookingDetailsSection(
                row.BookingId,
                detail.JobId,
                row.ProviderId,
                row.ServiceId,
                row.ServiceCategory,
                row.SubCategory,
                row.ServiceItemCode,
                row.BookingDate,
                row.StartTime,
                row.EndTime,
                row.Status,
                row.Source,
                detail.ServiceLocation,
                row.CustomerLocation,
                row.JobNotes,
                row.CreatedAtUtc,
                row.UpdatedAtUtc,
                row.CancelledAtUtc),
            new ParentDetailsSection(
                row.PetParentId,
                isCustom ? row.CustomerName : CombineName(row.ParentFirstName, row.ParentLastName),
                row.CustomerMobileCountryCode ?? row.ParentMobileCountryCode,
                row.CustomerMobile ?? row.ParentMobileNumber,
                row.ParentGender,
                row.ParentPhotoUrl),
            new PetDetailsSection(
                row.PetId,
                row.PetProfileName ?? row.PetName,
                row.PetType ?? row.AnimalType,
                row.PetGender,
                row.PetPhotoUrl),
            new PaymentDetailsSection(
                detail.PricePerHour,
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

    private static BookingResponse ToResponse(BookingResult result) =>
        new(result.BookingId,
            result.ProviderId,
            result.PetParentId,
            result.ServiceId,
            result.ServiceCategory,
            result.SubCategory,
            result.BookingDate,
            result.StartTime,
            result.EndTime,
            result.Status,
            result.CreatedAtUtc,
            result.UpdatedAtUtc,
            result.CancelledAtUtc,
            result.ServiceItemCode,
            result.Source,
            result.CustomerName,
            result.CustomerMobileCountryCode,
            result.CustomerMobile,
            result.AnimalType,
            result.PetName,
            result.ServiceLocation,
            result.CustomerLocation,
            result.PricePerHour,
            result.JobNotes,
            result.PetId);
}
