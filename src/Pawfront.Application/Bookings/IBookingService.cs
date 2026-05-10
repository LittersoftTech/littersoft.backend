using Pawfront.Contracts.Bookings;

namespace Pawfront.Application.Bookings;

public interface IBookingService
{
    Task<BookingResponse> CreateAsync(Guid providerId, CreateBookingRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<BookingResponse>> ListByProviderAsync(Guid providerId, CancellationToken cancellationToken);
}
