namespace Pawfront.Contracts.Bookings;

public sealed record CreateBookingRequest(
    Guid PetParentId,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime);

public sealed record CancelBookingRequest(Guid PetParentId);

public sealed record BookingResponse(
    Guid BookingId,
    Guid ProviderId,
    Guid PetParentId,
    string ServiceCategory,
    string SubCategory,
    DateOnly BookingDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CancelledAtUtc);
