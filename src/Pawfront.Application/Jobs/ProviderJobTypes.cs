using Pawfront.Application.Bookings;
using Pawfront.Application.Earnings;
using Pawfront.Domain.Services;
using Pawfront.Domain.Vocabularies;

namespace Pawfront.Application.Jobs;

/// <summary>
/// Everything the provider's job-list screen can filter, sort and page by.
/// </summary>
/// <remarks>
/// <para>
/// Every filter is optional and null means "no constraint", so the bare query is
/// the provider's whole job history — the right thing for an inbox to open on,
/// and deliberately unlike <c>GET .../earnings/bookings</c>, whose default is the
/// EARNED rows because it has a summary to reconcile with.
/// </para>
/// <para>
/// Distance is absent from the sort keys; see
/// <see cref="Providers.ProviderSearchSortBy"/> for why.
/// </para>
/// </remarks>
/// <param name="Statuses">
/// Raw lifecycle statuses, already expanded from the friendly groups by
/// <see cref="BookingStatusFilter.Expand"/>. Empty means every status.
/// </param>
/// <param name="ServiceTypes">
/// <c>DayCare</c> / <c>NightStay</c> / <c>GroomingSession</c> /
/// <c>TrainingSession</c> / <c>VetAppointment</c>. The design shows only the two
/// pet-sitter values because that is the screen it was drawn from; the full
/// vocabulary is accepted so a groomer or a vet gets the same filter.
/// </param>
/// <param name="LocationType">
/// <c>ParentLocation</c> (the design's "Customer Address") or
/// <c>ProviderLocation</c> ("Your Address").
/// </param>
/// <param name="Breed">
/// Case-insensitive "contains" match on the pet's breed. Free text rather than a
/// picker: breeds are typed by the parent on the pet profile and there is no
/// canonical list of them anywhere in the product.
/// </param>
/// <param name="MinEarnings">
/// Inclusive lower bound on what the job is worth. A legacy booking with no price
/// snapshot has no amount and is excluded by either bound — it cannot be shown to
/// satisfy a range it has no value for.
/// </param>
/// <param name="From">
/// Start of the date window, matched against the job's own span rather than a
/// single date, so a stay that runs across the window is returned. Both bounds
/// are independently optional.
/// </param>
public sealed record ProviderJobQuery(
    Guid ProviderId,
    IReadOnlyCollection<string>? Statuses = null,
    IReadOnlyCollection<string>? ServiceTypes = null,
    Guid? ServiceId = null,
    string? LocationType = null,
    IReadOnlyCollection<string>? AnimalTypes = null,
    string? Breed = null,
    decimal? MinEarnings = null,
    decimal? MaxEarnings = null,
    DateOnly? From = null,
    DateOnly? To = null,
    ProviderJobSortBy SortBy = ProviderJobSortBy.Date,
    EarningsSortDirection SortDirection = EarningsSortDirection.Descending,
    int Skip = 0,
    int Take = 0);

/// <summary>Sort keys for the provider job list.</summary>
public enum ProviderJobSortBy
{
    /// <summary>
    /// The day the job starts, then its start time. Ascending is the design's
    /// "Soonest First".
    /// </summary>
    Date = 0,

    /// <summary>What the job is worth. Jobs with no price sort as a null amount.</summary>
    Earnings = 1
}

/// <summary>
/// One row on the provider's job list — the agenda card, with everything it
/// renders and nothing it does not, so a page costs one round trip rather than
/// one call per card to the booking detail.
/// </summary>
/// <param name="BookingType">
/// <c>SingleDay</c> | <c>NightStay</c>. Required to open the right detail screen:
/// the two booking tables share no id space, so the id alone cannot say which
/// this is.
/// </param>
/// <param name="JobId">The friendly <c>PF-000123</c> label, formatted as the booking detail formats it.</param>
/// <param name="JobDate">
/// The day the job STARTS — the booking date, or a stay's check-in date. Not the
/// earnings date, which for a stay is its checkout.
/// </param>
/// <param name="CheckOutDate">Night-stay only; null on a single-day booking.</param>
/// <param name="StartTime">
/// The booked start, or a stay's drop-off time — the time the provider has to be
/// somewhere either way.
/// </param>
/// <param name="Amount">
/// What the job is worth, from the same <c>Booking.BookingAmounts</c> definition
/// the earnings screen reads, so the two can never disagree. Null on a legacy
/// booking that froze no price.
/// </param>
/// <param name="IsEarned">
/// Did this job produce money (COMPLETED or PAID)? Emitted rather than re-derived
/// from <paramref name="Status"/>, since a list that mixes finished and cancelled
/// jobs needs every row to answer it and the rule already lives in SQL.
/// </param>
/// <param name="IsPrivate">A Custom walk-in — the provider's own off-platform job.</param>
public sealed record ProviderJobRow(
    string BookingType,
    Guid BookingId,
    string JobId,
    string Status,
    bool IsEarned,
    bool IsPaid,
    bool IsPrivate,
    Guid ServiceId,
    string? ServiceType,
    string ServiceCategory,
    string SubCategory,
    string? ServiceItemCode,
    DateOnly JobDate,
    DateOnly? CheckOutDate,
    int? Nights,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? LocationType,
    string? AddressLine,
    string? City,
    string? ZipCode,
    string? JobNotes,
    Guid? PetParentId,
    string? CustomerName,
    string? CustomerPhotoUrl,
    Guid? PetId,
    string? PetName,
    string? AnimalType,
    string? Breed,
    string? PetGender,
    decimal? Amount,
    decimal? Fee,
    string? PayoutId,
    string PayoutStatus,
    DateTimeOffset? PaidAtUtc,
    string? PaymentMethod,
    DateTimeOffset CreatedAtUtc);

