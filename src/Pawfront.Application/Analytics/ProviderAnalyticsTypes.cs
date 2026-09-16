using Pawfront.Application.Earnings;

namespace Pawfront.Application.Analytics;

/// <summary>
/// One pet parent looking at one provider, as recorded by the parent host.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ServiceId"/> is which of the provider's bookable services was being
/// looked at, and is legitimately null: only the five <c>/providers/search/*</c>
/// cards carry a ServiceId, so a parent arriving from the generic discovery list
/// has chosen a provider rather than a service. Such a view still happened and is
/// still counted — see <see cref="ProviderViewTotals.UnattributedViews"/>.
/// </para>
/// <para>
/// <see cref="PetParentId"/> is null when the caller has no completed parent
/// profile. Browsing before onboarding finishes is legitimate on that host, so the
/// view is recorded rather than refused; it simply cannot appear in the viewer
/// list.
/// </para>
/// <para>
/// <see cref="PetId"/> is the pet the parent was SHOPPING FOR — the <c>petId</c>
/// the searches already filter by — not a guess at which animal they own. It is
/// what puts a breed and gender on the provider's viewer card, and is null when
/// the app named no pet.
/// </para>
/// </remarks>
public sealed record RecordProviderViewCommand(
    Guid ProviderId,
    Guid? ServiceId,
    Guid? PetParentId,
    Guid? PetId,
    string? Source);

/// <summary>The stored view, echoed back so a client can confirm what was recorded.</summary>
public sealed record ProviderViewRecord(
    Guid ProviderServiceViewId,
    Guid ProviderId,
    Guid? ServiceId,
    Guid? PetParentId,
    Guid? PetId,
    string? Source,
    DateTimeOffset ViewedAtUtc);

/// <summary>
/// The headline "Views" figure for a date range — level one of the card.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TotalViews"/> counts every view; <see cref="UniqueViewers"/> counts
/// distinct parents. Both travel because the raw figure is noisy and gameable
/// while the distinct one is the robust read, and which of the two a screen wants
/// is a product decision rather than a storage one.
/// </para>
/// <para>
/// <see cref="UnattributedViews"/> and <see cref="AnonymousViews"/> exist so the
/// two ways this total can exceed what the drill-downs show are stated rather than
/// left looking like arithmetic errors — the same posture
/// <see cref="ProviderEarningsTotals.UnpricedBookings"/> takes. Unattributed views
/// named no service and so are absent from the per-service breakdown; anonymous
/// views came from a caller with no parent profile and so are absent from the
/// viewer list. The two overlap freely and must not be added together.
/// </para>
/// <para>
/// <see cref="UniqueViewers"/> is NOT the sum of the per-service unique counts
/// either: a parent who looked at day care and at boarding is one viewer and two
/// service-viewers. Both answers are correct; summing distinct counts never is.
/// </para>
/// </remarks>
public sealed record ProviderViewTotals(
    int TotalViews,
    int UniqueViewers,
    int UnattributedViews,
    int AnonymousViews,
    DateTimeOffset? LastViewedAtUtc)
{
    /// <summary>An all-zero total, for a provider nobody has looked at in range.</summary>
    public static ProviderViewTotals Empty { get; } = new(0, 0, 0, 0, null);
}

/// <summary>
/// Views for one of the provider's services — level two of the card.
/// </summary>
/// <remarks>
/// A service with no views in range still appears, with zeros: "nobody looked at
/// day care" is the fact the provider needs to act on, whereas omitting the row
/// would read as "no such service". Deactivated services are included and flagged
/// via <see cref="IsActive"/>, because their historical views are real and a
/// provider comparing two periods needs the service they switched off to still be
/// in the list.
/// </remarks>
public sealed record ProviderServiceViewBreakdown(
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    string ServiceType,
    bool IsActive,
    int Views,
    int UniqueViewers,
    DateTimeOffset? LastViewedAtUtc);

/// <summary>
/// Levels one and two together: the range, its headline figures, and the
/// per-service breakdown. One result rather than two calls, so the card and the
/// breakdown it expands into cannot be computed from two different instants.
/// </summary>
public sealed record ProviderViewSummary(
    EarningsPeriod Period,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    ProviderViewTotals Totals,
    IReadOnlyList<ProviderServiceViewBreakdown> Services);

