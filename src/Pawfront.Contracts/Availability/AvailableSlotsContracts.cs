namespace Pawfront.Contracts.Availability;

public sealed record AvailableSlotsResponse(
    Guid ProviderId,
    DateOnly Date,
    string ServiceCategory,
    string SubCategory,
    decimal DurationHours,
    int Capacity,
    int GranularityMinutes,
    IReadOnlyCollection<TimeSlotResponse> Slots);

public sealed record TimeSlotResponse(TimeOnly StartTime, TimeOnly EndTime);
