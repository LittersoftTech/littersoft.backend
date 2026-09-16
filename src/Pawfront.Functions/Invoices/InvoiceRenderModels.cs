namespace Pawfront.Functions.Invoices;

/// <summary>
/// Everything SQL knows about a paid booking that either invoice needs.
///
/// One shape for both documents on purpose: they describe the same job from two
/// sides and their figures must agree, so they are assembled from ONE read rather
/// than two.
/// </summary>
/// <param name="EndDate">Night-stay check-out; null for a single-day booking.</param>
/// <param name="UnitPrice">
/// The price-locked rate — per hour for a single-day booking, per night for a
/// stay. Frozen at creation, so it is the rate the parent agreed to and not
/// whatever the offering says today.
/// </param>
public sealed record InvoiceBookingFacts(
    Guid BookingId,
    string BookingType,
    string JobId,
    Guid ProviderId,
    Guid PetParentId,
    string ServiceCategory,
    string SubCategory,
    string? ServiceItemCode,
    DateOnly ServiceDate,
    DateOnly? EndDate,
    TimeOnly StartTime,
    TimeOnly EndTime,
    decimal? UnitPrice,
    string? ProviderFirstName,
    string? ProviderLastName,
    string? ProviderMobileCountryCode,
    string? ProviderMobileNumber,
    string? ProviderEmail,
    string? ParentFirstName,
    string? ParentLastName,
    string? ParentAddressLine,
    string? ParentCity,
    string? ParentZipCode,
    string? ParentEmail,
    string? PetName,
    string? PetType,
    string? PetBreed,
    DateTimeOffset? PaidAtUtc,
    string? PaymentMethod,
    // The bookable service's type (DayCare / NightStay / GroomingSession /
    // TrainingSession / VetAppointment). Needed to name the line item, and a
    // join away from the booking row rather than on it.
    string? ServiceType)
{
    public bool IsNightStay =>
        string.Equals(BookingType, "NightStay", StringComparison.Ordinal);

    /// <summary>
    /// Nights for a stay, hours for a single-day booking. This is the quantity the
    /// line item multiplies the locked rate by — and it mirrors the arithmetic in
    /// <c>BookingService.GetDetailAsync</c> and <c>Booking.BookingAmounts</c>,
    /// which is why it is derived here rather than passed in.
    /// </summary>
    public decimal Quantity => IsNightStay
        ? Math.Max(1, (EndDate ?? ServiceDate).DayNumber - ServiceDate.DayNumber)
        : (decimal)(EndTime.ToTimeSpan() - StartTime.ToTimeSpan()).TotalHours;
}

/// <summary>One invoice row awaiting a render, as claimed from SQL.</summary>
public sealed record ClaimedInvoice(
    Guid InvoiceId,
    string InvoiceNumber,
    string Recipient,
    decimal Amount,
    decimal PawfrontFee,
    DateTimeOffset IssuedAtUtc,
    int AttemptCount);

/// <summary>The result of claiming a booking's invoices: the shared facts + the rows.</summary>
public sealed record InvoiceClaim(
    InvoiceBookingFacts? Facts,
    IReadOnlyList<ClaimedInvoice> Invoices);

/// <summary>
/// The provider's business identity, from their Cosmos offering document. All of
/// it is best-effort: a provider mid-onboarding has no offering, and an invoice
/// still has to render — with the person's own name in place of a business one.
/// </summary>
public sealed record InvoiceProviderIdentity(
    string? BusinessName,
    string? AddressLine,
    string? City,
    string? Zip)
{
    public static readonly InvoiceProviderIdentity Unknown = new(null, null, null, null);
}