/// <summary>
/// One parent who viewed — level three of the card, and the reason views are
/// stored as rows rather than counters.
/// </summary>
/// <remarks>
/// <para>
/// One row per PARENT, not per view: the question the screen asks is "which
/// customers are interested", and somebody who opened the profile six times is one
/// interested customer. <see cref="ViewCount"/> carries the six.
/// </para>
/// <para>
/// The pet comes from the parent's most recent view IN RANGE that named one, and
/// every pet field is null when none did. It is deliberately not filled in from
/// whatever pets the parent happens to own: with several animals there is no
/// honest answer, and a wrong breed on a card the provider is about to act on is
/// worse than a blank one.
/// </para>
/// <para>
/// Names and photos are resolved live, so a parent who has deleted their account
/// reads as "Deleted User" here rather than leaving real personal data behind in a
/// provider's analytics.
/// </para>
/// </remarks>
public sealed record ProviderServiceViewerRow(
    Guid PetParentId,
    string? ParentName,
    string? ParentPhotoUrl,
    Guid? PetId,
    string? PetName,
    string? PetType,
    string? Breed,
    string? PetGender,
    int ViewCount,
    DateTimeOffset FirstViewedAtUtc,
    DateTimeOffset LastViewedAtUtc);

/// <summary>
/// Filter + paging arguments for the viewer list. <see cref="ServiceId"/> narrows
/// to the service whose breakdown row the provider tapped; omit it for the
/// provider-wide list.
/// </summary>
public sealed record ProviderServiceViewerQuery(
    Guid ProviderId,
    Guid? ServiceId,
    EarningsPeriod Period,
    DateOnly? FromDate,
    DateOnly? ToDate,
    int Skip,
    int Take);

