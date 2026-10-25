namespace Pawfront.Contracts.Reliability;

/// <summary>Account-specific reliability. Mobile clients decide which fields to display.</summary>
public sealed record ReliabilityResponse(
    ServiceReliabilityResponse Services,
    CommunityReliabilityResponse Community,
    HostHistoryResponse Hosting,
    DateTimeOffset CalculatedAtUtc);

public sealed record ServiceReliabilityResponse(
    bool IsApplicable,
    decimal? Score,
    bool MeetsVolumeGate,
    int CountedBookings,
    int CompletedAndPaidBookings,
    int LateCancellations,
    int NoShows,
    int UnpaidServices,
    decimal WeightedPointsInFavour,
    decimal WeightedPointsAgainst,
    bool IsRedFlagged,
    DateTimeOffset? LastNegativeAtUtc,
    DateTimeOffset? RedFlagExpiresAtUtc,
    int? AdditionalPaidBookingsToReachThreshold,
    int WindowDays = 180,
    int MinimumCountedBookings = 5,
    int RedFlagThreshold = 50,
    int RedFlagDurationDays = 30);

public sealed record CommunityReliabilityResponse(
    bool IsRedFlagged,
    DateTimeOffset? RedFlagExpiresAtUtc,
    int ConsecutiveNegativeMarks,
    int ConsecutivePaidEventsAttended,
    int PaidEventsToRestoreAccess,
    bool CanCreateEvents,
    int MaximumUpcomingBookings,
    int ActiveBookingCount,
    int AvailableBookingSlots,
    int TriggerWindowDays = 30,
    int RedFlagDurationDays = 30,
    int ActivePaidBookingCount = 0);

public sealed record HostHistoryResponse(
    bool HasPublishedEvents,
    int EventsRun,
    int EventsCancelled,
    DateTimeOffset WindowStartsAtUtc,
    int WindowMonths = 12);
