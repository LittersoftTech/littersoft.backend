namespace Pawfront.Contracts.Availability;

public sealed record SaveProviderWeeklyAvailabilityRequest(
    IReadOnlyList<DayAvailabilityRequest> Days,
    string TimeZone = "UTC");

public sealed record DayAvailabilityRequest(
    int DayOfWeek,              // 0 = Sunday .. 6 = Saturday
    bool IsOpen,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    TimeOnly? BreakStartTime,
    TimeOnly? BreakEndTime);

public sealed record ProviderWeeklyAvailabilityResponse(
    Guid ProviderId,
    IReadOnlyList<DayAvailabilityResponse> Days,
    string TimeZone = "UTC");

public sealed record DayAvailabilityResponse(
    int DayOfWeek,
    bool IsOpen,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    TimeOnly? BreakStartTime,
    TimeOnly? BreakEndTime);