/// <summary>
/// Booking counts and money for one date range — used BOTH for a provider's
/// totals and for each of their services, so the same tile renders at either
/// level of the drill-down.
/// </summary>
/// <remarks>
/// <para>
/// The money fields mean exactly what they mean on
/// <see cref="ProviderEarningsTotals"/> (gross / fee / net, each split into
/// received and awaiting), and are computed by the same SQL expressions, so a
/// dashboard card cannot disagree with its own breakdown.
/// </para>
/// <para>
/// <see cref="TotalBookings"/> and <see cref="UpcomingBookings"/> are the two
/// figures the earnings summary has no need of: it reports only what money did,
/// whereas a "Bookings" card has to account for jobs still in flight.
/// <see cref="CompletedBookings"/>, <see cref="UpcomingBookings"/> and the
/// unrealised trio partition the range, so those four sum to
/// <see cref="TotalBookings"/> — and each maps to a status group the bookings list
/// accepts, so a client can tap a figure and list exactly those rows.
/// </para>
/// <para>
/// Custom walk-ins are excluded from every figure except
/// <see cref="PrivateJobCount"/> / <see cref="PrivateJobAmount"/>, consistent with
/// every other platform figure in the product.
/// </para>
/// </remarks>
public sealed record ProviderBookingFigures(
    int TotalBookings,
    int CompletedBookings,
    int PaidBookings,
    int AwaitingPaymentBookings,
    int UnpricedBookings,
    int UpcomingBookings,
    decimal GrossAmount,
    decimal PawfrontFee,
    decimal ReceivedGross,
    decimal ReceivedFee,
    decimal AwaitingGross,
    decimal AwaitingFee,
    int PrivateJobCount,
    decimal PrivateJobAmount,
    int CancelledJobCount,
    decimal CancelledJobAmount,
    int NoShowJobCount,
    decimal NoShowJobAmount,
    int ExpiredJobCount,
    decimal ExpiredJobAmount)
{
    /// <summary>
    /// Marketplace bookings still waiting on the provider to accept or decline
    /// (CREATED, plus the deprecated APPROVAL_NEEDED).
    /// </summary>
    /// <remarks>
    /// Broken out because a request nobody has answered is not work the provider
    /// has taken on, and a dashboard card counting "my jobs" that moves the moment
    /// a parent taps Book is reporting demand, not workload.
    /// </remarks>
    public int PendingBookings { get; init; }

    /// <summary>
    /// Marketplace bookings the provider accepted and which did not then fall
    /// through — confirmed-equivalent, underway, and finished alike.
    /// </summary>
    /// <remarks>
    /// Defined as the complement of <see cref="PendingBookings"/> and the
    /// unrealised trio, so those three partition the platform side of the range
    /// and sum to <see cref="TotalBookings"/>. To list exactly these rows, call
    /// the bookings list with
    /// <c>?status=Accepted,InProgress,ModificationRequest,Completed</c>.
    /// </remarks>
    public int AcceptedBookings { get; init; }

    /// <summary>
    /// Custom walk-ins the provider recorded and has not cancelled.
    /// </summary>
    /// <remarks>
    /// A walk-in is CONFIRMED from the moment it is created — there is nobody to
    /// accept it — so "accepted" here means only "not cancelled". Distinct from
    /// <see cref="PrivateJobCount"/>, which is gated on the job being finished
    /// because it feeds a money figure; this one feeds a job count.
    /// </remarks>
    public int PrivateAcceptedJobs { get; init; }

    /// <summary>What the provider keeps: gross less the platform fee.</summary>
    public decimal NetAmount => GrossAmount - PawfrontFee;

    /// <summary>Net on bookings already marked paid.</summary>
    public decimal ReceivedNet => ReceivedGross - ReceivedFee;

    /// <summary>Net still owed on completed-but-unpaid bookings.</summary>
    public decimal AwaitingNet => AwaitingGross - AwaitingFee;

    /// <summary>Cancelled + no-show + expired, counted once (the buckets are disjoint).</summary>
    public int UnrealisedJobCount => CancelledJobCount + NoShowJobCount + ExpiredJobCount;

    /// <summary>What the unrealised jobs would have been worth. Never part of gross.</summary>
    public decimal UnrealisedAmount => CancelledJobAmount + NoShowJobAmount + ExpiredJobAmount;

    /// <summary>
    /// The same range counted and priced the way the provider's own dashboard
    /// cards ask about it: platform work and private walk-ins together.
    /// </summary>
    /// <remarks>
    /// Every other figure on this record keeps Custom walk-ins out, which is right
    /// for platform accounting — Pawfront takes no commission on one and it can
    /// never be marked paid. It is wrong for the two cards a provider actually
    /// looks at, where a job they did for cash off-platform is still a job they
    /// did and money they hold.
    /// </remarks>
    public ProviderFiguresIncludingPrivate IncludingPrivate =>
        new(AcceptedJobs: AcceptedBookings + PrivateAcceptedJobs,
            CompletedJobs: CompletedBookings + PrivateJobCount,
            GrossAmount: GrossAmount + PrivateJobAmount,
            NetAmount: NetAmount + PrivateJobAmount);

    /// <summary>An all-zero figure set, for a service with nothing in range.</summary>
    public static ProviderBookingFigures Empty { get; } =
        new(0, 0, 0, 0, 0, 0, 0m, 0m, 0m, 0m, 0m, 0m, 0, 0m, 0, 0m, 0, 0m, 0, 0m);
}

/// <summary>One of the provider's services, with its bookings and its money.</summary>
/// <remarks>
/// Like the views breakdown, a service with nothing in range still appears with
/// zeros, and deactivated services are included and flagged.
/// </remarks>
public sealed record ProviderServiceBookingBreakdown(
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    string ServiceType,
    bool IsActive,
    ProviderBookingFigures Figures);

/// <summary>
/// The "Bookings" and "Earnings" cards' first two levels: the range, the totals,
/// and one row per service.
/// </summary>
/// <remarks>
/// Unlike the views summary, the breakdown here sums EXACTLY to the totals: a
/// booking's ServiceId is non-null and foreign-keyed, so every booking lands in
/// exactly one bucket. A mismatch is a bug, not a modelled gap.
/// </remarks>
public sealed record ProviderBookingBreakdown(
    EarningsPeriod Period,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    ProviderBookingFigures Totals,
    IReadOnlyList<ProviderServiceBookingBreakdown> Services);
