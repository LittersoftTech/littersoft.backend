namespace Pawfront.Contracts.Analytics;

/// <summary>
/// Body for <c>POST /providers/{providerId}/views</c> on the PET-PARENT host —
/// the capture behind the provider's PawPrints "Views" card.
/// </summary>
/// <remarks>
/// <para>
/// Every field is optional, but <c>serviceId</c> and <c>petId</c> are worth
/// sending and change what the provider can see.
/// </para>
/// <para>
/// <c>serviceId</c> is which service card the parent tapped — the same id the five
/// <c>/providers/search/*</c> cards return. Without it the view still counts
/// toward the total but appears in no per-service breakdown row, reported to the
/// provider as <c>unattributedViews</c>.
/// </para>
/// <para>
/// <c>petId</c> is the pet the parent is shopping for — the same <c>petId</c> the
/// searches filter by. It is what puts a pet name, breed and gender on the
/// provider's viewer card; without it those read null, because a parent with
/// several animals gives no honest answer and guessing would put a wrong breed in
/// front of a provider about to act on it. It must be one of the caller's own
/// non-deleted pets.
/// </para>
/// <para>
/// <c>source</c> is free text describing where the view came from (a search, the
/// browse list, a shared link), so a provider can tell an impression from a
/// deliberate visit. The vocabulary is the app's — an unrecognised value is stored
/// verbatim rather than refused, the same posture a support ticket's category
/// takes.
/// </para>
/// <para>
/// The caller's own PetParentId is taken from the JWT, never from the body. A
/// caller whose profile is not finished yet is still recorded (browsing before
/// onboarding completes is legitimate) but cannot appear in the viewer list.
/// </para>
/// </remarks>
public sealed record RecordProviderViewRequest(
    Guid? ServiceId = null,
    Guid? PetId = null,
    string? Source = null);

/// <summary>What was recorded, echoed back so a client can confirm it.</summary>
public sealed record ProviderViewRecordedResponse(
    Guid ProviderServiceViewId,
    Guid ProviderId,
    Guid? ServiceId,
    Guid? PetParentId,
    Guid? PetId,
    string? Source,
    DateTimeOffset ViewedAtUtc);

/// <summary>
/// The headline "Views" figure for a date range.
/// </summary>
/// <remarks>
/// <para>
/// <c>totalViews</c> counts every view; <c>uniqueViewers</c> counts distinct
/// parents. Both travel because the raw figure is noisy and gameable while the
/// distinct one is the robust read — which to show is a product decision.
/// </para>
/// <para>
/// <c>unattributedViews</c> and <c>anonymousViews</c> explain the two ways this
/// total can exceed what the drill-downs show, rather than leaving the gap looking
/// like an arithmetic error. Unattributed views named no service, so they are in
/// no <c>services[]</c> row; anonymous views came from a caller with no parent
/// profile, so they are in no viewer list. The two overlap freely — do NOT add
/// them together.
/// </para>
/// <para>
/// <c>uniqueViewers</c> is likewise NOT the sum of the per-service unique counts:
/// one parent who looked at day care and at boarding is one viewer and two
/// service-viewers. Both are correct; summing distinct counts never is.
/// </para>
/// </remarks>
public sealed record ProviderViewTotalsResponse(
    int TotalViews,
    int UniqueViewers,
    int UnattributedViews,
    int AnonymousViews,
    DateTimeOffset? LastViewedAtUtc);

/// <summary>
/// Views for one of the provider's services — the breakdown the dashboard card
/// expands into.
/// </summary>
/// <remarks>
/// A service with no views in range still appears with zeros: "nobody looked at
/// day care" is the fact worth acting on, whereas omitting the row would read as
/// "no such service". Deactivated services are included and flagged, because
/// their historical views are real and a provider comparing periods needs the
/// service they switched off to still be listed.
/// </remarks>
public sealed record ProviderServiceViewResponse(
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    string ServiceType,
    bool IsActive,
    int Views,
    int UniqueViewers,
    DateTimeOffset? LastViewedAtUtc);

/// <summary>
/// <c>GET /providers/{providerId}/analytics/views</c> — levels one and two of the
/// card in one round trip, so the total and the breakdown it expands cannot be
/// computed from two different instants.
/// </summary>
public sealed record ProviderViewSummaryResponse(
    string Period,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    ProviderViewTotalsResponse Totals,
    IReadOnlyList<ProviderServiceViewResponse> Services);

/// <summary>
/// One parent who viewed. <c>viewCount</c> is how many times they looked in range;
/// the row itself is one per PARENT, because six visits is one interested customer.
/// </summary>
/// <remarks>
/// The pet is the one they most recently viewed with, and every pet field is null
/// when none of their views named a pet — it is never filled in from whatever
/// animals they happen to own. <c>name</c> and <c>photoUrl</c> are resolved live,
/// so a parent who has deleted their account reads as "Deleted User".
/// </remarks>
public sealed record ProviderServiceViewerResponse(
    Guid PetParentId,
    string? Name,
    string? PhotoUrl,
    Guid? PetId,
    string? PetName,
    string? PetType,
    string? Breed,
    string? PetGender,
    int ViewCount,
    DateTimeOffset FirstViewedAtUtc,
    DateTimeOffset LastViewedAtUtc);

/// <summary>
/// <c>GET /providers/{providerId}/analytics/views/viewers</c> — level three.
/// <c>serviceId</c> echoes back the service the list was narrowed to (null for the
/// provider-wide list).
/// </summary>
public sealed record ProviderServiceViewersResponse(
    string Period,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    Guid? ServiceId,
    int TotalCount,
    int Skip,
    int Take,
    bool HasMore,
    IReadOnlyList<ProviderServiceViewerResponse> Viewers);

