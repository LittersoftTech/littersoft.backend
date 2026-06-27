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
