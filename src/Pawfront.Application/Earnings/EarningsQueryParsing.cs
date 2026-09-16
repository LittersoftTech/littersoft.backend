namespace Pawfront.Application.Earnings;

/// <summary>
/// Parses the sort query-string values shared by the provider earnings breakdown
/// and the parent booking history.
/// </summary>
/// <remarks>
/// Lives in Application rather than in either host so the two accept exactly the
/// same vocabulary — a client that learns <c>sortDirection=Asc</c> on one API does
/// not discover the other spells it differently. Sits next to
/// <see cref="EarningsPeriodRange.Parse"/>, which is here for the same reason.
/// Every method throws <see cref="ArgumentException"/> on an unrecognised value so
/// endpoints can answer 400 instead of silently ignoring a filter the caller meant.
/// </remarks>
public static class EarningsQueryParsing
{
    /// <summary>
    /// Accepts <c>Asc</c>/<c>Ascending</c> and <c>Desc</c>/<c>Descending</c>,
    /// case-insensitively. Blank defaults to descending — newest (or largest) first
    /// is what both screens open on.
    /// </summary>
    public static EarningsSortDirection ParseSortDirection(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return EarningsSortDirection.Descending;
        }

        return trimmed.ToLowerInvariant() switch
        {
            "asc" or "ascending" => EarningsSortDirection.Ascending,
            "desc" or "descending" => EarningsSortDirection.Descending,
            _ => throw new ArgumentException(
                $"Unsupported sortDirection '{value}'. Expected 'Asc' or 'Desc'.", nameof(value))
        };
    }

    /// <summary>Sort key for the provider earnings breakdown; blank defaults to date.</summary>
    public static EarningsSortBy ParseEarningsSortBy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EarningsSortBy.Date;
        }

        return Enum.TryParse<EarningsSortBy>(value.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Unsupported sortBy '{value}'. Expected 'Date' or 'Earnings'.", nameof(value));
    }

    /// <summary>Sort key for the parent booking history; blank defaults to date.</summary>
    public static ParentHistorySortBy ParseParentHistorySortBy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ParentHistorySortBy.Date;
        }

        return Enum.TryParse<ParentHistorySortBy>(value.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Unsupported sortBy '{value}'. Expected 'Date' or 'Amount'.", nameof(value));
    }
}
