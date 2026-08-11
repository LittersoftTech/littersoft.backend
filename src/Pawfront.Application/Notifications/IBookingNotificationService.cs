using Pawfront.Application.Bookings;

namespace Pawfront.Application.Notifications;

/// <summary>
/// Composes booking-related notifications — who to tell, and with what
/// parameters — so the endpoint handlers stay thin and the decision lives in one
/// place rather than being duplicated across two hosts.
///
/// Deliberately NOT called from <c>BookingService.CreateAsync</c>: that method is
/// shared by both hosts, and a provider creating a booking on their own host
/// should not be notified about their own action. Calling from the parent host's
/// two create handlers scopes this to genuinely parent-initiated bookings.
/// </summary>
public interface IBookingNotificationService
{
    /// <summary>
    /// Tells the provider a parent has booked a single-day service.
    /// </summary>
    /// <param name="serviceType">
    /// The catalog row's <c>ServiceType</c>, used to name the service when the
    /// booking carries no grooming menu-item code.
    /// </param>
    /// <param name="petName">The pet the booking is for; may be null/blank.</param>
    /// <param name="parentName">
    /// Who is asking — the provider's decision starts with this. May be
    /// null/blank, in which case the copy falls back to "A customer".
    /// </param>
    Task NotifyBookingRequestedAsync(
        BookingResult booking,
        string? serviceType,
        string? petName,
        string? parentName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Tells the provider a parent has booked a multi-night boarding stay.
    /// </summary>
    /// <param name="petName">The pet the stay is for; may be null/blank.</param>
    /// <param name="parentName">Who is asking; may be null/blank.</param>
    Task NotifyNightStayBookingRequestedAsync(
        NightStayBookingResult booking,
        string? petName,
        string? parentName,
        CancellationToken cancellationToken);
}
