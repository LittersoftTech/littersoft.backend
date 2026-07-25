using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.ProviderServices;
using Pawfront.Contracts.Bookings;
using Pawfront.Domain.Services;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

/// <summary>
/// Parent-host endpoints for multi-night boarding bookings (PetSitter NightStay).
/// Separate from the single-day service bookings in <see cref="PetParentEndpoints"/>
/// because a night stay is keyed by a check-in / check-out date range, not a
/// single-day time window. All routes are ownership-filtered: the booker is the
/// route's petParentId (JWT-verified), never the body. The pet parent is the only
/// booking party today — provider-host management endpoints aren't wired yet.
/// </summary>
internal static class NightStayBookingEndpoints
{
    public static IEndpointRouteBuilder MapNightStayBookingEndpoints(this IEndpointRouteBuilder builder)
    {
        var group = builder
            .MapGroup("/pet-parents/{petParentId:guid}/night-stay-bookings")
            .RequireOwnedPetParent();

        group.MapPost("/", CreateBooking);
        group.MapGet("/", ListByPetParent);
        // Single read — issues the start-OTP into the response when startable.
        group.MapGet("/{bookingId:guid}", GetBooking);
        // Legacy generic status setter — kept as a back-compat shim.
        group.MapPost("/{bookingId:guid}/status", UpdateStatus);
        group.MapGet("/{bookingId:guid}/status-history", GetStatusHistory);
        // Per-transition endpoints (parent actor).
        group.MapPost("/{bookingId:guid}/cancel", ParentCancel);
        group.MapPost("/{bookingId:guid}/no-show", ReportProviderNoShow);
        group.MapPost("/{bookingId:guid}/modifications", RequestModification);
        group.MapPost("/{bookingId:guid}/modifications/accept", AcceptModification);
        group.MapPost("/{bookingId:guid}/modifications/decline", DeclineModification);
        group.MapGet("/{bookingId:guid}/evidence", ListEvidence);

        return builder;
    }

