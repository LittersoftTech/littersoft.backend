namespace Pawfront.Contracts.Bookings;

public sealed record CreateBookingRequest(
    Guid ServiceId,
    Guid CustomerId,
    Guid PetId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt);

public sealed record BookingResponse(
    Guid Id,
    Guid ProviderId,
    Guid ServiceId,
    Guid CustomerId,
    Guid PetId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status);
