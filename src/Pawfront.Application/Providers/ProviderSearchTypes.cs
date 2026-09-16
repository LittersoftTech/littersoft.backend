namespace Pawfront.Application.Providers;

/// <summary>
/// Criteria for the per-service parent-facing search endpoints. All filters
/// are optional; endpoint-level validation guarantees the date/time fields
/// arrive as complete groups (trio for day care, pair for night stay).
/// Animals / City / ServiceLocation share the semantics of
/// <see cref="ProviderDiscoveryFilter"/>.
/// </summary>
public sealed record DayCareProviderSearchCriteria(
    IReadOnlyCollection<string>? Animals,
    string? City,
    string? ServiceLocation,
    DateOnly? Date,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    int Skip,
    int Take,
    ProviderSearchRefinements? Refinements = null,
    /// <summary>
    /// The temperament recorded on the pet named by <c>?petId=</c> (Anxious /
    /// Friendly / Aggressive), resolved server-side from that pet. NOT a filter:
    /// it only stamps <see cref="ProviderSearchResult.MatchesPetTemperament"/> on
    /// each card so the app can bucket a non-match into its "Other" section. The
    /// explicit <c>dogTemperaments</c> refinement is the hard filter.
    /// </summary>
    string? PetTemperament = null);

/// <summary>
/// The filters and sort shared by all five per-service searches — everything on
/// the parent app's filter sheet that is not specific to one service's shape.
/// </summary>
/// <remarks>
/// <para>
/// One record rather than five identical trailing parameters per criteria type:
/// the sheet is the same sheet whichever service the parent came in through, and
/// a copy per search would be five places to keep in step.
/// </para>
/// <para>
/// Every field is optional and null means "no constraint". A null
/// <see cref="Refinements"/> altogether is exactly today's behaviour, which is
/// what keeps existing callers unaffected.
/// </para>
/// </remarks>
/// <param name="ProviderType">
/// <c>RegisteredBusiness</c> | <c>Freelancer</c>, from
/// <see cref="ProviderTypeFilters"/>. Classified from the hit's sub-category, so
/// it costs no extra read.
/// </param>
/// <param name="PaymentMethods">
/// The provider must accept EVERY listed method (<c>Cash</c> / <c>Digital</c>).
/// A provider who has never saved a payout policy is excluded when this is set:
/// the parent is asking who takes their money, and "unrecorded" is not a yes.
/// </param>
/// <param name="DogTemperaments">
/// OR semantics — see <see cref="ProviderDiscoveryFilter.DogTemperaments"/>.
/// Meaningful only on the PetSitter and PetGroomer searches; the vet and trainer
/// endpoints do not expose it, because their offerings hold no such list and a
/// filter that silently matched nothing would be worse than no filter.
/// </param>
/// <param name="SortBy">
/// Null keeps discovery order. Any non-null value forces the search to evaluate
/// EVERY candidate before paging — a sorted page cannot be assembled from a
/// prefix of the candidates — so it costs more than an unsorted one.
/// </param>
public sealed record ProviderSearchRefinements(
    string? ProviderType = null,
    IReadOnlyCollection<string>? PaymentMethods = null,
    IReadOnlyCollection<string>? DogTemperaments = null,
    ProviderSearchSortBy? SortBy = null,
    Earnings.EarningsSortDirection SortDirection = Earnings.EarningsSortDirection.Descending);

/// <param name="StartDate">Drop-off date (first stayed night).</param>
/// <param name="PickupDate">
/// Checkout date — NOT a stayed night. Every date from StartDate up to the
/// day before PickupDate must have free NightStay capacity.
/// </param>
public sealed record NightStayProviderSearchCriteria(
    IReadOnlyCollection<string>? Animals,
    string? City,
    string? ServiceLocation,
    DateOnly? StartDate,
    DateOnly? PickupDate,
    int Skip,
    int Take,
    ProviderSearchRefinements? Refinements = null,
    /// <summary>
    /// The temperament recorded on the pet named by <c>?petId=</c> (Anxious /
    /// Friendly / Aggressive), resolved server-side from that pet. NOT a filter:
    /// it only stamps <see cref="ProviderSearchResult.MatchesPetTemperament"/> on
    /// each card so the app can bucket a non-match into its "Other" section. The
    /// explicit <c>dogTemperaments</c> refinement is the hard filter.
    /// </summary>
    string? PetTemperament = null);

/// <param name="ServiceItemCode">
/// One of the 18 canonical grooming codes. When set, only providers with
/// that item active on their menu match, and Charges carries the item's
/// price. When null, any groomer with at least one active menu item matches
/// and Charges is null (price is per item).
/// </param>
public sealed record GroomingProviderSearchCriteria(
    IReadOnlyCollection<string>? Animals,
    string? City,
    string? ServiceLocation,
    DateOnly? Date,
    string? ServiceItemCode,
    int Skip,
    int Take,
    ProviderSearchRefinements? Refinements = null,
    /// <summary>
    /// The temperament recorded on the pet named by <c>?petId=</c> (Anxious /
    /// Friendly / Aggressive), resolved server-side from that pet. NOT a filter:
    /// it only stamps <see cref="ProviderSearchResult.MatchesPetTemperament"/> on
    /// each card so the app can bucket a non-match into its "Other" section. The
    /// explicit <c>dogTemperaments</c> refinement is the hard filter.
    /// </summary>
    string? PetTemperament = null);