    /// <summary>
    /// Parent-initiated night-stay booking ("book now" from a night-stay search
    /// result). The booker is the route's petParentId (JWT-verified by the group
    /// filter, never the body). The provider is resolved server-side from the
    /// booked ServiceId; the pet must belong to the caller. The sproc re-checks
    /// both as defense-in-depth.
    /// </summary>
    private static async Task<IResult> CreateBooking(
        Guid petParentId,
        CreateParentNightStayBookingRequest request,
        INightStayBookingService bookingService,
        IProviderServiceCatalog serviceCatalog,
        IPetParentOwnershipReader ownershipReader,
        CancellationToken cancellationToken)
    {
        // The parent must say where the service happens — the detail read
        // resolves the matching address from this choice.
        if (string.IsNullOrWhiteSpace(request.LocationType))
        {
            return ApiResults.BadRequest(
                "InvalidRequest",
                "locationType is required. Use 'ParentLocation' or 'ProviderLocation'.");
        }

        var pet = await ownershipReader.GetPetLookupAsync(request.PetId, cancellationToken);
        if (pet is null)
        {
            return ApiResults.NotFound("PetNotFound", $"Pet '{request.PetId}' was not found.");
        }
        if (pet.OwningPetParentId != petParentId)
        {
            return ApiResults.Forbidden(
                "Forbidden",
                "You can only book for pets belonging to your own profile.");
        }

        var service = await serviceCatalog.GetByIdAsync(request.ServiceId, cancellationToken);
        if (service is null)
        {
            return ApiResults.BadRequest(
                "InvalidServiceId",
                $"Service '{request.ServiceId}' was not found.");
        }

        try
        {
            var result = await bookingService.CreateAsync(
                new CreateNightStayBookingCommand(
                    service.ProviderId,
                    petParentId,
                    request.ServiceId,
                    request.CheckInDate,
                    request.CheckOutDate,
                    request.PetId,
                    request.JobNotes,
                    request.LocationType),
                cancellationToken);

            return ApiResults.Created(
                $"/api/v1/pet-parents/{petParentId}/night-stay-bookings/{result.NightStayBookingId}",
                ToResponse(result));
        }
        catch (BookingServiceInvalidException exception)
        {
            return ApiResults.BadRequest("InvalidServiceId", exception.Message);
        }
        catch (BookingNotNightStayServiceException exception)
        {
            return ApiResults.BadRequest("NotNightStayService", exception.Message);
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
        catch (BookingPetParentNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }
        catch (BookingPetInvalidException exception)
        {
            return ApiResults.BadRequest("InvalidPetId", exception.Message);
        }
        catch (NightStayCapacityExceededException exception)
        {
            return ApiResults.Conflict("CapacityExceeded", exception.Message);
        }
        catch (NightStayPetAlreadyBookedException exception)
        {
            return ApiResults.Conflict("PetAlreadyBooked", exception.Message);
        }
        catch (ProviderClosedOnDateException exception)
        {
            return ApiResults.Conflict("ServiceClosed", exception.Message);
        }
        catch (InvalidNightStayDatesException exception)
        {
            return ApiResults.BadRequest("InvalidNightStayDates", exception.Message);
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

    /// <summary>
    /// The parent's own night-stay bookings ("my bookings"), most-recent first,
    /// cancelled included. Ownership-filtered, so a caller only sees their own.
    /// </summary>
    private static async Task<IResult> ListByPetParent(
        Guid petParentId,
        INightStayBookingService bookingService,
        IParentBookingEnrichmentService enrichment,
        CancellationToken cancellationToken)
    {
        var results = await bookingService.ListByPetParentAsync(petParentId, cancellationToken);
        var enriched = await enrichment.EnrichNightStayAsync(results, cancellationToken);
        return ApiResults.Ok(enriched.Select(ToNightStayBookingCard).ToArray());
    }

    /// <summary>
    /// Maps an enriched night-stay booking into the sectioned "my bookings" card —
    /// the booking, the booked provider's details, the service details + the
    /// per-night price, and the frozen-at-creation cancellation-policy +
    /// selected-location blocks.
    /// </summary>
    private static ParentNightStayBookingCardResponse ToNightStayBookingCard(EnrichedNightStayBookingCard card)
    {
        var b = card.Booking;
        return new ParentNightStayBookingCardResponse(
            ToResponse(b),
            new BookingProviderDetailsSection(
                b.ProviderId,
                card.Provider?.DisplayName,
                card.Provider?.ImageUrl,
                card.Provider?.City,
                b.ServiceCategory,
                b.SubCategory),
            new NightStayServiceDetailsSection(
                b.ServiceId,
                ProviderServiceTypes.NightStay,
                card.PricePerNight),
            new CancellationPolicyDetailsSection(card.CancellationPolicyHours),
            new BookingLocationDetailsSection(
                card.Location.LocationType,
                card.Location.AddressLine,
                card.Location.City,
                card.Location.ZipCode,
                card.Location.Latitude,
                card.Location.Longitude));
    }

    private static async Task<IResult> GetBooking(
        Guid petParentId,
        Guid bookingId,
        INightStayBookingService bookingService,
        CancellationToken cancellationToken)
    {
        // The group is ownership-filtered on petParentId, but bookingId is not —
        // confirm the booking belongs to this parent (404 rather than leaking).
        var detail = await bookingService.GetDetailAsync(bookingId, cancellationToken);
        if (detail is null || detail.Row.PetParentId != petParentId)
        {
            return ApiResults.NotFound("NightStayBookingNotFound", $"Night stay booking '{bookingId}' was not found.");
        }

        // Issue/return the start-OTP only while the stay is START_JOB. The parent
        // reads it to the provider, who enters it to move the job to IN_PROGRESS
        // (same model as single-day).
        StartOtpResponse? startOtp = null;
        if (detail.Row.Status == BookingStatuses.StartJob)
        {
            var otp = await bookingService.IssueStartOtpAsync(bookingId, cancellationToken);
            startOtp = new StartOtpResponse(otp.OtpCode, otp.ExpiresAtUtc);
        }

        var pending = await ToPendingAsync(bookingService, bookingId, detail.Row.Status, cancellationToken);

        return ApiResults.Ok(ToDetailResponse(detail, startOtp, pending));
    }

    /// <summary>
    /// Maps the enriched night-stay detail into the sectioned response — the
    /// night-stay analog of the single-day booking-detail mapping. Parent/Pet come
    /// from the joined records (night-stay is App-only).
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
                ProviderPhotoUrl: null,
                detail.ProviderAddress,
                detail.ProviderCity,
                detail.ProviderZip),
            new NightStayPaymentDetailsSection(
                detail.PricePerNight,
                detail.TotalAmount,
                detail.PawfrontFee,
                detail.FeePercentage,
                row.PayoutStatus,
                row.PayoutId),
            new CancellationPolicyDetailsSection(detail.MinimumHoursBeforeCancellation),
            PetParentEndpoints.ToLocationSection(detail.Location),
            startOtp,
            pendingModification);
    }

