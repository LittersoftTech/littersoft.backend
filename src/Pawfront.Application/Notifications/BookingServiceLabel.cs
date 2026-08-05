using Pawfront.Application.Services.PetGroomer;

namespace Pawfront.Application.Notifications;

/// <summary>
/// Turns a booking's stored service identifiers into the customer-facing name of
/// what was actually booked — the "Service Booked" line in a notification.
///
/// Resolution order matters: a groomer's <c>ServiceItemCode</c> is far more
/// useful to them than the category, because one PetGroomer service covers all
/// 18 menu items. "Nails Clipping" tells them what to prepare; "Pet Grooming"
/// does not.
/// </summary>
public static class BookingServiceLabel
{
    /// <summary>
    /// The specific grooming menu item when the booking names one, otherwise the
    /// bookable service's display name.
    /// </summary>
    /// <param name="serviceType">
    /// The <c>Provider.ProviderServices.ServiceType</c> — DayCare, NightStay,
    /// GroomingSession, TrainingSession or VetAppointment.
    /// </param>
    /// <param name="serviceItemCode">
    /// The booking's grooming menu-item code. Populated only for PetGroomer
    /// bookings; null everywhere else.
    /// </param>
    public static string Resolve(string? serviceType, string? serviceItemCode)
    {
        if (!string.IsNullOrWhiteSpace(serviceItemCode)
            && GroomingServiceCatalog.TryGetDisplayName(serviceItemCode, out var itemName))
        {
            return itemName;
        }

        return FromServiceType(serviceType);
    }

    /// <summary>
    /// The night-stay variant: the stay's length is part of what was booked, so
    /// it is folded into the label ("Night Stay (3 nights)") rather than adding a
    /// fourth field to an already-dense notification body.
    /// </summary>
    public static string ResolveNightStay(DateOnly checkInDate, DateOnly checkOutDate)
    {
        // The checkout day is NOT a stayed night — the stay spans
        // [CheckInDate, CheckOutDate), same as everywhere else in the codebase.
        var nights = checkOutDate.DayNumber - checkInDate.DayNumber;

        return nights switch
        {
            < 1 => "Night Stay",
            1 => "Night Stay (1 night)",
            _ => $"Night Stay ({nights} nights)"
        };
    }

    private static string FromServiceType(string? serviceType) => serviceType switch
    {
        "DayCare" => "Day Care",
        "NightStay" => "Night Stay",
        "GroomingSession" => "Grooming",
        "TrainingSession" => "Training Session",
        "VetAppointment" => "Vet Appointment",
        // The set above is closed today. Echo anything else rather than showing
        // nothing — a new service type should read oddly, not vanish.
        _ => string.IsNullOrWhiteSpace(serviceType) ? "a service" : serviceType
    };
}
