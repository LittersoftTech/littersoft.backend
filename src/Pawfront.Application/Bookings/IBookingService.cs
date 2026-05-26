namespace Pawfront.Application.Bookings;

public interface IBookingService
{
    Task<BookingResult> CreateAsync(CreateBookingCommand command, CancellationToken cancellationToken);

    Task<BookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken);

    Task<BookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken);

    /// <summary>
    /// List the provider's bookings. <paramref name="date"/> narrows to a single
    /// calendar day when provided (the provider's "today" view); omit it to return
    /// the full history.
    /// </summary>
    Task<IReadOnlyList<BookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? date,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<BookingResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken);
}
