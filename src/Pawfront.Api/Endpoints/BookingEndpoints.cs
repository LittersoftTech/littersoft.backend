using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Reviews;
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
        // Job lifecycle: start-job (issues start-OTP) → verify start-OTP (→ IN_PROGRESS)
        // → complete (→ COMPLETED, no OTP).
        providerScoped.MapPost("/{bookingId:guid}/start-job", StartJob);
        providerScoped.MapPost("/{bookingId:guid}/start-job/verify", VerifyStartOtp);
        providerScoped.MapPost("/{bookingId:guid}/complete", CompleteBooking);
        // Payment: the parent has paid the provider → PAID + a payment ledger row.
        providerScoped.MapPost("/{bookingId:guid}/paid", MarkPaid);
        providerScoped.MapPost("/{bookingId:guid}/prescription", UpsertPrescription);
        providerScoped.MapPost("/{bookingId:guid}/cancel", ProviderCancelBooking);
        providerScoped.MapPost("/{bookingId:guid}/no-show", MarkParentNoShow);
        // What the provider has changed since the booking was made — read this
        // before opening the edit screen so the app can confirm the new terms.
        providerScoped.MapGet("/{bookingId:guid}/terms-changes", GetTermsChanges);
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
                    request.JobNotes,
                    LocationType: request.LocationType),
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
        catch (PetAlreadyBookedException exception)
        {
            return ApiResults.Conflict("PetAlreadyBooked", exception.Message);
        }
        catch (ProviderClosedOnDateException exception)
        {
            return ApiResults.Conflict("ServiceClosed", exception.Message);
        }
        catch (InvalidBookingTimeException exception)
        {
            return ApiResults.BadRequest("InvalidBookingTime", exception.Message);
        }
        catch (BookingLeadTimeTooShortException exception)
        {
            return ApiResults.Conflict("BookingLeadTimeTooShort", exception.Message);
        }
        catch (BookingNightStayUseDedicatedEndpointException exception)
        {
            return ApiResults.BadRequest("UseNightStayEndpoint", exception.Message);
        }
        catch (UnsupportedBookingLocationTypeException exception)
        {
            return ApiResults.BadRequest("UnsupportedLocationType", exception.Message);
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
        catch (UnsupportedBookingLocationTypeException exception)
        {
            return ApiResults.BadRequest("UnsupportedLocationType", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> GetBooking(
        Guid bookingId,
        IBookingService bookingService,
        IBookingReviewService reviewService,
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

        var myRating = await reviewService.GetAsync(
            ReviewedBookingTypes.SingleDay, bookingId, ReviewerTypes.Provider, cancellationToken);

        return ApiResults.Ok(ToBookingDetailResponse(detail, startOtp: null, pending, myRating));
    }

    private static BookingModificationResponse? ToModificationResponse(BookingModificationResult? mod) =>
        mod is null
            ? null
            : new BookingModificationResponse(
                mod.BookingModificationId, mod.BookingId, mod.RequestedByActor, mod.RequestedByActorId,
                mod.ProposedBookingDate, mod.ProposedStartTime, mod.ProposedEndTime, mod.Note, mod.CreatedAtUtc,
                ToAcknowledgedTermsResponse(mod.AcknowledgedTerms));

    /// <summary>
    /// The provider's terms that changed since this booking was created. Always
    /// 200 — an unchanged booking simply reports <c>hasChanges: false</c> with an
    /// empty list. Shared shape with the night-stay twin.
    /// </summary>
    private static async Task<IResult> GetTermsChanges(
        Guid providerId,
        Guid bookingId,
        IBookingService bookingService,
        IBookingTermsChangeService termsChangeService,
        CancellationToken cancellationToken)
    {
        // Don't leak another provider's terms: confirm the booking is this
        // provider's before diffing it.
        var booking = await bookingService.GetAsync(bookingId, cancellationToken);
        if (booking is null || booking.ProviderId != providerId)
        {
            return ApiResults.NotFound("BookingNotFound", $"Booking '{bookingId}' was not found.");
        }

        var result = await termsChangeService.GetForBookingAsync(bookingId, cancellationToken);
        return ApiResults.Ok(ToTermsChangesResponse(result));
    }

    // Shared with NightStayBookingEndpoints (same host, same shape).
    internal static BookingTermsChangesResponse ToTermsChangesResponse(BookingTermsChangeResult result) =>
        new(result.BookingId,
            result.HasChanges,
            result.Changes
                .Select(c => new BookingTermsChangeResponse(
                    c.Field, c.ChangeType, c.BookedValue, c.CurrentValue, c.Message))
                .ToList());

    internal static AcknowledgedTermsResponse? ToAcknowledgedTermsResponse(BookingAcknowledgedTerms? terms) =>
        terms is null
            ? null
            : new AcknowledgedTermsResponse(
                terms.UnitPrice, terms.CancellationPolicyHours, terms.DropOffTime, terms.PickUpTime,
                terms.AddressLine, terms.City, terms.ZipCode, terms.Latitude, terms.Longitude);

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
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
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
        // The list rows carry frozen-at-creation extras for the parent host's
        // cards; this provider-facing lookup keeps its original flat shape.
        var results = await bookingService.ListByPetParentAsync(petParentId, cancellationToken);
        return ApiResults.Ok(results.Select(r => ToResponse(r.Booking)).ToArray());
    }

    // --- Per-transition handlers (provider actor) ---------------------------

    private static Task<IResult> AcceptBooking(Guid providerId, Guid bookingId, IBookingService s, CancellationToken ct)
        => SetStatusAsync(providerId, bookingId, BookingStatuses.Confirmed, s, ct);

    private static Task<IResult> DeclineBooking(Guid providerId, Guid bookingId, IBookingService s, CancellationToken ct)
        => SetStatusAsync(providerId, bookingId, BookingStatuses.ProviderDeclined, s, ct);

    private static async Task<IResult> CompleteBooking(
        Guid providerId,
        Guid bookingId,
        CompleteBookingRequest? request,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        // The body is optional — it only carries the next-consultation date and/or
        // a vet prescription. Completion itself needs nothing beyond the route.
        try
        {
            var result = await bookingService.CompleteAsync(
                new CompleteBookingCommand(
                    bookingId, providerId, request?.NextConsultationDate,
                    ToPrescriptionInput(request?.Prescription)),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (NextConsultationNotSupportedException exception)
        {
            return ApiResults.BadRequest("NextConsultationNotSupported", exception.Message);
        }
        catch (NextConsultationRequiresPetException exception)
        {
            return ApiResults.BadRequest("NextConsultationRequiresPet", exception.Message);
        }
        catch (InvalidNextConsultationDateException exception)
        {
            return ApiResults.BadRequest("InvalidNextConsultationDate", exception.Message);
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    /// <summary>
    /// The provider records that the parent has paid them for a COMPLETED booking
    /// (→ PAID). The amount is computed from the booking's price-locked total; the
    /// body only carries the payment method (Cash / Digital). App bookings only.
    /// </summary>
    private static async Task<IResult> MarkPaid(
        Guid providerId,
        Guid bookingId,
        MarkBookingPaidRequest? request,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PaymentMethod))
        {
            return ApiResults.BadRequest("InvalidRequest", "A payment method (Cash or Digital) is required.");
        }

        try
        {
            var result = await bookingService.MarkPaidAsync(
                new MarkBookingPaidCommand(bookingId, providerId, request.PaymentMethod),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    /// <summary>
    /// Records (upserts) the vet's prescription for a booking, independent of the
    /// complete flow — the vet can fill or edit it while the job is started or after
    /// it's completed. Vet bookings only. Returns the saved prescription block.
    /// </summary>
    private static async Task<IResult> UpsertPrescription(
        Guid providerId,
        Guid bookingId,
        PrescriptionRequest request,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.UpsertPrescriptionAsync(
                new UpsertBookingPrescriptionCommand(
                    bookingId,
                    providerId,
                    request.PrescriptionText,
                    request.IsPetVaccinated,
                    request.Vaccinations ?? Array.Empty<string>()),
                cancellationToken);
            return ApiResults.Ok(ToPrescriptionSection(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    private static PrescriptionInput? ToPrescriptionInput(PrescriptionRequest? request) =>
        request is null
            ? null
            : new PrescriptionInput(
                request.PrescriptionText,
                request.IsPetVaccinated,
                request.Vaccinations ?? Array.Empty<string>());

    private static PrescriptionDetailsSection ToPrescriptionSection(BookingPrescriptionResult result) =>
        new(result.PrescriptionText, result.IsPetVaccinated, result.Vaccinations, result.NextConsultationDate);

    /// <summary>Builds the detail read's prescription block — null until a vet records one.</summary>
    private static PrescriptionDetailsSection? ToPrescriptionSection(BookingDetailRow row) =>
        row.HasPrescription
            ? new PrescriptionDetailsSection(
                row.PrescriptionText,
                row.IsPetVaccinated ?? false,
                row.PrescriptionVaccinations ?? Array.Empty<string>(),
                row.NextConsultationDate)
            : null;

    private static Task<IResult> ProviderCancelBooking(Guid providerId, Guid bookingId, IBookingService s, CancellationToken ct)
        => SetStatusAsync(providerId, bookingId, BookingStatuses.ProviderCancelled, s, ct);

    /// <summary>
    /// The provider reports that the parent (pet) never showed up. Only allowed
    /// 30+ minutes after the booking's scheduled start (409 NoShowTooEarly).
    /// </summary>
    private static Task<IResult> MarkParentNoShow(Guid providerId, Guid bookingId, IBookingService s, CancellationToken ct)
        => SetStatusAsync(providerId, bookingId, BookingStatuses.ParentNoShow, s, ct);

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

    /// <summary>
    /// Provider taps "Start Job" (customer arrived). Moves the booking to START_JOB
    /// and issues the parent-facing start-OTP. Only on the booking's own service
    /// date (409 BookingNotOnServiceDate otherwise) and only while the provider is
    /// inside their own weekly working hours (409 OutsideWorkingHours otherwise).
    /// </summary>
    private static async Task<IResult> StartJob(
        Guid providerId, Guid bookingId, IBookingService bookingService, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.StartJobAsync(
                new StartBookingCommand(bookingId, providerId), cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    /// <summary>
    /// Provider enters the start-code the parent showed. Moves the booking
    /// START_JOB → IN_PROGRESS (6 wrong attempts cancel the job).
    /// </summary>
    private static async Task<IResult> VerifyStartOtp(
        Guid providerId, Guid bookingId, VerifyStartOtpRequest? request,
        IBookingService bookingService, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.OtpCode))
        {
            return ApiResults.BadRequest(
                "InvalidRequest", "The parent's start code is required to begin the job.");
        }

        try
        {
            var result = await bookingService.VerifyStartOtpAsync(
                bookingId, providerId, request.OtpCode, cancellationToken);
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
                    request.BookingDate, request.StartTime, request.EndTime, request.Note,
                    request.AcknowledgeTermsChanges),
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
        or BookingStartOutsideWorkingHoursException or BookingStartNotOnServiceDateException
        or BookingJobInProgressException
        or InvalidStartOtpException or StartOtpExpiredException or OtpAttemptsExceededException
        or BookingNotCompletableException or BookingNotModifiableException
        or BookingModificationConflictException or NoPendingModificationException
        or BookingModificationCapacityException or BookingTermsChangedException
        or BookingModificationWindowClosedException or BookingModificationExpiredException
        or BookingServiceInvalidException
        or BookingOfferingNotConfiguredException or BookingGroomingItemCodeRequiredException
        or BookingGroomingItemNotOfferedException or BookingGroomingItemInactiveException
        or ProviderClosedOnDateException or InvalidBookingTimeException
        or BookingLeadTimeTooShortException
        or BookingNightStayUseDedicatedEndpointException or BookingStatusNotAllowedException
        or BookingStatusTerminalException or BookingStatusUnchangedException
        or BookingNoShowTooEarlyException or BookingExpiredException
        or BookingPrescriptionForbiddenException or BookingPrescriptionNotVetException
        or BookingPrescriptionInvalidStateException
        or BookingNotPayableException or BookingAlreadyPaidException
        or BookingPaymentNotAppException or BookingNotPriceableException
        or UnsupportedBookingPaymentMethodException
        or UnsupportedBookingStatusException or ArgumentException;

    private static IResult MapBookingError(Exception ex) => ex switch
    {
        BookingNotFoundException e => ApiResults.NotFound("BookingNotFound", e.Message),
        BookingStatusForbiddenException e => ApiResults.Forbidden("Forbidden", e.Message),
        BookingNotStartableException e => ApiResults.Conflict("BookingNotStartable", e.Message),
        BookingStartOutsideWorkingHoursException e => ApiResults.Conflict("OutsideWorkingHours", e.Message),
        BookingStartNotOnServiceDateException e => ApiResults.Conflict("BookingNotOnServiceDate", e.Message),
        BookingJobInProgressException e => ApiResults.Conflict("BookingInProgress", e.Message),
        InvalidStartOtpException e => ApiResults.BadRequest("InvalidStartOtp", e.Message),
        StartOtpExpiredException e => ApiResults.Conflict("StartOtpExpired", e.Message),
        OtpAttemptsExceededException e => ApiResults.Conflict("OtpAttemptsExceeded", e.Message),
        BookingNotCompletableException e => ApiResults.Conflict("BookingNotCompletable", e.Message),
        BookingNotModifiableException e => ApiResults.Conflict("BookingNotModifiable", e.Message),
        BookingModificationConflictException e => ApiResults.Conflict("ModificationAlreadyPending", e.Message),
        NoPendingModificationException e => ApiResults.Conflict("NoPendingModification", e.Message),
        BookingModificationCapacityException e => ApiResults.Conflict("CapacityExceeded", e.Message),
        BookingTermsChangedException e => ApiResults.Conflict("BookingTermsChanged", e.Message),
        BookingModificationWindowClosedException e => ApiResults.Conflict("ModificationWindowClosed", e.Message),
        BookingModificationExpiredException e => ApiResults.Conflict("ModificationRequestExpired", e.Message),
        BookingServiceInvalidException e => ApiResults.BadRequest("InvalidServiceId", e.Message),
        BookingOfferingNotConfiguredException e => ApiResults.BadRequest("OfferingNotConfigured", e.Message),
        BookingGroomingItemCodeRequiredException e => ApiResults.BadRequest("ServiceItemCodeRequired", e.Message),
        BookingGroomingItemNotOfferedException e => ApiResults.BadRequest("ServiceItemNotOffered", e.Message),
        BookingGroomingItemInactiveException e => ApiResults.Conflict("ServiceItemInactive", e.Message),
        ProviderClosedOnDateException e => ApiResults.Conflict("ServiceClosed", e.Message),
        InvalidBookingTimeException e => ApiResults.BadRequest("InvalidBookingTime", e.Message),
        BookingLeadTimeTooShortException e => ApiResults.Conflict("BookingLeadTimeTooShort", e.Message),
        BookingNightStayUseDedicatedEndpointException e => ApiResults.BadRequest("UseNightStayEndpoint", e.Message),
        BookingStatusNotAllowedException e => ApiResults.BadRequest("BookingStatusNotAllowed", e.Message),
        BookingStatusTerminalException e => ApiResults.Conflict("BookingStatusTerminal", e.Message),
        BookingStatusUnchangedException e => ApiResults.Conflict("BookingStatusUnchanged", e.Message),
        BookingNoShowTooEarlyException e => ApiResults.Conflict("NoShowTooEarly", e.Message),
        BookingExpiredException e => ApiResults.Conflict("BookingExpired", e.Message),
        BookingPrescriptionForbiddenException e => ApiResults.Forbidden("Forbidden", e.Message),
        BookingPrescriptionNotVetException e => ApiResults.BadRequest("PrescriptionNotVetBooking", e.Message),
        BookingPrescriptionInvalidStateException e => ApiResults.Conflict("PrescriptionNotAllowed", e.Message),
        BookingNotPayableException e => ApiResults.Conflict("BookingNotPayable", e.Message),
        BookingAlreadyPaidException e => ApiResults.Conflict("BookingAlreadyPaid", e.Message),
        BookingPaymentNotAppException e => ApiResults.BadRequest("PaymentNotAppBooking", e.Message),
        BookingNotPriceableException e => ApiResults.Conflict("BookingNotPriceable", e.Message),
        UnsupportedBookingPaymentMethodException e => ApiResults.BadRequest("UnsupportedPaymentMethod", e.Message),
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
        BookingModificationResponse? pendingModification,
        BookingReviewRecord? myRating = null)
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
                row.ParentPhotoUrl,
                row.ParentAddressLine,
                row.ParentCity,
                row.ParentZipCode,
                row.ParentLatitude,
                row.ParentLongitude),
            new PetDetailsSection(
                row.PetId,
                row.PetProfileName ?? row.PetName,
                row.PetType ?? row.AnimalType,
                row.PetGender,
                row.PetPhotoUrl,
                row.PetBreed,
                row.PetVaccinationStatus,
                row.PetVaccinationType,
                row.PetVaccinationDose,
                row.PetPrescription,
                row.PetSterilizationStatus,
                row.PetMedicalHistory,
                row.PetTemperament),
            new ProviderDetailsSection(
                row.ProviderId,
                CombineName(row.ProviderFirstName, row.ProviderLastName),
                row.ProviderMobileCountryCode,
                row.ProviderMobileNumber,
                row.ProviderGender,
                detail.ProviderPhotoUrl,
                detail.ProviderAddress,
                detail.ProviderCity,
                detail.ProviderZip),
            new PaymentDetailsSection(
                detail.PricePerHour,
                detail.TotalAmount,
                detail.PawfrontFee,
                detail.FeePercentage,
                row.PayoutStatus,
                row.PayoutId,
                row.PayoutMethod,
                row.PaidAtUtc),
            new CancellationPolicyDetailsSection(detail.MinimumHoursBeforeCancellation),
            ToLocationSection(detail.Location),
            startOtp,
            pendingModification,
            ToPrescriptionSection(row),
            // This host's side only: the provider's own rating of the parent. The
            // review the PARENT left for the provider is public and read via
            // GET /providers/{providerId}/reviews.
            ReviewResponseMapping.ToDetailsSection(row.Status, row.Source, myRating));
    }

    internal static BookingLocationDetailsSection ToLocationSection(BookingLocationResult location) =>
        new(location.LocationType,
            location.AddressLine,
            location.City,
            location.ZipCode,
            location.Latitude,
            location.Longitude);

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
