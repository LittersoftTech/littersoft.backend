using System.Security.Claims;
using Pawfront.Application.Events;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.ParentPets;
using Pawfront.Application.Storage;
using Pawfront.Contracts.Events;
using Pawfront.Contracts.ParentOnboarding;
using Pawfront.Contracts.ParentPets;
using Pawfront.PetParentApi.Auth;

namespace Pawfront.PetParentApi.Endpoints;

internal static class PetParentEndpoints
{
    // Hard upper bound on uploaded image size. Mirrors the user-specified
    // "max 1 MB" constraint. Applied to both pet-parent profile photos and
    // individual pet photos — both endpoints share the same validation.
    private const long MaxPhotoBytes = 1L * 1024 * 1024;

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
        group.MapDelete("/identity", DeleteIdentity);
        group.MapGet("/event-bookings", ListEventBookings);
        group.MapGet("/onboarding-status", GetOnboardingStatus);

        var mobileVerification = group.MapGroup("/mobile-verification");
        mobileVerification.MapPost("/otp", SendMobileOtp);
        mobileVerification.MapPost("/otp/{parentMobileOtpId:guid}/verify", VerifyMobileOtp);

        // Ownership: every pet-scoped route confirms the {petId:guid} belongs
        // to the caller's resolved PetParentId. Unknown pet → 404; wrong
        // owner → 403.
        var pets = builder.MapGroup("/pets/{petId:guid}").RequireOwnedPet();
        pets.MapPatch("/", UpdatePet);
        pets.MapPatch("/medical-info", UpdatePetMedicalInfo);
        pets.MapPost("/photos", UploadPetPhoto).DisableAntiforgery();

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
            summary.EventStartDate,
            summary.EventStartTime,
            summary.EventBannerImageUrl,
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
                $"Photo must be {MaxPhotoBytes / 1024} KB or smaller.");
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
