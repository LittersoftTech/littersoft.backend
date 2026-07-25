namespace Pawfront.Contracts.Availability;

/// <summary>
/// Response of <c>GET .../availability/slots</c> (both hosts). Hourly services
/// (DayCare / GroomingSession / TrainingSession / VetAppointment) fill
/// <see cref="Slots"/> and leave <see cref="Nights"/> null. NightStay is
/// DATE-granular — capacity is per night, not per time window — so it fills
/// <see cref="Nights"/> (one entry per night in <c>date..endDate</c>, remaining
/// capacity included) and leaves <see cref="Slots"/> empty;
/// <see cref="DurationHours"/> / <see cref="GranularityMinutes"/> are 0.
/// </summary>
public sealed record AvailableSlotsResponse(
    Guid ProviderId,
    Guid ServiceId,
    DateOnly Date,
    string ServiceCategory,
    string SubCategory,
    string ServiceType,
    decimal DurationHours,
    int Capacity,
    int GranularityMinutes,
    IReadOnlyCollection<TimeSlotResponse> Slots,
    IReadOnlyCollection<NightAvailabilityResponse>? Nights = null);

/// <summary>
/// One slot window. <see cref="RemainingCapacity"/> = the service's
/// capacity minus the active bookings overlapping this window — how many more
/// pets can still be booked into it. Fully-booked windows are returned with
/// <see cref="RemainingCapacity"/> = 0 so the client can render them as
/// unavailable.
/// </summary>
public sealed record TimeSlotResponse(TimeOnly StartTime, TimeOnly EndTime, int RemainingCapacity);

/// <summary>
/// Per-night availability for a NightStay service. <see cref="IsClosed"/> —
/// a full-day closure covers the night; <see cref="IsAvailable"/> — the night
/// is open and has at least one capacity unit left.
/// </summary>
public sealed record NightAvailabilityResponse(
    DateOnly Date,
    int ActiveBookings,
    int RemainingCapacity,
    bool IsClosed,
    bool IsAvailable);
