using Pawfront.Application.Earnings;

namespace Pawfront.Application.Reviews;

/// <summary>
/// Parses the provider-reviews list's query-string values.
/// </summary>
/// <remarks>
/// Sort DIRECTION is deliberately not re-implemented here: it delegates to
/// <see cref="EarningsQueryParsing.ParseSortDirection"/> so a client that learned
/// <c>sortDirection=Asc</c> on the earnings list does not discover reviews spell it
/// differently. The working agreement is explicit that shared vocabularies get one
/// definition rather than a per-feature copy. The <c>Earnings</c> namespace reads
/// oddly from here — a rename to a neutral shared parser would be a clean follow-up,
/// but duplicating the vocabulary to avoid the awkward name would be the worse trade.
/// Throws <see cref="ArgumentException"/> on an unrecognised value so endpoints can
/// answer 400 rather than silently ignoring a sort the caller asked for.
/// </remarks>
public static class ReviewQueryParsing
{
    /// <summary>Blank defaults to newest-first, which is what the screen opens on.</summary>
    public static EarningsSortDirection ParseSortDirection(string? value)
        => EarningsQueryParsing.ParseSortDirection(value);

    /// <summary>Blank defaults to the date the review was given.</summary>
    public static ReviewSortBy ParseSortBy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ReviewSortBy.Date;
        }

        return Enum.TryParse<ReviewSortBy>(value.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Unsupported sortBy '{value}'. Expected 'Date' or 'Rating'.", nameof(value));
    }
}