public sealed record VetProviderSearchCriteria(
    IReadOnlyCollection<string>? Animals,
    string? City,
    string? ServiceLocation,
    DateOnly? Date,
    int Skip,
    int Take,
    ProviderSearchRefinements? Refinements = null,
    /// <summary>
    /// The temperament recorded on the pet named by <c>?petId=</c> (Anxious /
    /// Friendly / Aggressive), resolved server-side from that pet. NOT a filter:
    /// it only stamps <see cref="ProviderSearchResult.MatchesPetTemperament"/> on
    /// each card so the app can bucket a non-match into its "Other" section. The
    /// explicit <c>dogTemperaments</c> refinement is the hard filter.
    /// </summary>
    string? PetTemperament = null);

/// <summary>
/// Criteria for the PetTrainer/TrainingSession search. A training session is a
/// single fixed-duration booking (like a vet appointment): when a date is set,
/// a provider matches if any free slot of the session's duration exists that
/// day. Charges = PricePerSession.
/// </summary>
public sealed record TrainerProviderSearchCriteria(
    IReadOnlyCollection<string>? Animals,
    string? City,
    string? ServiceLocation,
    DateOnly? Date,
    int Skip,
    int Take,
    ProviderSearchRefinements? Refinements = null,
    /// <summary>
    /// The temperament recorded on the pet named by <c>?petId=</c> (Anxious /
    /// Friendly / Aggressive), resolved server-side from that pet. NOT a filter:
    /// it only stamps <see cref="ProviderSearchResult.MatchesPetTemperament"/> on
    /// each card so the app can bucket a non-match into its "Other" section. The
    /// explicit <c>dogTemperaments</c> refinement is the hard filter.
    /// </summary>
    string? PetTemperament = null);

/// <summary>
/// Per-provider search hit. ServiceId is included so the mobile client can
/// go straight to the slots / booking endpoints without a follow-up lookup.
/// </summary>
/// <param name="BusinessName">
/// Display label for the provider: the shop/hotel/clinic business name for
/// businesses, or the provider's personal name (FirstName + LastName) for
/// freelance sub-categories, which have no business name in the offering doc.
/// Null only when neither can be resolved (e.g. missing profile row).
/// </param>
/// <param name="CompletedBookings">
/// Bookings already finished (not cancelled / no-show) across ALL the
/// provider's services.
/// </param>
/// <param name="Charges">
/// PricePerHour for DayCare/NightStay, the menu item's price for a grooming
/// search with ServiceItemCode, PricePerAppointment for vets. Null for a
/// grooming search without a ServiceItemCode.
/// </param>
/// <param name="ChargesUnit">PerHour | PerService | PerAppointment.</param>
/// <param name="ImageUrl">
/// The service image the provider uploaded for this offering (the same image
/// shown on the discovery card). Null when the provider hasn't set one.
/// </param>
/// <param name="BannerImageUrl">
/// The wide banner the provider uploaded for this specific service
/// (Provider.ProviderServiceBanners, keyed by ServiceId). Distinct from
/// ImageUrl (the offering/discovery photo). Null when no banner is set.
/// </param>
/// <param name="Description">
/// What the provider says about the service being searched: the menu item's
/// blurb for a grooming search with a ServiceItemCode, the session description
/// for trainers. Null for the other searches (no per-service text), and for a
/// grooming search without a code — no single item is being described.
/// </param>
/// <param name="PetCapacity">
/// How many pets the provider can take at once on THIS service — the offering's
/// capacity (maxPetsAtOneTime / maxConcurrentSessions / maxConcurrent
/// consultations), scoped by ServiceId, so day care and night stay report their
/// own buckets. Grooming capacity is shop-wide across the whole menu.
///
/// It is on the card because <c>sortBy=PetCapacity</c> is offered: a list the
/// parent has asked to order by a number should show them the number.
/// </param>
/// <param name="MatchesPetTemperament">
/// Does this provider take the temperament of the pet the search was filtered by?
/// <c>true</c> / <c>false</c> when the question can be answered, <c>null</c> when
/// it cannot — no pet was named, the pet has no temperament recorded, or the
/// provider's category holds no such list (vets, trainers, adoption and sale).
/// <c>false</c> covers both "they listed other temperaments" and "they listed
/// none", which is what keeps it in step with the hard <c>dogTemperaments</c>
/// filter. The provider is still RETURNED either way: this is a hint for the
/// app's "Other" bucket, not a filter. See
/// <see cref="PetTemperamentMatch"/>.
/// </param>
public sealed record ProviderSearchResult(
    Guid ProviderId,
    Guid ServiceId,
    string SubCategory,
    string? BusinessName,
    int CompletedBookings,
    decimal? Charges,
    string ChargesUnit,
    string? ServiceItemCode,
    string? ImageUrl,
    string? Description = null,
    string? BannerImageUrl = null,
    int PetCapacity = 0,
    bool? MatchesPetTemperament = null);

public static class ProviderSearchChargesUnits
{
    public const string PerHour = "PerHour";
    public const string PerService = "PerService";
    public const string PerAppointment = "PerAppointment";
    public const string PerSession = "PerSession";
}
