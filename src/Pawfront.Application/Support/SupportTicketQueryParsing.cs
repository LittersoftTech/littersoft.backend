namespace Pawfront.Application.Support;

/// <summary>
/// Parses the query-string vocabulary of the "my tickets" list, so both hosts accept
/// exactly the same values.
/// </summary>
/// <remarks>
/// Sits alongside <see cref="Earnings.EarningsQueryParsing"/> and
/// <c>BookingStatusFilter</c> for the same reason those do: the status GROUPS are
/// expanded to raw lifecycle statuses here, in C#, because <c>Support.ListTickets</c> takes
/// a plain CSV. Adding a status is then an edit to <see cref="SupportTicketStatuses"/> and
/// this file, rather than to the procedure.
/// Every method throws <see cref="ArgumentException"/> on an unrecognised value, so
/// endpoints answer 400 instead of silently ignoring a filter the caller meant.
/// </remarks>
public static class SupportTicketQueryParsing
{
    /// <summary>The friendly group meaning "everything not yet closed".</summary>
    private const string OpenGroup = "open";

    /// <summary>Sort key; blank defaults to last activity, which is what the screen opens on.</summary>
    public static SupportTicketSortBy ParseSortBy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SupportTicketSortBy.UpdatedAt;
        }

        return Enum.TryParse<SupportTicketSortBy>(value.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Unsupported sortBy '{value}'. Expected 'UpdatedAt' or 'CreatedAt'.", nameof(value));
    }

    /// <summary>
    /// Expands a comma-separated <c>status</c> filter into raw lifecycle statuses.
    /// Accepts the friendly group <c>Open</c> (every non-closed status) alongside any raw
    /// value from <see cref="SupportTicketStatuses.All"/>, case-insensitively. Blank or
    /// omitted means no filter — the caller's whole history.
    /// </summary>
    /// <remarks>
    /// <c>Closed</c> needs no group of its own: it is already a raw status and expands to
    /// itself, so <c>?status=Closed</c> works without a special case.
    /// </remarks>
    public static IReadOnlyList<string>? ParseStatuses(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var expanded = new List<string>();

        foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(token, OpenGroup, StringComparison.OrdinalIgnoreCase))
            {
                expanded.AddRange(SupportTicketStatuses.Open);
                continue;
            }

            var match = SupportTicketStatuses.All.FirstOrDefault(
                known => string.Equals(known, token, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                throw new ArgumentException(
                    $"Unsupported status '{token}'. Expected 'Open' or one of: " +
                    $"{string.Join(", ", SupportTicketStatuses.All)}.",
                    nameof(value));
            }

            expanded.Add(match);
        }

        // A caller can legitimately name overlapping filters (?status=Open,OPENED), and the
        // procedure's IN (...) would be unharmed by duplicates — but de-duplicating keeps
        // the CSV short and the intent obvious in a query plan.
        return expanded.Distinct(StringComparer.Ordinal).ToArray();
    }
}
