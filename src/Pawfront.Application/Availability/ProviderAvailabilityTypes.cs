namespace Pawfront.Application.Availability;

public sealed record SaveProviderWeeklyAvailabilityCommand(
    Guid ProviderId,
    IReadOnlyList<DayAvailabilityInput> Days);

public sealed record DayAvailabilityInput(
    int DayOfWeek,              // 0 = Sunday .. 6 = Saturday
    bool IsOpen,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    TimeOnly? BreakStartTime,
    TimeOnly? BreakEndTime);

public sealed record ProviderWeeklyAvailabilityResult(
    Guid ProviderId,
    IReadOnlyList<DayAvailabilityResult> Days);

public sealed record DayAvailabilityResult(
    int DayOfWeek,
    bool IsOpen,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    TimeOnly? BreakStartTime,
    TimeOnly? BreakEndTime);

public sealed class AvailabilityProviderNotFoundException(Guid providerId)
    : Exception($"Provider profile '{providerId}' was not found.");
