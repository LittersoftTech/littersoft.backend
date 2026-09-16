using Pawfront.Contracts.ParentOnboarding;

namespace Pawfront.Application.ParentOnboarding;

/// <summary>
/// The outcome of the SQL half of a pet-parent account delete (an anonymise +
/// disable, not a row delete). SQL cannot reach Blob Storage, so the sproc also
/// hands back the media the parent still owns there; the
/// <see cref="IParentAccountService"/> orchestrator finishes the cleanup.
///
/// Unlike the provider delete there is no Cosmos leg — a pet parent owns no
/// Cosmos document. The events they organised do have one, and those events (and
/// their documents) are deliberately retained.
///
/// <see cref="BlobUrls"/> is empty when the account was already deleted — the
/// sproc is idempotent and there is nothing left to clean up.
/// </summary>
public sealed record PetParentAccountDeletionResult(
    DeletePetParentAccountResponse Summary,
    // Profile photo, parent gallery, identity document, and the pets' profile +
    // gallery photos. Booking evidence and event banners are deliberately
    // absent: those belong to records the delete retains.
    IReadOnlyList<string> BlobUrls);

/// <summary>
/// One unfinished booking blocking a pet-parent account delete. Flat and
/// booking-kind-agnostic: <see cref="BookingType"/> ('SingleDay' | 'NightStay')
/// discriminates, and for a stay <see cref="ServiceDate"/> is the check-in date
/// with the times being drop-off / pick-up.
/// </summary>
/// <param name="CheckOutDate">
/// The stay's checkout day (exclusive — not a stayed night), which with
/// <see cref="ServiceDate"/> gives the number of nights billed. Null for a
/// single-day booking, whose duration comes from the two times instead.
/// </param>
/// <param name="SnapshotUnitPrice">
/// The unit rate frozen onto the booking when it was created (price-lock), which
/// is all SQL can supply. Null on a legacy row created before price-locking
/// shipped — <see cref="IPendingJobEnricher"/> falls back to the provider's live
/// offering for those, exactly as the booking-detail read does.
/// </param>
/// <param name="ProviderProfilePhotoUrl">
/// Filled by <see cref="IPendingJobEnricher"/>, not by SQL: the provider's photo
/// lives in their Cosmos offering document (the business image for a
/// shop/hotel/clinic, the freelancer's own image otherwise), which the
/// <c>Provider.Providers</c> row has no column for.
/// </param>
/// <param name="Price">
/// Also filled by <see cref="IPendingJobEnricher"/> — see <see cref="PendingJobPrice"/>.
/// </param>
public sealed record PendingParentJob(
    Guid BookingId,
    string BookingType,
    string JobId,
    Guid ProviderId,
    string? ProviderName,
    string ServiceCategory,
    string SubCategory,
    string Status,
    DateOnly ServiceDate,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    string? PetName,
    Guid ServiceId,
    string? ServiceItemCode,
    DateOnly? CheckOutDate,
    decimal? SnapshotUnitPrice,
    string? ProviderProfilePhotoUrl = null,
    PendingJobPrice? Price = null);

/// <summary>
/// What a pending job costs, in the same shape and by the same arithmetic as the
/// booking detail's payment block — so a parent settling a blocked delete sees the
/// figure they will see on the job itself.
/// </summary>
/// <remarks>
/// Every pending job here is an App booking (it was found by PetParentId, and a
/// Custom walk-in has none), so the commission always applies — unlike the booking
/// detail, which zeroes the fee for private jobs.
/// </remarks>
/// <param name="PricePerUnit">
/// The rate, price-locked at creation where the booking has one and read live from
/// the provider's current offering otherwise. Null when neither is resolvable —
/// a legacy row whose service has since been deactivated.
/// </param>
/// <param name="PriceUnit">
/// What the rate is per: <c>PerHour</c>, <c>PerNight</c>, <c>PerService</c>,
/// <c>PerAppointment</c> or <c>PerSession</c>. Always populated (it follows from
/// the booking kind and service category) even when the rate itself is not.
/// </param>
/// <param name="TotalAmount">
/// Rate times quantity for the services billed that way — hours for pet-sitter day
/// care, nights for a stay — and the flat fee for everything else. Null whenever
/// <see cref="PricePerUnit"/> is.
/// </param>
public sealed record PendingJobPrice(
    decimal? PricePerUnit,
    string PriceUnit,
    decimal? TotalAmount,
    decimal? PawfrontFee,
    decimal FeePercentage);

/// <summary>
/// Canonical <see cref="PendingJobPrice.PriceUnit"/> values. The four single-day
/// ones are the same strings the parent-facing search cards use for
/// <c>chargesUnit</c> (<c>ProviderSearchChargesUnits</c>), so the app learns one
/// vocabulary; <see cref="PerNight"/> is added here because a stay never appears
/// on those cards priced per night.
/// </summary>
public static class PendingJobPriceUnits
{
    public const string PerHour = "PerHour";
    public const string PerNight = "PerNight";
    public const string PerService = "PerService";
    public const string PerAppointment = "PerAppointment";
    public const string PerSession = "PerSession";
}
