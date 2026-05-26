namespace Pawfront.Contracts.Availability;

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
    IReadOnlyCollection<TimeSlotResponse> Slots);

public sealed record TimeSlotResponse(TimeOnly StartTime, TimeOnly EndTime);
