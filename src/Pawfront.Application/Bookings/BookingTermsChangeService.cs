using System.Globalization;
using Pawfront.Application.Offerings;
using Pawfront.Application.Policies;
using Pawfront.Application.Providers;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Domain.Services;

namespace Pawfront.Application.Bookings;

/// <summary>
/// Diffs a booking's frozen terms against the provider's live ones. Read-only:
/// it composes the same readers the create and detail paths use, so "current"
/// here means exactly what a booking made right now would freeze.
/// </summary>
internal sealed class BookingTermsChangeService(
    IBookingSqlStore bookingStore,
    INightStayBookingSqlStore nightStayStore,
    IProviderOfferingResolver offeringResolver,
    IProviderPolicyService policyService,
    IPetSitterServiceRegistry petSitterRegistry,
    IProviderDiscoveryService providerDiscovery,
    IProviderServiceLocationRegistry providerLocationRegistry) : IBookingTermsChangeService
{
    public async Task<BookingTermsChangeResult> GetForBookingAsync(
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        var row = await bookingStore.GetDetailAsync(bookingId, cancellationToken)
            ?? throw new BookingNotFoundException(bookingId);

        var changes = new List<BookingTermsChangeItem>();

        // --- Price -----------------------------------------------------------
        // The live unit rate for this exact service: the chosen menu item's price
        // for a groomer, the offering's headline price otherwise. Resolving the
        // offering also gives the duration rule used further down.
        var offering = await TryResolveOfferingAsync(row.ServiceId, cancellationToken);
        var (livePrice, liveFixedDurationHours, liveMinimumDurationHours) =
            await ResolveLiveSingleDayTermsAsync(row, offering, cancellationToken);

        // A Custom walk-in's PricePerHour is the rate the provider typed in for
        // that one job, not the offering's — the two aren't comparable, and
        // adopting the offering rate would silently overwrite what they charged.
        // The rest of the terms (policy, duration rules) still apply.
        var isCustom = string.Equals(row.Source, "Custom", StringComparison.Ordinal);
        if (!isCustom)
        {
            AddPriceChange(changes, row.PricePerHour, livePrice, perUnitLabel: "hour");
        }

        // --- Cancellation policy ---------------------------------------------
        var livePolicyHours = await TryResolvePolicyHoursAsync(row.ProviderId, cancellationToken);
        AddCancellationPolicyChange(changes, row.CancellationPolicyHours, livePolicyHours);

        // --- Duration rules ---------------------------------------------------
        // Not an old-vs-new diff (the booking never froze a duration rule) but a
        // check of the booked window against the rule as it stands today: the
        // requester needs to know a 60-minute slot is now a fixed 90.
        var bookedHours = (decimal)(row.EndTime - row.StartTime).TotalHours;
        AddDurationChanges(changes, bookedHours, liveFixedDurationHours, liveMinimumDurationHours);

        // --- Selected-location address ---------------------------------------
        var liveLocation = await ResolveLiveLocationAsync(
            row.LocationType, row.ProviderId, row.ServiceCategory,
            row.ParentAddressLine, row.ParentCity, row.ParentZipCode,
            row.ParentLatitude, row.ParentLongitude, cancellationToken);
        var frozenLocation = BookingService.TrySnapshotLocation(
            row.LocationType, row.SnapshotAddressLine, row.SnapshotCity,
            row.SnapshotZipCode, row.SnapshotLatitude, row.SnapshotLongitude);
        AddLocationChange(changes, frozenLocation, liveLocation);

        var terms = new BookingAcknowledgedTerms(
            UnitPrice: isCustom ? row.PricePerHour : livePrice ?? row.PricePerHour,
            CancellationPolicyHours: livePolicyHours,
            DropOffTime: null,
            PickUpTime: null,
            AddressLine: liveLocation?.AddressLine ?? row.SnapshotAddressLine,
            City: liveLocation?.City ?? row.SnapshotCity,
            ZipCode: liveLocation?.ZipCode ?? row.SnapshotZipCode,
            Latitude: liveLocation?.Latitude ?? row.SnapshotLatitude,
            Longitude: liveLocation?.Longitude ?? row.SnapshotLongitude);

        return new BookingTermsChangeResult(bookingId, changes.Count > 0, changes, terms);
    }

    public async Task<BookingTermsChangeResult> GetForNightStayBookingAsync(
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        var row = await nightStayStore.GetDetailAsync(bookingId, cancellationToken)
            ?? throw new NightStayBookingNotFoundException(bookingId);

        var changes = new List<BookingTermsChangeItem>();

        // A stay reads its terms straight off the PetSitter NightStay branch —
        // the same branch the create path snapshots from.
        var nightStay = await TryResolveNightStayOfferingAsync(row.ProviderId, cancellationToken);

        AddPriceChange(changes, row.PricePerNight, nightStay?.PricePerHour, perUnitLabel: "night");

        var livePolicyHours = await TryResolvePolicyHoursAsync(row.ProviderId, cancellationToken);
        AddCancellationPolicyChange(changes, row.CancellationPolicyHours, livePolicyHours);

        // Drop-off / pick-up are frozen NOT NULL on the stay, so both sides always
        // have a value to compare whenever the offering still resolves.
        if (nightStay is not null && nightStay.DropOffTime != row.DropOffTime)
        {
            changes.Add(new BookingTermsChangeItem(
                BookingTermsChangeFields.DropOffTime,
                BookingTermsChangeTypes.ValueChanged,
                FormatTime(row.DropOffTime),
                FormatTime(nightStay.DropOffTime),
                $"The drop-off time changed from {FormatTime(row.DropOffTime)} to {FormatTime(nightStay.DropOffTime)}."));
        }

        if (nightStay is not null && nightStay.PickUpTime != row.PickUpTime)
        {
            changes.Add(new BookingTermsChangeItem(
                BookingTermsChangeFields.PickUpTime,
                BookingTermsChangeTypes.ValueChanged,
                FormatTime(row.PickUpTime),
                FormatTime(nightStay.PickUpTime),
                $"The pick-up time changed from {FormatTime(row.PickUpTime)} to {FormatTime(nightStay.PickUpTime)}."));
        }

        // Minimum nights: the NightStay branch's MinimumBookingHours is the stay's
        // minimum length in nights. The checkout day isn't a stayed night.
        var bookedNights = row.CheckOutDate.DayNumber - row.CheckInDate.DayNumber;
        if (nightStay is not null && nightStay.MinimumBookingHours > 0 && bookedNights < nightStay.MinimumBookingHours)
        {
            changes.Add(new BookingTermsChangeItem(
                BookingTermsChangeFields.MinimumNights,
                BookingTermsChangeTypes.RuleViolation,
                FormatNights(bookedNights),
                FormatNights(nightStay.MinimumBookingHours),
                $"This stay now has a minimum of {FormatNights(nightStay.MinimumBookingHours)}; "
                + $"your booking is {FormatNights(bookedNights)}."));
        }

        var liveLocation = await ResolveLiveLocationAsync(
            row.LocationType, row.ProviderId, row.ServiceCategory,
            row.ParentAddressLine, row.ParentCity, row.ParentZipCode,
            row.ParentLatitude, row.ParentLongitude, cancellationToken);
        var frozenLocation = BookingService.TrySnapshotLocation(
            row.LocationType, row.SnapshotAddressLine, row.SnapshotCity,
            row.SnapshotZipCode, row.SnapshotLatitude, row.SnapshotLongitude);
        AddLocationChange(changes, frozenLocation, liveLocation);

        var terms = new BookingAcknowledgedTerms(
            UnitPrice: nightStay?.PricePerHour ?? row.PricePerNight,
            CancellationPolicyHours: livePolicyHours,
            DropOffTime: nightStay?.DropOffTime ?? row.DropOffTime,
            PickUpTime: nightStay?.PickUpTime ?? row.PickUpTime,
            AddressLine: liveLocation?.AddressLine ?? row.SnapshotAddressLine,
            City: liveLocation?.City ?? row.SnapshotCity,
            ZipCode: liveLocation?.ZipCode ?? row.SnapshotZipCode,
            Latitude: liveLocation?.Latitude ?? row.SnapshotLatitude,
            Longitude: liveLocation?.Longitude ?? row.SnapshotLongitude);

        return new BookingTermsChangeResult(bookingId, changes.Count > 0, changes, terms);
    }

    // --- Per-field comparisons ------------------------------------------------

    /// <summary>
    /// A price change is only reportable when the booking actually froze a rate.
    /// Legacy rows (null snapshot) already read the live price through the
    /// detail-read fallback, so there is nothing the user would perceive as a change.
    /// </summary>
    private static void AddPriceChange(
        List<BookingTermsChangeItem> changes, decimal? frozen, decimal? live, string perUnitLabel)
    {
        if (frozen is null || live is null || frozen.Value == live.Value)
        {
            return;
        }

        changes.Add(new BookingTermsChangeItem(
            BookingTermsChangeFields.Price,
            BookingTermsChangeTypes.ValueChanged,
            FormatMoney(frozen.Value),
            FormatMoney(live.Value),
            $"The price changed from {FormatMoney(frozen.Value)} to {FormatMoney(live.Value)} per {perUnitLabel}."));
    }

    /// <summary>
    /// Null is a real value on both sides here — it means "no cancellation
    /// restriction" — so a set-to-unset or unset-to-set transition is a change.
    /// </summary>
    private static void AddCancellationPolicyChange(
        List<BookingTermsChangeItem> changes, int? frozen, int? live)
    {
        if (frozen == live)
        {
            return;
        }

        changes.Add(new BookingTermsChangeItem(
            BookingTermsChangeFields.CancellationPolicy,
            BookingTermsChangeTypes.ValueChanged,
            FormatPolicy(frozen),
            FormatPolicy(live),
            $"The cancellation policy changed from {FormatPolicy(frozen)} to {FormatPolicy(live)}."));
    }

    private static void AddDurationChanges(
        List<BookingTermsChangeItem> changes,
        decimal bookedHours,
        decimal? liveFixedDurationHours,
        decimal? liveMinimumDurationHours)
    {
        if (liveFixedDurationHours is decimal fixedHours && bookedHours != fixedHours)
        {
            changes.Add(new BookingTermsChangeItem(
                BookingTermsChangeFields.Duration,
                BookingTermsChangeTypes.RuleViolation,
                FormatHours(bookedHours),
                FormatHours(fixedHours),
                $"This service now runs for {FormatHours(fixedHours)}; your booking is {FormatHours(bookedHours)}. "
                + "Pick a window of the new length."));
        }

        if (liveMinimumDurationHours is decimal minimumHours && bookedHours < minimumHours)
        {
            changes.Add(new BookingTermsChangeItem(
                BookingTermsChangeFields.MinimumDuration,
                BookingTermsChangeTypes.RuleViolation,
                FormatHours(bookedHours),
                FormatHours(minimumHours),
                $"This service now has a minimum of {FormatHours(minimumHours)}; "
                + $"your booking is {FormatHours(bookedHours)}."));
        }
    }

    /// <summary>
    /// Reported only when the booking has a frozen address to compare against —
    /// a legacy row with no snapshot already resolves live on every read.
    /// </summary>
    private static void AddLocationChange(
        List<BookingTermsChangeItem> changes,
        BookingLocationResult? frozen,
        BookingLocationResult? live)
    {
        if (frozen is null || live is null)
        {
            return;
        }

        var same = string.Equals(frozen.AddressLine, live.AddressLine, StringComparison.Ordinal)
            && string.Equals(frozen.City, live.City, StringComparison.Ordinal)
            && string.Equals(frozen.ZipCode, live.ZipCode, StringComparison.Ordinal)
            && frozen.Latitude == live.Latitude
            && frozen.Longitude == live.Longitude;
        if (same)
        {
            return;
        }

        var bookedAddress = FormatAddress(frozen);
        var currentAddress = FormatAddress(live);
        changes.Add(new BookingTermsChangeItem(
            BookingTermsChangeFields.Location,
            BookingTermsChangeTypes.ValueChanged,
            bookedAddress,
            currentAddress,
            $"The service address changed from {bookedAddress} to {currentAddress}."));
    }

    // --- Live-value resolution ------------------------------------------------

    private async Task<OfferingResolution.Resolved?> TryResolveOfferingAsync(
        Guid serviceId, CancellationToken cancellationToken)
    {
        try
        {
            return await offeringResolver.ResolveAsync(serviceId, cancellationToken)
                as OfferingResolution.Resolved;
        }
        catch
        {
            // Best-effort throughout: a booking whose offering can no longer be
            // read simply reports no drift for the offering-sourced terms.
            return null;
        }
    }

    /// <summary>
    /// The offering-sourced terms for a single-day booking: the live unit rate and
    /// the duration rule, split into its fixed and minimum forms (a service has one
    /// or the other, never both). PetGroomer resolves both from the booked menu item.
    /// </summary>
    private async Task<(decimal? Price, decimal? FixedDurationHours, decimal? MinimumDurationHours)>
        ResolveLiveSingleDayTermsAsync(
            BookingDetailRow row,
            OfferingResolution.Resolved? offering,
            CancellationToken cancellationToken)
    {
        if (offering is null)
        {
            return (null, null, null);
        }

        if (offering.ServiceType == ProviderServiceTypes.GroomingSession)
        {
            if (string.IsNullOrWhiteSpace(row.ServiceItemCode))
            {
                return (null, null, null);
            }

            try
            {
                var item = await offeringResolver.ResolveGroomingItemAsync(
                    row.ProviderId, row.ServiceItemCode!, cancellationToken);
                // A code the provider has since dropped or disabled has no current
                // price or duration — the booking keeps what it froze.
                return item is GroomingItemResolution.Resolved resolved
                    ? (resolved.Price, (decimal)resolved.DurationMinutes / 60m, null)
                    : (null, null, null);
            }
            catch
            {
                return (null, null, null);
            }
        }

        return offering.IsDurationFixed
            ? (offering.Price, offering.DurationHours, null)
            : (offering.Price, null, offering.DurationHours);
    }

    private async Task<BoardingOfferingResult?> TryResolveNightStayOfferingAsync(
        Guid providerId, CancellationToken cancellationToken)
    {
        try
        {
            var doc = await petSitterRegistry.GetAsync(providerId, cancellationToken);
            return (doc?.PetHotel?.Offering ?? doc?.Freelance?.Offering)?.NightStay;
        }
        catch
        {
            return null;
        }
    }

    private async Task<int?> TryResolvePolicyHoursAsync(Guid providerId, CancellationToken cancellationToken)
    {
        try
        {
            var policy = await policyService.GetAsync(providerId, cancellationToken);
            return policy.MinimumHoursBeforeCancellation;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The address a booking made right now would freeze for this location type:
    /// the parent's current profile address, or the provider's current business
    /// address. Null when the booking has no location type (Custom walk-ins,
    /// legacy rows) — there is nothing to compare.
    /// </summary>
    private async Task<BookingLocationResult?> ResolveLiveLocationAsync(
        string? locationType,
        Guid providerId,
        string serviceCategory,
        string? parentAddressLine,
        string? parentCity,
        string? parentZipCode,
        decimal? parentLatitude,
        decimal? parentLongitude,
        CancellationToken cancellationToken)
    {
        switch (locationType)
        {
            case BookingLocationTypes.ParentLocation:
                return new BookingLocationResult(
                    BookingLocationTypes.ParentLocation,
                    parentAddressLine, parentCity, parentZipCode, parentLatitude, parentLongitude);

            case BookingLocationTypes.ProviderLocation:
                var summary = await BookingService.TryGetProviderSummaryAsync(
                    providerDiscovery, providerId, serviceCategory, cancellationToken);
                return await BookingService.ResolveProviderLocationAsync(
                    providerLocationRegistry, providerId, summary, cancellationToken);

            default:
                return null;
        }
    }

    // --- Display formatting ---------------------------------------------------

    private static string FormatMoney(decimal value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatPolicy(int? hours)
        => hours is null ? "no restriction" : $"{hours} hours";

    private static string FormatTime(TimeOnly value)
        => value.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string FormatHours(decimal hours)
        => hours == 1m
            ? "1 hour"
            : $"{hours.ToString("0.##", CultureInfo.InvariantCulture)} hours";

    private static string FormatNights(int nights)
        => nights == 1 ? "1 night" : $"{nights} nights";

    private static string FormatAddress(BookingLocationResult location)
    {
        var parts = new[] { location.AddressLine, location.City, location.ZipCode }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        var joined = string.Join(", ", parts);
        return string.IsNullOrEmpty(joined) ? "an unspecified address" : joined;
    }
}