/// <summary>One page of the provider's jobs, plus the total behind it.</summary>
public sealed record ProviderJobPage(
    IReadOnlyList<ProviderJobRow> Items,
    int TotalCount,
    int Skip,
    int Take)
{
    public bool HasMore => Skip + Items.Count < TotalCount;
}

/// <summary>
/// Validates the job list's own filter vocabularies. Kept beside the query rather
/// than in the endpoint so the rules live with the type they constrain, matching
/// where <see cref="BookingStatusFilter"/> and
/// <see cref="EarningsQueryParsing"/> sit.
/// </summary>
public static class ProviderJobQueryParsing
{
    /// <summary>
    /// The five bookable service types. A provider only ever has rows for their
    /// own category, so an unrecognised value is a client bug rather than an empty
    /// result to shrug at — it answers 400.
    /// </summary>
    public static readonly IReadOnlySet<string> ServiceTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            ProviderServiceTypes.DayCare,
            ProviderServiceTypes.NightStay,
            ProviderServiceTypes.GroomingSession,
            ProviderServiceTypes.TrainingSession,
            ProviderServiceTypes.VetAppointment
        };

    /// <summary>
    /// The two values <c>Booking.Bookings.LocationType</c> stores — deliberately
    /// the same strings the parent sends when creating a booking, not a
    /// provider-side synonym for them.
    /// </summary>
    public const string ParentLocation = "ParentLocation";
    public const string ProviderLocation = "ProviderLocation";

    public static ProviderJobSortBy ParseSortBy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ProviderJobSortBy.Date;
        }

        return Enum.TryParse<ProviderJobSortBy>(value.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Unsupported sortBy '{value}'. Expected 'Date' or 'Earnings'.", nameof(value));
    }

    public static IReadOnlyCollection<string> ParseServiceTypes(IEnumerable<string>? values) =>
        Normalise(values, ServiceTypes, "service type");

    /// <summary>
    /// Animal types are validated against the canonical Animal vocabulary — the
    /// same one the pet profile and the walk-in booking store — so a typo is a 400
    /// rather than a silently empty job list.
    /// </summary>
    public static IReadOnlyCollection<string> ParseAnimalTypes(IEnumerable<string>? values) =>
        Normalise(values, VocabularyCatalog.AnimalCodes, "animal type");

    public static string? ParseLocationType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim() switch
        {
            ParentLocation => ParentLocation,
            ProviderLocation => ProviderLocation,
            var unsupported => throw new ArgumentException(
                $"Unsupported locationType '{unsupported}'. " +
                $"Expected '{ParentLocation}' or '{ProviderLocation}'.", nameof(value))
        };
    }

    private static IReadOnlyCollection<string> Normalise(
        IEnumerable<string>? values,
        IReadOnlySet<string> allowed,
        string label)
    {
        if (values is null)
        {
            return Array.Empty<string>();
        }

        var normalised = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in values)
        {
            // A repeated query parameter can legitimately arrive with an empty
            // member (?animalTypes=&animalTypes=Dog); ignore those rather than
            // refusing the whole request over one.
            var value = raw?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            var match = allowed.FirstOrDefault(
                a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                throw new ArgumentException(
                    $"Unsupported {label} '{value}'. Expected one of: " +
                    string.Join(", ", allowed) + ".",
                    nameof(values));
            }

            normalised.Add(match);
        }

        return normalised;
    }
}