    private static string? CombineName(string? first, string? last)
    {
        var name = $"{first} {last}".Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static async Task<NightStayBookingModificationResponse?> ToPendingAsync(
        INightStayBookingService bookingService, Guid bookingId, string status, CancellationToken cancellationToken)
    {
        if (!BookingStatuses.ModificationRequested.Contains(status))
        {
            return null;
        }

        var mod = await bookingService.GetPendingModificationAsync(bookingId, cancellationToken);
        return mod is null
            ? null
            : new NightStayBookingModificationResponse(
                mod.NightStayBookingModificationId, mod.NightStayBookingId, mod.RequestedByActor, mod.RequestedByActorId,
                mod.ProposedCheckInDate, mod.ProposedCheckOutDate, mod.Note, mod.CreatedAtUtc);
    }

    private static async Task<IResult> ParentCancel(
        Guid petParentId, Guid bookingId, INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.UpdateStatusAsync(
                new UpdateNightStayBookingStatusCommand(bookingId, BookingStatuses.ParentCancelled, BookingStatusActor.Parent, petParentId, null),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    /// <summary>
    /// The parent reports that the provider never showed up for the stay. Only
    /// allowed 30+ minutes after check-in + drop-off (409 NoShowTooEarly).
    /// </summary>
    private static async Task<IResult> ReportProviderNoShow(
        Guid petParentId, Guid bookingId, INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.UpdateStatusAsync(
                new UpdateNightStayBookingStatusCommand(bookingId, BookingStatuses.ProviderNoShow, BookingStatusActor.Parent, petParentId, null),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    private static async Task<IResult> RequestModification(
        Guid petParentId, Guid bookingId, RequestNightStayBookingModificationRequest request,
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
                    bookingId, BookingStatusActor.Parent, petParentId, request.CheckInDate, request.CheckOutDate, request.Note),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    private static Task<IResult> AcceptModification(
        Guid petParentId, Guid bookingId, RespondBookingModificationRequest? request, INightStayBookingService s, CancellationToken ct)
        => RespondModificationAsync(petParentId, bookingId, accept: true, request?.Note, s, ct);

    private static Task<IResult> DeclineModification(
        Guid petParentId, Guid bookingId, RespondBookingModificationRequest? request, INightStayBookingService s, CancellationToken ct)
        => RespondModificationAsync(petParentId, bookingId, accept: false, request?.Note, s, ct);

    private static async Task<IResult> RespondModificationAsync(
        Guid petParentId, Guid bookingId, bool accept, string? note, INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.RespondModificationAsync(
                new RespondBookingModificationCommand(bookingId, BookingStatusActor.Parent, petParentId, accept, note),
                cancellationToken);
            return ApiResults.Ok(ToResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    private static async Task<IResult> ListEvidence(
        Guid petParentId, Guid bookingId, INightStayBookingService bookingService, CancellationToken cancellationToken)
    {
        var booking = await bookingService.GetAsync(bookingId, cancellationToken);
        if (booking is null || booking.PetParentId != petParentId)
        {
            return ApiResults.NotFound("NightStayBookingNotFound", $"Night stay booking '{bookingId}' was not found.");
        }

        var evidence = await bookingService.ListEvidenceAsync(bookingId, cancellationToken);
        return ApiResults.Ok(evidence
            .Select(e => new BookingEvidenceResponse(e.BookingEvidenceId, e.BookingId, e.PhotoUrl, e.CreatedAtUtc))
            .ToArray());
    }

    private static bool IsBookingError(Exception ex) => ex is
        NightStayBookingNotFoundException or BookingStatusForbiddenException or BookingNotStartableException
        or BookingJobInProgressException
        or InvalidStartOtpException or StartOtpExpiredException or BookingNotModifiableException
        or BookingModificationConflictException or NoPendingModificationException
        or BookingModificationCapacityException or InvalidNightStayDatesException
        or ProviderClosedOnDateException or BookingOfferingNotConfiguredException
        or BookingStatusNotAllowedException or BookingStatusTerminalException
        or BookingStatusUnchangedException or BookingNoShowTooEarlyException or BookingExpiredException
        or UnsupportedBookingStatusException or ArgumentException;

    private static IResult MapBookingError(Exception ex) => ex switch
    {
        NightStayBookingNotFoundException e => ApiResults.NotFound("NightStayBookingNotFound", e.Message),
        BookingStatusForbiddenException e => ApiResults.Forbidden("Forbidden", e.Message),
        BookingNotStartableException e => ApiResults.Conflict("BookingNotStartable", e.Message),
        BookingJobInProgressException e => ApiResults.Conflict("BookingInProgress", e.Message),
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
        BookingNoShowTooEarlyException e => ApiResults.Conflict("NoShowTooEarly", e.Message),
        BookingExpiredException e => ApiResults.Conflict("BookingExpired", e.Message),
        UnsupportedBookingStatusException e => ApiResults.BadRequest("UnsupportedBookingStatus", e.Message),
        _ => ApiResults.BadRequest("InvalidRequest", ex.Message)
    };

    /// <summary>
    /// Parent moves their booking to a new lifecycle status (APPROVAL_NEEDED |
    /// COMPLETED | PARENT_CANCELLED — cancel is done here, mirroring the
    /// single-day parent flow). Actor = Parent, actorId = route petParentId.
    /// </summary>
    private static async Task<IResult> UpdateStatus(
        Guid petParentId,
        Guid bookingId,
        UpdateBookingStatusRequest request,
        INightStayBookingService bookingService,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Status))
        {
            return ApiResults.BadRequest("InvalidRequest", "A status is required.");
        }

        try
        {
            var result = await bookingService.UpdateStatusAsync(
                new UpdateNightStayBookingStatusCommand(
                    bookingId,
                    request.Status,
                    BookingStatusActor.Parent,
                    petParentId,
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
        Guid petParentId,
        Guid bookingId,
        INightStayBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var booking = await bookingService.GetAsync(bookingId, cancellationToken);
        if (booking is null || booking.PetParentId != petParentId)
        {
            return ApiResults.NotFound("NightStayBookingNotFound", $"Night stay booking '{bookingId}' was not found.");
        }

        var history = await bookingService.ListStatusHistoryAsync(bookingId, cancellationToken);
        return ApiResults.Ok(history.Select(ToHistoryResponse).ToArray());
    }

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