/// <summary>
/// Booking counts and money for one date range — the SAME shape for the provider's
/// totals and for each of their services, so one tile component renders at either
/// level of the drill-down.
/// </summary>
/// <remarks>
/// <para>
/// The money fields mean exactly what they mean on the earnings endpoints (gross
/// is what parents pay, <c>pawfrontFee</c> the commission owed back, <c>net</c>
/// what the provider keeps; each split into <c>received*</c> and
/// <c>awaiting*</c>), and are computed by the same SQL, so a card cannot disagree
/// with its own breakdown.
/// </para>
/// <para>
/// <c>completedBookings</c>, <c>upcomingBookings</c> and the unrealised trio
/// PARTITION the range, so those four sum to <c>totalBookings</c> — and each maps
/// to a status group <c>GET .../earnings/bookings?status=</c> accepts, so a client
/// can tap a figure and list exactly those rows.
/// </para>
/// <para>
/// Custom walk-ins are excluded from every figure except <c>privateJob*</c>,
/// consistent with every other platform figure. The unrealised amounts are what
/// the jobs WOULD have been worth and carry no fee, since a commission on money
/// that never moved is not owed.
/// </para>
/// </remarks>
public sealed record ProviderBookingFiguresResponse(
    int TotalBookings,
    int CompletedBookings,
    int PaidBookings,
    int AwaitingPaymentBookings,
    int UnpricedBookings,
    int UpcomingBookings,
    decimal GrossAmount,
    decimal PawfrontFee,
    decimal NetAmount,
    decimal ReceivedGross,
    decimal ReceivedFee,
    decimal ReceivedNet,
    decimal AwaitingGross,
    decimal AwaitingFee,
    decimal AwaitingNet,
    int PrivateJobCount,
    decimal PrivateJobAmount,
    int CancelledJobCount,
    decimal CancelledJobAmount,
    int NoShowJobCount,
    decimal NoShowJobAmount,
    int ExpiredJobCount,
    decimal ExpiredJobAmount,
    int UnrealisedJobCount,
    decimal UnrealisedAmount,
    // --- Added 2026-09-11, appended last -------------------------------------
    // Bookings still waiting on the provider to accept or decline (CREATED, plus
    // the deprecated APPROVAL_NEEDED).
    int PendingBookings,
    // Bookings the provider ACCEPTED and which did not fall through —
    // confirmed-equivalent, underway and finished alike. pendingBookings +
    // acceptedBookings + unrealisedJobCount partition the platform side of the
    // range. To list exactly these rows call the bookings list with
    // ?status=Accepted,InProgress,ModificationRequest,Completed.
    int AcceptedBookings,
    // Custom walk-ins the provider recorded and has not cancelled. Unlike
    // privateJobCount (which feeds a money figure and is gated on the job being
    // finished), this counts work taken on.
    int PrivateAcceptedJobs,
    // Platform + private together — what the dashboard cards show. See the type.
    Pawfront.Contracts.Earnings.ProviderFiguresIncludingPrivateResponse IncludingPrivate);

/// <summary>
/// One of the provider's services, with its bookings and its money. Services with
/// nothing in range still appear with zeros; deactivated ones are included and
/// flagged.
/// </summary>
public sealed record ProviderServiceBookingResponse(
    Guid ServiceId,
    string ServiceCategory,
    string SubCategory,
    string ServiceType,
    bool IsActive,
    ProviderBookingFiguresResponse Figures);

/// <summary>
/// <c>GET /providers/{providerId}/analytics/bookings</c> — the "Bookings" and
/// "Earnings" cards' first two levels. One call returns counts AND money because
/// they come from the same rows; each card renders the half it shows.
/// </summary>
/// <remarks>
/// Unlike the views summary, <c>services[]</c> sums EXACTLY to <c>totals</c>: a
/// booking always names a service, so every booking lands in exactly one row.
/// </remarks>
public sealed record ProviderBookingBreakdownResponse(
    string Period,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    ProviderBookingFiguresResponse Totals,
    IReadOnlyList<ProviderServiceBookingResponse> Services);

/// <summary>
/// <c>GET /providers/{providerId}/earnings?period=</c> — the period-scoped
/// earnings totals, now with the per-service breakdown behind them.
/// </summary>
/// <remarks>
/// <para>
/// Additive: <c>period</c> / <c>periodStart</c> / <c>periodEnd</c> / <c>totals</c>
/// are byte-for-byte what this endpoint already returned, and <c>services</c> is
/// the only new key — the endpoint previously returned totals only, with no way to
/// see which service produced them.
/// </para>
/// <para>
/// <c>totals</c> is still read from <c>Booking.GetProviderEarningsSummary</c>, the
/// same procedure <c>/earnings/overview</c> reads, so the two endpoints cannot
/// disagree; <c>services</c> comes from the per-service procedure. Both sit on the
/// same underlying amounts function, so the rows sum to the totals.
/// </para>
/// <para>
/// <c>/earnings/overview</c> deliberately does NOT gain this: it carries four
/// periods at once, and a per-service breakdown of each is a different screen.
/// Call <c>/analytics/bookings</c> for the period the provider drills into.
/// </para>
/// </remarks>
public sealed record ProviderEarningsPeriodWithServicesResponse(
    string Period,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    Pawfront.Contracts.Earnings.ProviderEarningsTotalsResponse Totals,
    IReadOnlyList<ProviderServiceBookingResponse> Services);
