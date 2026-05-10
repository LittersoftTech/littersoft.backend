namespace Pawfront.Domain.Bookings;

public sealed class Booking
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ProviderId { get; init; }
    public Guid ServiceId { get; init; }
    public Guid CustomerId { get; init; }
    public Guid PetId { get; init; }
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
    public BookingStatus Status { get; set; } = BookingStatus.Requested;
}
