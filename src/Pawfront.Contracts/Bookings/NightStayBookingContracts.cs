namespace Pawfront.Contracts.Bookings;

/// <summary>
/// Body for the parent-host night-stay create
/// (<c>POST /pet-parents/{petParentId}/night-stay-bookings</c>). The booker is the
/// route's petParentId (ownership-filtered); PetId must be one of their pets. The
/// provider is resolved server-side from ServiceId. The stay spans
/// <c>[CheckInDate, CheckOutDate)</c> — CheckOutDate is the pickup day and is NOT
/// a stayed night.
/// </summary>
public sealed record CreateParentNightStayBookingRequest(
    Guid PetId,
    Guid ServiceId,
    DateOnly CheckInDate,
    DateOnly CheckOutDate);

public sealed record NightStayBookingResponse(
    Guid NightStayBookingId,
    Guid ProviderId,
    Guid PetParentId,
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    TimeOnly DropOffTime,
    TimeOnly PickUpTime,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CancelledAtUtc,
    Guid? PetId);

/// <summary>
/// Body for a night-stay modification request
/// (<c>POST .../night-stay-bookings/{bookingId}/modifications</c>). The proposed
/// check-in / check-out range. Accept/decline reuses
/// <see cref="RespondBookingModificationRequest"/>.
/// </summary>
public sealed record RequestNightStayBookingModificationRequest(
    DateOnly CheckInDate,
    DateOnly CheckOutDate,
    string? Note);

/// <summary>The staged (pending) check-in/check-out change proposal on a night-stay booking.</summary>
public sealed record NightStayBookingModificationResponse(
    Guid NightStayBookingModificationId,
    Guid NightStayBookingId,
    string RequestedByActor,
    Guid RequestedByActorId,
    DateOnly ProposedCheckInDate,
    DateOnly ProposedCheckOutDate,
    string? Note,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// Night-stay single booking read: the booking, the start-OTP (when startable),
/// and the staged pending modification (when one awaits a response).
/// </summary>
public sealed record NightStayBookingDetailResponse(
    NightStayBookingResponse Booking,
    StartOtpResponse? StartOtp,
    NightStayBookingModificationResponse? PendingModification);
