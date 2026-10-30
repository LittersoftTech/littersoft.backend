using Pawfront.Contracts.Reliability;

namespace Pawfront.Application.Reliability;

public sealed record ServiceReliabilityMark(string BookingType, Guid BookingId, string Outcome, DateTimeOffset OccurredAtUtc);

public static class ServiceReliabilityCalculator
{
    public static ServiceReliabilityResponse Calculate(IEnumerable<ServiceReliabilityMark> history, DateTimeOffset now)
    {
        var marks = history.Where(m => m.OccurredAtUtc >= now.AddDays(-180) && m.OccurredAtUtc <= now)
            .OrderByDescending(m => m.OccurredAtUtc).ThenBy(m => m.BookingType, StringComparer.Ordinal)
            .ThenBy(m => m.BookingId).ToArray();
        var (positive, negative) = Points(marks.Select(m => m.Outcome));
        decimal? rawScore = marks.Length >= 5 ? positive / (positive + negative) * 100m : null;
        var lastNegative = marks.Where(m => m.Outcome != "Paid").Select(m => (DateTimeOffset?)m.OccurredAtUtc).FirstOrDefault();
        var expiry = lastNegative?.AddDays(30);
        var flagged = rawScore < 50m && expiry > now;

        // Recompute the recency weights after each hypothetical positive outcome.
        // A fixed points-deficit calculation would be wrong as older marks change position.
        int? additional = null;
        if (flagged)
        {
            additional = 0;
            do
            {
                additional++;
                var totals = Points(Enumerable.Repeat("Paid", additional.Value).Concat(marks.Select(m => m.Outcome)));
                if (totals.Positive >= totals.Negative) break;
            } while (additional < marks.Length * 6 + 5);
        }

        return new(true, rawScore is null ? null : decimal.Round(rawScore.Value, 2, MidpointRounding.AwayFromZero),
            marks.Length >= 5, marks.Length, marks.Count(m => m.Outcome == "Paid"),
            marks.Count(m => m.Outcome == "LateCancellation"), marks.Count(m => m.Outcome == "NoShow"),
            marks.Count(m => m.Outcome == "Unpaid"), positive, negative, flagged, lastNegative,
            flagged ? expiry : null, additional);
    }

    public static ServiceReliabilityResponse NotApplicable() =>
        new(false, null, false, 0, 0, 0, 0, 0, 0, 0, false, null, null, null);

    private static (decimal Positive, decimal Negative) Points(IEnumerable<string> outcomes)
    {
        decimal positive = 0, negative = 0;
        var index = 0;
        foreach (var outcome in outcomes)
        {
            var weight = index < 5 ? 1m : index < 10 ? .5m : .25m;
            if (outcome == "Paid") positive += weight;
            else negative += weight * (outcome switch
            {
                "LateCancellation" => 2,
                "NoShow" => 4,
                "Unpaid" => 6,
                _ => throw new ArgumentException($"Unknown reliability outcome: {outcome}")
            });
            index++;
        }
        return (positive, negative);
    }
}
