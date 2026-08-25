using Pawfront.Application.Blocks;
using Microsoft.AspNetCore.Mvc;
using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Reviews;
using Pawfront.Application.Storage;
using Pawfront.Contracts.Bookings;
using Pawfront.Application.Support;

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
        // Job lifecycle: start-job (issues start-OTP) → verify start-OTP (→ IN_PROGRESS)
        // → complete (→ COMPLETED, no OTP).
        group.MapPost("/{bookingId:guid}/start-job", StartJob);
        group.MapPost("/{bookingId:guid}/start-job/verify", VerifyStartOtp);
        group.MapPost("/{bookingId:guid}/complete", CompleteBooking);
        // Payment: the parent has paid the provider → PAID + a payment ledger row.
        group.MapPost("/{bookingId:guid}/paid", MarkPaid);
        group.MapPost("/{bookingId:guid}/cancel", CancelBooking);
        group.MapPost("/{bookingId:guid}/no-show", MarkParentNoShow);
        // What the provider has changed since the stay was booked — read this
        // before opening the edit screen so the app can confirm the new terms.
        group.MapGet("/{bookingId:guid}/terms-changes", GetTermsChanges);
        group.MapPost("/{bookingId:guid}/modifications", RequestModification);
        group.MapPost("/{bookingId:guid}/modifications/accept", AcceptModification);
        group.MapPost("/{bookingId:guid}/modifications/decline", DeclineModification);
        group.MapPost("/{bookingId:guid}/evidence", UploadEvidence).DisableAntiforgery();
        group.MapGet("/{bookingId:guid}/evidence", ListEvidence);
        // Geolocation capture — mirrors the single-day routes; see there.
        group.MapPost("/{bookingId:guid}/cash-not-received", RecordCashNotReceived);
        group.MapPost("/{bookingId:guid}/location", RecordLocation);

        return builder;
    }

    private static async Task<IResult> ListByProvider(
        Guid providerId,
        DateOnly? date,
        INightStayBookingService bookingService,
        IMySupportTicketLookup ticketLookup,
        IMyBlockLookup blockLookup,
        CancellationToken cancellationToken)
    {
        var results = await bookingService.ListByProviderAsync(providerId, date, cancellationToken);

        // One read for the whole page — the caller's own open tickets, keyed by subject.
        var myTickets = await MySupportTickets.ForProviderAsync(
            providerId, ticketLookup, cancellationToken);
        var myBlocks = await MyBlocks.ForProviderAsync(
            providerId, blockLookup, cancellationToken);

        return ApiResults.Ok(results
            .Select(r => ToResponse(
                r,
                myTickets.ForBooking(BookingTypes.NightStay, r.NightStayBookingId),
                myBlocks.ForCounterparty(r.PetParentId)))
            .ToArray());
    }

    private static async Task<IResult> GetBooking(
        Guid providerId,
        Guid bookingId,
        INightStayBookingService bookingService,
        IBookingReviewService reviewService,
        IMySupportTicketLookup ticketLookup,
        IMyBlockLookup blockLookup,
        CancellationToken cancellationToken)
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
                    mod.ProposedCheckInDate, mod.ProposedCheckOutDate, mod.Note, mod.CreatedAtUtc,
                    BookingEndpoints.ToAcknowledgedTermsResponse(mod.AcknowledgedTerms));
        }

        var myRating = await reviewService.GetAsync(
            ReviewedBookingTypes.NightStay, bookingId, ReviewerTypes.Provider, cancellationToken);

        // Route-scoped, and the booking was just confirmed to be this provider's, so the
        // route id IS the actor here.
        var myTickets = await MySupportTickets.ForProviderAsync(
            providerId, ticketLookup, cancellationToken);
        var myBlocks = await MyBlocks.ForProviderAsync(
            providerId, blockLookup, cancellationToken);

        return ApiResults.Ok(ToDetailResponse(
            detail,
            startOtp: null,
            pending,
            myRating,
            myTickets.ForBooking(BookingTypes.NightStay, bookingId),
            myBlocks.ForCounterparty(detail.Row.PetParentId)));
    }

    /// <summary>
    /// Maps the enriched night-stay detail into the sectioned response (the
    /// night-stay analog of the single-day booking-detail mapping).
    /// </summary>
    private static NightStayBookingDetailResponse ToDetailResponse(
        NightStayBookingDetailResult detail,
        StartOtpResponse? startOtp,
        NightStayBookingModificationResponse? pendingModification,
        BookingReviewRecord? myRating = null,
        MySupportTicketRef? myTicket = null,
        // Whether the booking's counterparty is blocked, and whose block it
        // is. Default false/false on the write paths, where it cannot be true.
        (bool IsBlocked, bool BlockedByMe) block = default)
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
                row.CancelledAtUtc,
                row.JobNotes),
            new ParentDetailsSection(
                row.PetParentId,
                CombineName(row.ParentFirstName, row.ParentLastName),
                row.ParentMobileCountryCode,
                row.ParentMobileNumber,
                row.ParentGender,
                row.ParentPhotoUrl,
                row.ParentAddressLine,
                row.ParentCity,
                row.ParentZipCode,
                row.ParentLatitude,
                row.ParentLongitude),
            new PetDetailsSection(
                row.PetId,
                row.PetProfileName,
                row.PetType,
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
            new NightStayPaymentDetailsSection(
                detail.PricePerNight,
                detail.TotalAmount,
                detail.PawfrontFee,
                detail.FeePercentage,
                row.PayoutStatus,
                row.PayoutId,
                row.PayoutMethod,
                row.PaidAtUtc),
            new CancellationPolicyDetailsSection(detail.MinimumHoursBeforeCancellation),
            BookingEndpoints.ToLocationSection(detail.Location),
            startOtp,
            pendingModification,
            // Night-stay is App-only, so there is no Custom walk-in case to exclude.
            ReviewResponseMapping.ToDetailsSection(row.Status, myRating),
            // Whether THIS caller already has an open incident on the stay.
            IsTicketRaisedByMe: myTicket is not null,
            TicketId: myTicket?.TicketId,
            TicketRef: myTicket?.TicketRef,
            IsBlocked: block.IsBlocked,
            BlockedByMe: block.BlockedByMe);
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

    private static Task<IResult> CancelBooking(Guid providerId, Guid bookingId, INightStayBookingService s, CancellationToken ct)
        => SetStatusAsync(providerId, bookingId, BookingStatuses.ProviderCancelled, s, ct);

    /// <summary>
    /// The provider reports that the parent (pet) never showed up for the stay.
    /// Only allowed 30+ minutes after check-in + drop-off (409 NoShowTooEarly).
    /// </summary>
    /// <remarks>Carries a body: the provider's position is required (400 LocationRequired).</remarks>
    private static Task<IResult> MarkParentNoShow(
        Guid providerId, Guid bookingId, BookingLocationOnlyRequest? request,
        INightStayBookingService s, CancellationToken ct)
        => SetStatusAsync(
            providerId, bookingId, BookingStatuses.ParentNoShow, s, ct,
            request?.Location.ToCapturedLocation());

    private static async Task<IResult> SetStatusAsync(
        Guid providerId, Guid bookingId, string status, INightStayBookingService bookingService,
        CancellationToken cancellationToken, CapturedLocation? location = null)
    {
        try
        {
            var result = await bookingService.UpdateStatusAsync(
                new UpdateNightStayBookingStatusCommand(
                    bookingId, status, BookingStatusActor.Provider, providerId, null, location),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    /// <summary>Provider taps "Start Job" for the stay → START_JOB + start-OTP (check-in-date + working-hours gates).</summary>
    private static async Task<IResult> StartJob(
        Guid providerId, Guid bookingId, StartJobRequest? request,
        INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.StartJobAsync(
                new StartBookingCommand(bookingId, providerId, request?.Location.ToCapturedLocation()),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    /// <summary>Provider enters the parent's start-code → START_JOB → IN_PROGRESS.</summary>
    private static async Task<IResult> VerifyStartOtp(
        Guid providerId, Guid bookingId, VerifyStartOtpRequest? request,
        INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.OtpCode))
        {
            return ApiResults.BadRequest(
                "InvalidRequest", "The parent's start code is required to begin the job.");
        }

        try
        {
            var result = await bookingService.VerifyStartOtpAsync(
                bookingId, providerId, request.OtpCode,
                request.Location.ToCapturedLocation(), cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    /// <summary>Provider completes the job → IN_PROGRESS → COMPLETED (no OTP, no body).</summary>
    private static async Task<IResult> CompleteBooking(
        Guid providerId, Guid bookingId,
        INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.CompleteAsync(bookingId, providerId, cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    /// <summary>
    /// The provider records that the parent has paid them for a COMPLETED stay
    /// (→ PAID). The amount is the stay's price-locked total; the body only carries
    /// the payment method (Cash / Digital).
    /// </summary>
    private static async Task<IResult> MarkPaid(
        Guid providerId, Guid bookingId, MarkBookingPaidRequest? request,
        INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PaymentMethod))
        {
            return ApiResults.BadRequest("InvalidRequest", "A payment method (Cash or Digital) is required.");
        }

        try
        {
            var result = await bookingService.MarkPaidAsync(
                new MarkBookingPaidCommand(
                    bookingId, providerId, request.PaymentMethod,
                    request.Location.ToCapturedLocation()),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    /// <summary>
    /// The provider's terms that changed since this stay was booked. Always 200 —
    /// an unchanged stay reports <c>hasChanges: false</c> with an empty list.
    /// </summary>
    private static async Task<IResult> GetTermsChanges(
        Guid providerId,
        Guid bookingId,
        INightStayBookingService bookingService,
        IBookingTermsChangeService termsChangeService,
        CancellationToken cancellationToken)
    {
        var booking = await bookingService.GetAsync(bookingId, cancellationToken);
        if (booking is null || booking.ProviderId != providerId)
        {
            return ApiResults.NotFound("NightStayBookingNotFound", $"Night stay booking '{bookingId}' was not found.");
        }

        var result = await termsChangeService.GetForNightStayBookingAsync(bookingId, cancellationToken);
        return ApiResults.Ok(BookingEndpoints.ToTermsChangesResponse(result));
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
                    bookingId, BookingStatusActor.Provider, providerId, request.CheckInDate, request.CheckOutDate,
                    request.Note, request.AcknowledgeTermsChanges),
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
        [FromForm] decimal? latitude,
        [FromForm] decimal? longitude,
        [FromForm] decimal? accuracyMetres,
        [FromForm] DateTimeOffset? capturedAtUtc,
        IPawfrontBlobStorage blobStorage,
        INightStayBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var validation = ValidatePhotoFile(file);
        if (validation is not null)
        {
            return validation;
        }

        // Validated before the upload so a caller with no fix is not charged one.
        var location = BookingLocationMapping.ToCapturedLocation(
            latitude, longitude, accuracyMetres, capturedAtUtc);
        try
        {
            CapturedLocation.Require(location, "attach job evidence");
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }

        await using var stream = file.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            BlobUploadKind.BookingEvidence, bookingId, file.FileName, stream, file.ContentType, cancellationToken);

        try
        {
            var result = await bookingService.AddEvidenceAsync(
                bookingId, providerId, url, location, cancellationToken);
            return ApiResults.Created($"/api/v1/providers/{providerId}/night-stay-bookings/{bookingId}/evidence/{result.BookingEvidenceId}",
                new BookingEvidenceResponse(result.BookingEvidenceId, result.BookingId, result.PhotoUrl, result.CreatedAtUtc));
        }
        catch (NightStayBookingNotFoundException exception)
        {
            return ApiResults.NotFound("NightStayBookingNotFound", exception.Message);
        }
    }

    /// <summary>
    /// The provider records that they were NOT paid for the stay, with their
    /// position. Log only — no status and no payout field changes; see the
    /// single-day route for why the money verdict is deliberately left open.
    /// </summary>
    private static Task<IResult> RecordCashNotReceived(
        Guid providerId, Guid bookingId, BookingLocationOnlyRequest? request,
        IBookingLocationService locationService, CancellationToken cancellationToken)
        => RecordLocationAsync(
            new RecordBookingLocationCommand(
                BookingTypes.NightStay, bookingId, BookingLocationTriggers.CashNotReceived,
                BookingStatusActor.Provider, providerId, request?.Location.ToCapturedLocation()),
            locationService, cancellationToken);

    /// <summary>The provider's own fix for a moment the PARENT drove.</summary>
    private static Task<IResult> RecordLocation(
        Guid providerId, Guid bookingId, RecordBookingLocationRequest? request,
        IBookingLocationService locationService, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Task.FromResult(ApiResults.BadRequest("InvalidRequest", "Request body is required."));
        }

        return RecordLocationAsync(
            new RecordBookingLocationCommand(
                BookingTypes.NightStay, bookingId, request.Trigger,
                BookingStatusActor.Provider, providerId, request.Location.ToCapturedLocation()),
            locationService, cancellationToken);
    }

    private static async Task<IResult> RecordLocationAsync(
        RecordBookingLocationCommand command,
        IBookingLocationService locationService,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await locationService.RecordAsync(command, cancellationToken);
            return ApiResults.Ok(BookingLocationMapping.ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
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
        or BookingStartOutsideWorkingHoursException or BookingStartNotOnServiceDateException
        or BookingJobInProgressException
        or InvalidStartOtpException or StartOtpExpiredException or OtpAttemptsExceededException
        or BookingNotCompletableException or BookingNotModifiableException
        or BookingModificationConflictException or NoPendingModificationException
        or BookingModificationCapacityException or BookingTermsChangedException
        or BookingModificationWindowClosedException or BookingModificationExpiredException
        or InvalidNightStayDatesException
        or ProviderClosedOnDateException or BookingOfferingNotConfiguredException
        or BookingStatusNotAllowedException or BookingStatusTerminalException
        or BookingStatusUnchangedException or BookingNoShowTooEarlyException or BookingExpiredException
        or BookingNotPayableException or BookingAlreadyPaidException or BookingNotPriceableException
        or UnsupportedBookingPaymentMethodException
        or MissingCapturedLocationException or InvalidCapturedLocationException
        or UnsupportedBookingLocationTriggerException
        or UnsupportedBookingStatusException or ArgumentException;

    private static IResult MapBookingError(Exception ex) => ex switch
    {
        NightStayBookingNotFoundException e => ApiResults.NotFound("NightStayBookingNotFound", e.Message),
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
        InvalidNightStayDatesException e => ApiResults.BadRequest("InvalidNightStayDates", e.Message),
        ProviderClosedOnDateException e => ApiResults.Conflict("ServiceClosed", e.Message),
        BookingOfferingNotConfiguredException e => ApiResults.BadRequest("OfferingNotConfigured", e.Message),
        BookingStatusNotAllowedException e => ApiResults.BadRequest("BookingStatusNotAllowed", e.Message),
        BookingStatusTerminalException e => ApiResults.Conflict("BookingStatusTerminal", e.Message),
        BookingStatusUnchangedException e => ApiResults.Conflict("BookingStatusUnchanged", e.Message),
        BookingNoShowTooEarlyException e => ApiResults.Conflict("NoShowTooEarly", e.Message),
        BookingExpiredException e => ApiResults.Conflict("BookingExpired", e.Message),
        BookingNotPayableException e => ApiResults.Conflict("BookingNotPayable", e.Message),
        BookingAlreadyPaidException e => ApiResults.Conflict("BookingAlreadyPaid", e.Message),
        BookingNotPriceableException e => ApiResults.Conflict("BookingNotPriceable", e.Message),
        UnsupportedBookingPaymentMethodException e => ApiResults.BadRequest("UnsupportedPaymentMethod", e.Message),
        UnsupportedBookingStatusException e => ApiResults.BadRequest("UnsupportedBookingStatus", e.Message),
        // Distinct codes on purpose: "you sent no location" and "your location is
        // nonsense" call for different things from the app — prompting for the
        // permission versus retrying the fix.
        MissingCapturedLocationException e => ApiResults.BadRequest("LocationRequired", e.Message),
        InvalidCapturedLocationException e => ApiResults.BadRequest("InvalidLocation", e.Message),
        UnsupportedBookingLocationTriggerException e => ApiResults.BadRequest("UnsupportedLocationTrigger", e.Message),
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

    /// <param name="myTicket">
    /// The caller's own open support ticket on this stay, when the read path resolved one.
    /// Null on the write paths, where it is false by construction.
    /// </param>
    private static NightStayBookingResponse ToResponse(
        NightStayBookingResult result,
        MySupportTicketRef? myTicket = null,
        // Whether the booking's counterparty is blocked, and whose block it
        // is. Default false/false on the write paths, where it cannot be true.
        (bool IsBlocked, bool BlockedByMe) block = default) =>
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
            result.PetId,
            IsTicketRaisedByMe: myTicket is not null,
            TicketId: myTicket?.TicketId,
            TicketRef: myTicket?.TicketRef,
            IsBlocked: block.IsBlocked,
            BlockedByMe: block.BlockedByMe);
}
