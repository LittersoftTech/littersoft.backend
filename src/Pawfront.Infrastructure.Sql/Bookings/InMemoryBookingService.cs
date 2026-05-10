using System.Collections.Concurrent;
using Pawfront.Application.Bookings;
using Pawfront.Contracts.Bookings;
using Pawfront.Domain.Bookings;

namespace Pawfront.Infrastructure.Sql.Bookings;

internal sealed class InMemoryBookingService : IBookingService
{
    private readonly ConcurrentDictionary<Guid, Booking> bookings = new();

    public Task<BookingResponse> CreateAsync(Guid providerId, CreateBookingRequest request, CancellationToken cancellationToken)
    {
        var booking = new Booking
        {
            ProviderId = providerId,
            ServiceId = request.ServiceId,
            CustomerId = request.CustomerId,
            PetId = request.PetId,
            StartsAt = request.StartsAt,
            EndsAt = request.EndsAt,
            Status = BookingStatus.Confirmed
        };

        bookings[booking.Id] = booking;

        return Task.FromResult(ToResponse(booking));
    }

    public Task<IReadOnlyCollection<BookingResponse>> ListByProviderAsync(Guid providerId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<BookingResponse> result = bookings.Values
            .Where(booking => booking.ProviderId == providerId)
            .OrderBy(booking => booking.StartsAt)
            .Select(ToResponse)
            .ToArray();

        return Task.FromResult(result);
    }

    private static BookingResponse ToResponse(Booking booking)
    {
        return new BookingResponse(
            booking.Id,
            booking.ProviderId,
            booking.ServiceId,
            booking.CustomerId,
            booking.PetId,
            booking.StartsAt,
            booking.EndsAt,
            booking.Status.ToString());
    }
}
