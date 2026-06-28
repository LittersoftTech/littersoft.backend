using System.Security.Claims;
using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Events;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.ParentPets;
using Pawfront.Application.ParentPhotos;
using Pawfront.Application.ProviderServices;
using Pawfront.Application.Storage;
using Pawfront.Contracts.Bookings;
using Pawfront.Contracts.Events;
using Pawfront.Contracts.ParentOnboarding;
using Pawfront.Contracts.ParentPets;
using Pawfront.Contracts.ParentPhotos;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

internal static class PetParentEndpoints
{
    // Hard upper bound on uploaded image size. Mirrors the user-specified
    // "max 3 MB" constraint. Applied to both pet-parent profile photos and
    // individual pet photos — both endpoints share the same validation.
    private const long MaxPhotoBytes = 3L * 1024 * 1024;

    // Allowlist of MIME types the mobile client can send. JPEG / PNG / WebP
    // covers the standard iOS + Android capture/picker formats. HEIC can be
    // added when iOS clients start delivering it natively.
    private static readonly HashSet<string> AllowedPhotoContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/jpg",
        "image/png",
        "image/webp"
    };

    public static IEndpointRouteBuilder MapPetParentEndpoints(this IEndpointRouteBuilder builder)
    {
        // Ownership: the {petParentId:guid} route segment is checked against
        // the caller's resolved PetParentId before every handler in this
        // group runs. Mismatch → 403; missing profile → 403
        // ParentProfileNotCompleted.
        var group = builder.MapGroup("/pet-parents/{petParentId:guid}").RequireOwnedPetParent();
        group.MapGet("/profile", GetProfile);
        group.MapPatch("/profile", UpdateProfile);
        group.MapPost("/profile-image", UploadProfilePhoto).DisableAntiforgery();
        group.MapPost("/pets", AddPet);
        group.MapGet("/pets", ListPets);
        group.MapPost("/identity", UploadIdentity).DisableAntiforgery();
        group.MapGet("/identity", GetIdentity);
        group.MapDelete("/identity", DeleteIdentity);
        group.MapPost("/photos", UploadPhoto).DisableAntiforgery();
        group.MapGet("/photos", ListPhotos);
        group.MapDelete("/photos/{photoId:guid}", DeletePhoto);
        group.MapGet("/event-bookings", ListEventBookings);
        group.MapPost("/bookings", CreateServiceBooking);
        group.MapGet("/bookings", ListServiceBookings);
        // Single booking read — issues the start-OTP into the response when the
        // booking is in a startable (confirmed-equivalent) state.
        group.MapGet("/bookings/{bookingId:guid}", GetServiceBookingDetail);
        // Legacy generic status setter — kept as a back-compat shim.
        group.MapPost("/bookings/{bookingId:guid}/status", UpdateBookingStatus);
        group.MapGet("/bookings/{bookingId:guid}/status-history", GetBookingStatusHistory);
        // Per-transition endpoints (parent actor).
        group.MapPost("/bookings/{bookingId:guid}/cancel", ParentCancelServiceBooking);
        group.MapPost("/bookings/{bookingId:guid}/modifications", RequestServiceBookingModification);
        group.MapPost("/bookings/{bookingId:guid}/modifications/accept", AcceptServiceBookingModification);
        group.MapPost("/bookings/{bookingId:guid}/modifications/decline", DeclineServiceBookingModification);
        group.MapGet("/bookings/{bookingId:guid}/evidence", ListServiceBookingEvidence);
        group.MapGet("/onboarding-status", GetOnboardingStatus);

        var mobileVerification = group.MapGroup("/mobile-verification");
        mobileVerification.MapPost("/otp", SendMobileOtp);
        mobileVerification.MapPost("/otp/{parentMobileOtpId:guid}/verify", VerifyMobileOtp);

        // Ownership: every pet-scoped route confirms the {petId:guid} belongs
        // to the caller's resolved PetParentId. Unknown pet → 404; wrong
        // owner → 403.
        var pets = builder.MapGroup("/pets/{petId:guid}").RequireOwnedPet();
        pets.MapGet("/", GetPet);
        pets.MapPatch("/", UpdatePet);
        pets.MapPatch("/medical-info", UpdatePetMedicalInfo);
        pets.MapDelete("/", DeletePet);
        pets.MapPost("/profile-image", UploadPetProfilePhoto).DisableAntiforgery();
        pets.MapPost("/photos", UploadPetPhoto).DisableAntiforgery();
        pets.MapDelete("/photos/{photoId:guid}", DeletePetPhoto);

        return builder;
    }

    private static async Task<IResult> ListEventBookings(
        Guid petParentId,
        HttpContext httpContext,
        IEventBookingService bookingService,
        CancellationToken cancellationToken)
    {
        // Booker identity on Event.EventBookings is free text (no FK to
        // PetParents) — we match on the caller's Firebase email rather than
        // a parent id. The ownership filter on this group already confirmed
        // the route's petParentId belongs to the caller, so the email comes
        // from the JWT and the parent can only see their own bookings.
        var email = httpContext.User.FindFirstValue("email");
        if (string.IsNullOrWhiteSpace(email))
        {
            // Firebase tokens normally carry email, but some sign-in methods
            // omit it. Without one we can't filter the bookings table.
            return ApiResults.Forbidden(
                "EmailClaimMissing",
                "The Firebase token does not carry an email claim, so the booking list cannot be resolved.");
        }

        var summaries = await bookingService.ListByBookerEmailAsync(email, cancellationToken);
        return ApiResults.Ok(summaries.Select(ToSummaryResponse).ToArray());
    }

    private static EventBookingSummaryResponse ToSummaryResponse(EventBookingSummary summary) =>
        new(
            summary.BookingId,
            summary.EventId,
            summary.EventTitle,
            summary.EventCategory,
            summary.EventType,
            summary.EventStartDate,
            summary.EventStartTime,
            summary.EventBannerImageUrl,
            summary.EventLocation is null
                ? null
                : new EventLocationResponse(
                    summary.EventLocation.HouseNumber,
                    summary.EventLocation.Street,
                    summary.EventLocation.City,
                    summary.EventLocation.Zip,
                    summary.EventLocation.Country,
                    summary.EventLocation.Latitude,
                    summary.EventLocation.Longitude),
            summary.BookerName,
            summary.BookerEmail,
            summary.BookerMobile,
            summary.TicketCount,
            summary.PaymentMethod,
            summary.PaymentStatus,
            summary.PaymentReference,
            summary.TotalAmount,
            summary.Status,
            summary.CreatedAtUtc,
            summary.UpdatedAtUtc,
            summary.CancelledAtUtc);

    /// <summary>
    /// The parent's own service bookings ("my bookings"), most-recent first,
    /// including cancelled ones. The petParentId is JWT-verified by the group's
    /// ownership filter, so a caller only ever sees their own bookings. Empty
    /// array when the parent has none — list semantics, no 404.
    /// </summary>
    private static async Task<IResult> ListServiceBookings(
        Guid petParentId,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        var results = await bookingService.ListByPetParentAsync(petParentId, cancellationToken);
        return ApiResults.Ok(results.Select(ToBookingResponse).ToArray());
    }

    /// <summary>
    /// Parent-initiated service booking ("book now" from a search result /
    /// slot). The booker is the route's petParentId — JWT-verified by the
    /// group's ownership filter, never taken from the body. The provider is
    /// resolved server-side from the booked ServiceId. The pet must belong
    /// to the caller; the sproc re-checks both as defense-in-depth.
    /// </summary>
    private static async Task<IResult> CreateServiceBooking(
        Guid petParentId,
        CreateParentBookingRequest request,
        IBookingService bookingService,
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
                new CreateBookingCommand(
                    service.ProviderId,
                    petParentId,
                    request.ServiceId,
                    request.BookingDate,
                    request.StartTime,
                    request.EndTime,
                    request.ServiceItemCode,
                    request.PetId),
                cancellationToken);
            return ApiResults.Ok(ToBookingResponse(result));
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
        catch (BookingPetInvalidException exception)
        {
            return ApiResults.BadRequest("InvalidPetId", exception.Message);
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

    private static async Task<IResult> UpdateBookingStatus(
        Guid petParentId,
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
                    BookingStatusActor.Parent,
                    petParentId,
                    request.Note),
                cancellationToken);
            return ApiResults.Ok(ToBookingResponse(result));
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

    private static async Task<IResult> GetBookingStatusHistory(
        Guid petParentId,
        Guid bookingId,
        IBookingService bookingService,
        CancellationToken cancellationToken)
    {
        // The group is ownership-filtered on petParentId, but the bookingId is
        // not — confirm the booking belongs to this parent before returning its
        // audit trail (404 rather than leaking existence).
        var booking = await bookingService.GetAsync(bookingId, cancellationToken);
        if (booking is null || booking.PetParentId != petParentId)
        {
            return ApiResults.NotFound("BookingNotFound", $"Booking '{bookingId}' was not found.");
        }

        var history = await bookingService.ListStatusHistoryAsync(bookingId, cancellationToken);
        return ApiResults.Ok(history.Select(ToBookingStatusHistoryResponse).ToArray());
    }

    /// <summary>
    /// Single booking read. Confirms the booking belongs to the caller (404
    /// otherwise) and, when the booking is in a startable (confirmed-equivalent)
    /// state, issues/returns the start-OTP for the parent to read to the provider.
    /// </summary>
    private static async Task<IResult> GetServiceBookingDetail(
        Guid petParentId, Guid bookingId, IBookingService bookingService, CancellationToken cancellationToken)
    {
        var detail = await bookingService.GetDetailAsync(bookingId, cancellationToken);
        if (detail is null || detail.Row.PetParentId != petParentId)
        {
            return ApiResults.NotFound("BookingNotFound", $"Booking '{bookingId}' was not found.");
        }

        StartOtpResponse? startOtp = null;
        if (BookingStatuses.ConfirmedEquivalent.Contains(detail.Row.Status))
        {
            var otp = await bookingService.IssueStartOtpAsync(bookingId, cancellationToken);
            startOtp = new StartOtpResponse(otp.OtpCode, otp.ExpiresAtUtc);
        }

        BookingModificationResponse? pending = null;
        if (BookingStatuses.ModificationRequested.Contains(detail.Row.Status))
        {
            var mod = await bookingService.GetPendingModificationAsync(bookingId, cancellationToken);
            pending = mod is null
                ? null
                : new BookingModificationResponse(
                    mod.BookingModificationId, mod.BookingId, mod.RequestedByActor, mod.RequestedByActorId,
                    mod.ProposedBookingDate, mod.ProposedStartTime, mod.ProposedEndTime, mod.Note, mod.CreatedAtUtc);
        }

        return ApiResults.Ok(ToBookingDetailResponse(detail, startOtp, pending));
    }

    private static Task<IResult> ParentCancelServiceBooking(
        Guid petParentId, Guid bookingId, IBookingService s, CancellationToken ct)
        => SetParentBookingStatusAsync(petParentId, bookingId, BookingStatuses.ParentCancelled, s, ct);

    private static async Task<IResult> SetParentBookingStatusAsync(
        Guid petParentId, Guid bookingId, string status, IBookingService bookingService, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.UpdateStatusAsync(
                new UpdateBookingStatusCommand(bookingId, status, BookingStatusActor.Parent, petParentId, null),
                cancellationToken);
            return ApiResults.Ok(ToBookingResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    private static async Task<IResult> RequestServiceBookingModification(
        Guid petParentId, Guid bookingId, RequestBookingModificationRequest request,
        IBookingService bookingService, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ApiResults.BadRequest("InvalidRequest", "Request body is required.");
        }

        try
        {
            var result = await bookingService.RequestModificationAsync(
                new RequestBookingModificationCommand(
                    bookingId, BookingStatusActor.Parent, petParentId,
                    request.BookingDate, request.StartTime, request.EndTime, request.Note),
                cancellationToken);
            return ApiResults.Ok(ToBookingResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    private static Task<IResult> AcceptServiceBookingModification(
        Guid petParentId, Guid bookingId, RespondBookingModificationRequest? request, IBookingService s, CancellationToken ct)
        => RespondParentBookingModificationAsync(petParentId, bookingId, accept: true, request?.Note, s, ct);

    private static Task<IResult> DeclineServiceBookingModification(
        Guid petParentId, Guid bookingId, RespondBookingModificationRequest? request, IBookingService s, CancellationToken ct)
        => RespondParentBookingModificationAsync(petParentId, bookingId, accept: false, request?.Note, s, ct);

    private static async Task<IResult> RespondParentBookingModificationAsync(
        Guid petParentId, Guid bookingId, bool accept, string? note, IBookingService bookingService, CancellationToken cancellationToken)
    {
        try
        {
            var result = await bookingService.RespondModificationAsync(
                new RespondBookingModificationCommand(bookingId, BookingStatusActor.Parent, petParentId, accept, note),
                cancellationToken);
            return ApiResults.Ok(ToBookingResponse(result));
        }
        catch (Exception ex) when (IsBookingError(ex))
        {
            return MapBookingError(ex);
        }
    }

    private static async Task<IResult> ListServiceBookingEvidence(
        Guid petParentId, Guid bookingId, IBookingService bookingService, CancellationToken cancellationToken)
    {
        var booking = await bookingService.GetAsync(bookingId, cancellationToken);
        if (booking is null || booking.PetParentId != petParentId)
        {
            return ApiResults.NotFound("BookingNotFound", $"Booking '{bookingId}' was not found.");
        }

        var evidence = await bookingService.ListEvidenceAsync(bookingId, cancellationToken);
        return ApiResults.Ok(evidence
            .Select(e => new BookingEvidenceResponse(e.BookingEvidenceId, e.BookingId, e.PhotoUrl, e.CreatedAtUtc))
            .ToArray());
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

    private static BookingStatusHistoryEntryResponse ToBookingStatusHistoryResponse(BookingStatusHistoryEntry entry) =>
        new(entry.BookingStatusHistoryId,
            entry.BookingId,
            entry.FromStatus,
            entry.ToStatus,
            entry.ChangedByActor,
            entry.ChangedByActorId,
            entry.Note,
            entry.ChangedAtUtc);

    private static BookingResponse ToBookingResponse(BookingResult result) =>
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
                row.ServiceLocation,
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
            startOtp,
            pendingModification);
    }

    private static string? CombineName(string? first, string? last)
    {
        var name = $"{first} {last}".Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static async Task<IResult> GetProfile(
        Guid petParentId,
        IParentOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await onboardingService.GetProfileAsync(petParentId, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (PetParentNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }
    }

    private static async Task<IResult> UpdateProfile(
        Guid petParentId,
        UpdatePetParentProfileRequest request,
        IParentOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await onboardingService.UpdateProfileAsync(petParentId, request, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (PetParentNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }
        catch (UnsupportedPetParentGenderException exception)
        {
            return ApiResults.BadRequest("UnsupportedGender", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> SendMobileOtp(
        Guid petParentId,
        IParentOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await onboardingService.SendMobileOtpAsync(petParentId, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (PetParentNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }
    }

    private static async Task<IResult> VerifyMobileOtp(
        Guid petParentId,
        Guid parentMobileOtpId,
        VerifyPetParentMobileOtpRequest request,
        IParentOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await onboardingService.VerifyMobileOtpAsync(
                petParentId,
                parentMobileOtpId,
                request,
                cancellationToken);

            return ApiResults.Ok(response);
        }
        catch (ParentMobileOtpNotFoundException exception)
        {
            return ApiResults.NotFound("ParentMobileOtpNotFound", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> GetOnboardingStatus(
        Guid petParentId,
        IPetParentOnboardingStatusService statusService,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await statusService.GetAsync(petParentId, cancellationToken);
            return ApiResults.Ok(ToResponse(status));
        }
        catch (PetParentOnboardingStatusNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }
    }

    private static PetParentOnboardingStatusResponse ToResponse(PetParentOnboardingStatus status)
    {
        return new PetParentOnboardingStatusResponse(
            status.PetParentId,
            new OnboardingStageResponse(status.BasicInfo.Status),
            new OnboardingStageResponse(status.ProfilePhoto.Status),
            new PetsStageResponse(status.Pets.Status, status.Pets.PetCount),
            new PetMedicalInfoStageResponse(
                status.PetMedicalInfo.Status,
                status.PetMedicalInfo.Pets
                    .Select(p => new PetMedicalInfoCompletionResponse(
                        p.PetId,
                        p.PetName,
                        p.IsMedicalInfoComplete))
                    .ToArray()),
            new IdentityStageResponse(status.Identity.Status, status.Identity.IdentityType),
            new PetParentVerificationStatusResponse(
                status.Verification.IsEmailVerified,
                status.Verification.IsMobileVerified),
            status.IsFullyOnboarded);
    }

    private static async Task<IResult> UploadPetProfilePhoto(
        Guid petId,
        IFormFile file,
        IPawfrontBlobStorage blobStorage,
        IParentPetService petService,
        CancellationToken cancellationToken)
    {
        var validation = ValidatePhotoFile(file);
        if (validation is not null)
        {
            return validation;
        }

        await using var stream = file.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            BlobUploadKind.PetProfilePhoto,
            petId,
            file.FileName,
            stream,
            file.ContentType,
            cancellationToken);

        try
        {
            var response = await petService.UpdateProfilePhotoAsync(petId, url, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (PetNotFoundException exception)
        {
            return ApiResults.NotFound("PetNotFound", exception.Message);
        }
    }

    private static async Task<IResult> UploadPetPhoto(
        Guid petId,
        IFormFile file,
        IPawfrontBlobStorage blobStorage,
        IParentPetService petService,
        CancellationToken cancellationToken)
    {
        var validation = ValidatePhotoFile(file);
        if (validation is not null)
        {
            return validation;
        }

        await using var stream = file.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            BlobUploadKind.PetPhoto,
            petId,
            file.FileName,
            stream,
            file.ContentType,
            cancellationToken);

        try
        {
            var response = await petService.AddPhotoAsync(petId, url, cancellationToken);
            return ApiResults.Created(
                $"/api/v1/pets/{petId}/photos/{response.PetPhotoId}",
                response);
        }
        catch (PetNotFoundException exception)
        {
            return ApiResults.NotFound("PetNotFound", exception.Message);
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
                "ImageTooLarge",
                $"Photo must be {MaxPhotoBytes / (1024 * 1024)} MB or smaller.");
        }

        var contentType = file.ContentType;
        if (string.IsNullOrWhiteSpace(contentType) || !AllowedPhotoContentTypes.Contains(contentType))
        {
            return ApiResults.BadRequest(
                "UnsupportedImageFormat",
                "Photo must be a JPEG, PNG, or WebP image.");
        }

        return null;
    }

    private static async Task<IResult> GetPet(
        Guid petId,
        IParentPetService petService,
        CancellationToken cancellationToken)
    {
        // The group's ownership filter already confirmed the pet exists and
        // belongs to the caller, so a null here would be an unexpected race
        // (pet deleted between filter and read). Map it to 404 defensively.
        var pet = await petService.GetPetAsync(petId, cancellationToken);
        return pet is null
            ? ApiResults.NotFound("PetNotFound", $"Pet '{petId}' was not found.")
            : ApiResults.Ok(pet);
    }

    private static async Task<IResult> UpdatePet(
        Guid petId,
        UpdatePetParentPetRequest request,
        IParentPetService petService,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await petService.UpdatePetAsync(petId, request, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (PetNotFoundException exception)
        {
            return ApiResults.NotFound("PetNotFound", exception.Message);
        }
        catch (MicrochipIdAlreadyExistsException exception)
        {
            return ApiResults.Conflict("MicrochipIdAlreadyExists", exception.Message);
        }
        catch (UnsupportedPetTypeException exception)
        {
            return ApiResults.BadRequest("UnsupportedPetType", exception.Message);
        }
        catch (UnsupportedPetGenderException exception)
        {
            return ApiResults.BadRequest("UnsupportedPetGender", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> UpdatePetMedicalInfo(
        Guid petId,
        UpdatePetMedicalInfoRequest request,
        IParentPetService petService,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await petService.UpdateMedicalInfoAsync(petId, request, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (PetNotFoundException exception)
        {
            return ApiResults.NotFound("PetNotFound", exception.Message);
        }
        catch (UnsupportedVaccinationStatusException exception)
        {
            return ApiResults.BadRequest("UnsupportedVaccinationStatus", exception.Message);
        }
        catch (UnsupportedSterilizationStatusException exception)
        {
            return ApiResults.BadRequest("UnsupportedSterilizationStatus", exception.Message);
        }
        catch (UnsupportedTemperamentException exception)
        {
            return ApiResults.BadRequest("UnsupportedTemperament", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> ListPets(
        Guid petParentId,
        IParentPetService petService,
        CancellationToken cancellationToken)
    {
        var pets = await petService.GetPetsAsync(petParentId, cancellationToken);
        return ApiResults.Ok(pets);
    }

    private static async Task<IResult> DeletePet(
        Guid petId,
        IParentPetService petService,
        CancellationToken cancellationToken)
    {
        // Ownership is already enforced by RequireOwnedPet on the group — the
        // route's petId is confirmed to belong to the caller (unknown → 404,
        // wrong owner → 403) before this handler runs.
        try
        {
            var response = await petService.DeletePetAsync(petId, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (PetNotFoundException exception)
        {
            return ApiResults.NotFound("PetNotFound", exception.Message);
        }
    }

    private static async Task<IResult> DeletePetPhoto(
        Guid petId,
        Guid photoId,
        IPawfrontBlobStorage blobStorage,
        IParentPetService petService,
        CancellationToken cancellationToken)
    {
        // Ownership: RequireOwnedPet on the group confirmed the pet belongs to
        // the caller; the sproc additionally scopes the photo by petId so a
        // photo can only be removed via its own pet.
        DeletePetPhotoResponse response;
        try
        {
            response = await petService.DeletePhotoAsync(petId, photoId, cancellationToken);
        }
        catch (PetPhotoNotFoundException exception)
        {
            return ApiResults.NotFound("PetPhotoNotFound", exception.Message);
        }

        // Best-effort blob cleanup: the SQL row (source of truth) is already
        // gone, so a storage hiccup must not fail the request — the blob is
        // merely orphaned for a future sweep.
        try
        {
            await blobStorage.DeleteAsync(response.PhotoUrl, cancellationToken);
        }
        catch
        {
            // Swallow — see above.
        }

        return ApiResults.Ok(response);
    }

    private static async Task<IResult> AddPet(
        Guid petParentId,
        AddPetParentPetRequest request,
        IParentPetService petService,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await petService.AddPetAsync(petParentId, request, cancellationToken);
            return ApiResults.Created(
                $"/api/v1/pet-parents/{petParentId}/pets/{response.PetId}",
                response);
        }
        catch (PetParentNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }
        catch (MicrochipIdAlreadyExistsException exception)
        {
            return ApiResults.Conflict("MicrochipIdAlreadyExists", exception.Message);
        }
        catch (UnsupportedPetTypeException exception)
        {
            return ApiResults.BadRequest("UnsupportedPetType", exception.Message);
        }
        catch (UnsupportedPetGenderException exception)
        {
            return ApiResults.BadRequest("UnsupportedPetGender", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> UploadIdentity(
        Guid petParentId,
        IFormFile file,
        [Microsoft.AspNetCore.Mvc.FromForm] string identityType,
        IPawfrontBlobStorage blobStorage,
        IParentOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        var validation = ValidatePhotoFile(file);
        if (validation is not null)
        {
            return validation;
        }

        if (string.IsNullOrWhiteSpace(identityType))
        {
            return ApiResults.BadRequest("InvalidRequest", "identityType is required.");
        }

        await using var stream = file.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            BlobUploadKind.PetParentIdentity,
            petParentId,
            file.FileName,
            stream,
            file.ContentType,
            cancellationToken);

        try
        {
            var response = await onboardingService.UpsertIdentityAsync(
                petParentId, identityType, url, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (PetParentNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }
        catch (UnsupportedPetParentIdentityTypeException exception)
        {
            return ApiResults.BadRequest("UnsupportedIdentityType", exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ApiResults.BadRequest("InvalidRequest", exception.Message);
        }
    }

    private static async Task<IResult> GetIdentity(
        Guid petParentId,
        IParentOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        // Ownership filter on the group already confirmed the parent belongs to
        // the caller — a null result here means the parent has no identity on
        // file yet, which maps to 404 (not 403).
        var response = await onboardingService.GetIdentityAsync(petParentId, cancellationToken);
        return response is null
            ? ApiResults.NotFound(
                "ParentIdentityNotFound",
                $"Pet parent '{petParentId}' has no identity on file.")
            : ApiResults.Ok(response);
    }

    private static async Task<IResult> DeleteIdentity(
        Guid petParentId,
        IPawfrontBlobStorage blobStorage,
        IParentOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        DeletePetParentIdentityResponse response;
        try
        {
            response = await onboardingService.DeleteIdentityAsync(petParentId, cancellationToken);
        }
        catch (PetParentIdentityNotFoundException exception)
        {
            return ApiResults.NotFound("ParentIdentityNotFound", exception.Message);
        }

        // Identity documents are sensitive — delete the stored blob too, not
        // just the SQL row. Best-effort: the row is the source of truth and
        // is already gone, so a storage hiccup must not fail the request
        // (the blob is merely orphaned, same as a re-upload).
        try
        {
            await blobStorage.DeleteAsync(response.IdentityPhotoUrl, cancellationToken);
        }
        catch
        {
            // Swallow — see above.
        }

        return ApiResults.Ok(response);
    }

    private static async Task<IResult> UploadPhoto(
        Guid petParentId,
        IFormFile file,
        IPawfrontBlobStorage blobStorage,
        IPetParentPhotoService photoService,
        CancellationToken cancellationToken)
    {
        var validation = ValidatePhotoFile(file);
        if (validation is not null)
        {
            return validation;
        }

        await using var stream = file.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            BlobUploadKind.PetParentPhoto,
            petParentId,
            file.FileName,
            stream,
            file.ContentType,
            cancellationToken);

        try
        {
            var response = await photoService.AddAsync(petParentId, url, cancellationToken);
            return ApiResults.Created(
                $"/api/v1/pet-parents/{petParentId}/photos/{response.PetParentPhotoId}",
                response);
        }
        catch (PetParentNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }
    }

    private static async Task<IResult> ListPhotos(
        Guid petParentId,
        IPetParentPhotoService photoService,
        CancellationToken cancellationToken)
    {
        var photos = await photoService.ListAsync(petParentId, cancellationToken);
        return ApiResults.Ok(photos);
    }

    private static async Task<IResult> DeletePhoto(
        Guid petParentId,
        Guid photoId,
        IPawfrontBlobStorage blobStorage,
        IPetParentPhotoService photoService,
        CancellationToken cancellationToken)
    {
        DeletePetParentPhotoResponse response;
        try
        {
            response = await photoService.DeleteAsync(petParentId, photoId, cancellationToken);
        }
        catch (PetParentPhotoNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentPhotoNotFound", exception.Message);
        }

        // Best-effort blob cleanup: the SQL row is already gone (the source of
        // truth), so a storage hiccup must not fail the request — the blob is
        // merely orphaned for a future sweep.
        try
        {
            await blobStorage.DeleteAsync(response.PhotoUrl, cancellationToken);
        }
        catch
        {
            // Swallow — see above.
        }

        return ApiResults.Ok(response);
    }

    private static async Task<IResult> UploadProfilePhoto(
        Guid petParentId,
        IFormFile file,
        IPawfrontBlobStorage blobStorage,
        IParentOnboardingService onboardingService,
        CancellationToken cancellationToken)
    {
        var validation = ValidatePhotoFile(file);
        if (validation is not null)
        {
            return validation;
        }

        await using var stream = file.OpenReadStream();
        var url = await blobStorage.UploadAsync(
            BlobUploadKind.PetParentProfilePhoto,
            petParentId,
            file.FileName,
            stream,
            file.ContentType,
            cancellationToken);

        try
        {
            var response = await onboardingService.UpdateProfilePhotoAsync(petParentId, url, cancellationToken);
            return ApiResults.Ok(response);
        }
        catch (PetParentNotFoundException exception)
        {
            return ApiResults.NotFound("PetParentNotFound", exception.Message);
        }
    }
}
