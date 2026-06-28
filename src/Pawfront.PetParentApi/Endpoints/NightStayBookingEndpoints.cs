using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.ProviderServices;
using Pawfront.Contracts.Bookings;
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
                    request.PetId),
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
        catch (ProviderClosedOnDateException exception)
        {
            return ApiResults.Conflict("ServiceClosed", exception.Message);
        }
        catch (InvalidNightStayDatesException exception)
        {
            return ApiResults.BadRequest("InvalidNightStayDates", exception.Message);
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
        CancellationToken cancellationToken)
    {
        var results = await bookingService.ListByPetParentAsync(petParentId, cancellationToken);
        return ApiResults.Ok(results.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> GetBooking(
        Guid petParentId,
        Guid bookingId,
        INightStayBookingService bookingService,
        CancellationToken cancellationToken)
    {
        // The group is ownership-filtered on petParentId, but bookingId is not —
        // confirm the booking belongs to this parent (404 rather than leaking).
        var result = await bookingService.GetAsync(bookingId, cancellationToken);
        if (result is null || result.PetParentId != petParentId)
        {
            return ApiResults.NotFound("NightStayBookingNotFound", $"Night stay booking '{bookingId}' was not found.");
        }

        // Issue/return the start-OTP when the stay is in a startable state, so the
        // parent can read it to the provider (same model as single-day bookings).
        StartOtpResponse? startOtp = null;
        if (BookingStatuses.ConfirmedEquivalent.Contains(result.Status))
        {
            var otp = await bookingService.IssueStartOtpAsync(bookingId, cancellationToken);
            startOtp = new StartOtpResponse(otp.OtpCode, otp.ExpiresAtUtc);
        }

        var pending = await ToPendingAsync(bookingService, bookingId, result.Status, cancellationToken);

        return ApiResults.Ok(new NightStayBookingDetailResponse(ToResponse(result), startOtp, pending));
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
        catch (UnsupportedBookingStatusException exception)
        {
            return ApiResults.BadRequest("UnsupportedBookingStatus", exception.Message);
        }
        catch (NightStayBookingNotFoundException exception)
        {
            return ApiResults.NotFound("NightStayBookingNotFound", exception.Message);
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
