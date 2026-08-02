using System.Collections.Concurrent;
using Pawfront.Application.Bookings;

namespace Pawfront.Infrastructure.Sql.Bookings;

/// <summary>
/// In-memory implementation of <see cref="INightStayBookingSqlStore"/> for the dev
/// fallback path (no SQL conn string + Key Vault disabled). Mirrors the SQL sproc
/// behaviour for the happy path plus the typed exceptions; the per-night capacity
/// check is race-safe via a per-service async lock instead of UPDLOCK + HOLDLOCK.
/// </summary>
internal sealed class InMemoryNightStayBookingStore : INightStayBookingSqlStore
{
    private readonly ConcurrentDictionary<Guid, Row> bookings = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> serviceLocks = new();
    private readonly ConcurrentDictionary<Guid, List<BookingStatusHistoryEntry>> history = new();

    private static bool IsActive(Row row) => !BookingStatuses.Cancelled.Contains(row.Status);

    /// <summary>
    /// Active stays occupying the given night on this service
    /// (CheckInDate &lt;= night &lt; CheckOutDate). Consumed by
    /// <see cref="InMemoryBookingStore.GetBookingsForDateAsync"/> so the slot
    /// service sees night-stay occupancy — the in-memory mirror of the
    /// NightStayBookings branch in [Booking].[GetBookingsForDate].
    /// </summary>
    internal int CountActiveStaysCoveringNight(Guid serviceId, DateOnly night) =>
        bookings.Values.Count(b =>
            b.ServiceId == serviceId
            && IsActive(b)
            && b.CheckInDate <= night
            && b.CheckOutDate > night);

    /// <summary>
    /// The identified form of <see cref="CountActiveStaysCoveringNight"/>, read by
    /// <see cref="InMemoryBookingStore.GetAgendaForDateAsync"/> — the in-memory
    /// mirror of the NightStayBookings branch in [Booking].[GetAgendaForDate].
    /// The dev store has no JobNumber IDENTITY, so job numbers come back as 0.
    /// </summary>
    internal IReadOnlyList<AgendaBookingRow> ListActiveStaysCoveringNight(Guid serviceId, DateOnly night) =>
        bookings.Values
            .Where(b =>
                b.ServiceId == serviceId
                && IsActive(b)
                && b.CheckInDate <= night
                && b.CheckOutDate > night)
            .Select(b => new AgendaBookingRow(
                "NightStay",
                b.NightStayBookingId,
                JobNumber: 0,
                b.PetParentId,
                TimeOnly.MinValue,
                new TimeOnly(23, 59, 59),
                b.Status))
            .ToArray();

    public Task<IReadOnlyDictionary<DateOnly, int>> GetNightlyOccupancyAsync(
        Guid serviceId,
        DateOnly fromNight,
        DateOnly toNight,
        CancellationToken cancellationToken)
    {
        var occupancy = new Dictionary<DateOnly, int>();
        for (var night = fromNight; night <= toNight; night = night.AddDays(1))
        {
            occupancy[night] = CountActiveStaysCoveringNight(serviceId, night);
        }
        return Task.FromResult<IReadOnlyDictionary<DateOnly, int>>(occupancy);
    }

    public async Task<NightStayBookingResult> CreateAsync(
        Guid providerId,
        Guid petParentId,
        Guid? petId,
        Guid serviceId,
        string serviceCategory,
        string subCategory,
        DateOnly checkInDate,
        DateOnly checkOutDate,
        TimeOnly dropOffTime,
        TimeOnly pickUpTime,
        string? jobNotes,
        string? locationType,
        decimal? pricePerNight,
        ProviderAddressSnapshot? providerAddress,
        int capacity,
        CancellationToken cancellationToken)
    {
        var serviceLock = serviceLocks.GetOrAdd(serviceId, _ => new SemaphoreSlim(1, 1));
        await serviceLock.WaitAsync(cancellationToken);
        try
        {
            // Reject a duplicate stay for the same pet (overlapping date range) —
            // mirrors THROW 51239 in [Booking].[CreateNightStayBooking].
            if (petId is not null && bookings.Values.Any(b =>
                    b.ServiceId == serviceId
                    && b.PetId == petId
                    && IsActive(b)
                    && b.CheckInDate < checkOutDate
                    && b.CheckOutDate > checkInDate))
            {
                throw new NightStayPetAlreadyBookedException(petId.Value, serviceId, checkInDate, checkOutDate);
            }

            // Reject if any stayed night already has @capacity active overlapping stays.
            for (var night = checkInDate; night < checkOutDate; night = night.AddDays(1))
            {
                var occupied = bookings.Values.Count(b =>
                    b.ServiceId == serviceId
                    && IsActive(b)
                    && b.CheckInDate <= night
                    && b.CheckOutDate > night);

                if (occupied >= capacity)
                {
                    throw new NightStayCapacityExceededException(serviceId, checkInDate, checkOutDate);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var row = new Row
            {
                NightStayBookingId = Guid.NewGuid(),
                ProviderId = providerId,
                PetParentId = petParentId,
                PetId = petId,
                ServiceId = serviceId,
                ServiceCategory = serviceCategory,
                SubCategory = subCategory,
                CheckInDate = checkInDate,
                CheckOutDate = checkOutDate,
                DropOffTime = dropOffTime,
                PickUpTime = pickUpTime,
                JobNotes = jobNotes,
                LocationType = locationType,
                PricePerNight = pricePerNight,
                // Dev fallback: ParentLocation isn't joined in-memory (stays null,
                // falls back to live on read); ProviderLocation snapshots the address.
                SnapshotAddressLine = providerAddress?.AddressLine,
                SnapshotCity = providerAddress?.City,
                SnapshotZipCode = providerAddress?.ZipCode,
                SnapshotLatitude = providerAddress?.Latitude,
                SnapshotLongitude = providerAddress?.Longitude,
                Status = BookingStatuses.Created,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CancelledAtUtc = null
            };

            bookings[row.NightStayBookingId] = row;
            AppendHistory(row.NightStayBookingId, null, row.Status, "System", null, "Night stay booking created");
            return ToResult(row);
        }
        finally
        {
            serviceLock.Release();
        }
    }

    public Task<NightStayBookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        bookings.TryGetValue(bookingId, out var row);
        return Task.FromResult(row is null ? null : (NightStayBookingResult?)ToResult(row));
    }

    public Task<NightStayBookingDetailRow?> GetDetailAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        bookings.TryGetValue(bookingId, out var row);
        if (row is null)
        {
            return Task.FromResult<NightStayBookingDetailRow?>(null);
        }

        // Dev fallback: no JobNumber / payout / pet-parent data in memory — leave
        // them at defaults / null (same posture as the in-memory event store).
        return Task.FromResult<NightStayBookingDetailRow?>(new NightStayBookingDetailRow(
            row.NightStayBookingId, JobNumber: 0, row.ProviderId, row.PetParentId, row.ServiceId,
            row.ServiceCategory, row.SubCategory, row.CheckInDate, row.CheckOutDate,
            row.DropOffTime, row.PickUpTime, row.Status, row.CreatedAtUtc, row.UpdatedAtUtc,
            row.CancelledAtUtc, row.PetId, PayoutStatus: "Pending", PayoutId: null,
            ParentFirstName: null, ParentLastName: null, ParentGender: null,
            ParentMobileCountryCode: null, ParentMobileNumber: null, ParentPhotoUrl: null,
            PetProfileName: null, PetType: null, PetGender: null, PetPhotoUrl: null,
            ProviderFirstName: null, ProviderLastName: null, ProviderGender: null,
            ProviderMobileCountryCode: null, ProviderMobileNumber: null,
            PetBreed: null, PetVaccinationStatus: null, PetVaccinationType: null,
            PetVaccinationDose: null, PetPrescription: null,
            JobNotes: row.JobNotes, LocationType: row.LocationType,
            // Dev fallback: no provider cancellation policy is resolved in-memory.
            CancellationPolicyHours: null,
            SnapshotAddressLine: row.SnapshotAddressLine,
            SnapshotCity: row.SnapshotCity,
            SnapshotZipCode: row.SnapshotZipCode,
            SnapshotLatitude: row.SnapshotLatitude,
            SnapshotLongitude: row.SnapshotLongitude));
    }

    public Task<NightStayBookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        if (!bookings.TryGetValue(bookingId, out var row))
        {
            throw new NightStayBookingNotFoundException(bookingId);
        }

        if (row.PetParentId != petParentId)
        {
            throw new NightStayBookingCancellationForbiddenException(bookingId);
        }

        if (BookingStatuses.Cancelled.Contains(row.Status))
        {
            throw new NightStayBookingAlreadyCancelledException(bookingId);
        }

        var now = DateTimeOffset.UtcNow;
        var from = row.Status;
        row.Status = BookingStatuses.ParentCancelled;
        row.CancelledAtUtc = now;
        row.UpdatedAtUtc = now;
        AppendHistory(bookingId, from, row.Status, "Parent", petParentId, null);

        return Task.FromResult(ToResult(row));
    }

    public Task<NightStayBookingResult> UpdateStatusAsync(
        Guid bookingId,
        string newStatus,
        BookingStatusActor actor,
        Guid actorId,
        string? note,
        CancellationToken cancellationToken)
    {
        if (!bookings.TryGetValue(bookingId, out var row))
        {
            throw new NightStayBookingNotFoundException(bookingId);
        }

        var ownsBooking = actor == BookingStatusActor.Provider
            ? row.ProviderId == actorId
            : row.PetParentId == actorId;
        if (!ownsBooking)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }

        // A booking left pending (CREATED) for 24+ hours has expired: reject the
        // attempted transition — mirror of the SQL sproc's guard (THROW 51249).
        // Reject only; the EXPIRED status is written by the scheduled external
        // job, which this dev fallback has no equivalent of, so the row simply
        // stays in CREATED.
        if (row.Status == BookingStatuses.Created
            && DateTimeOffset.UtcNow >= row.CreatedAtUtc.AddHours(24))
        {
            throw BookingExpiredException.NeverAccepted(bookingId);
        }

        // BR-53: still CREATED with under 2 hours to check-in + drop-off —
        // mirror of the sproc's THROW 51273, and reject-only for the same reason.
        if (row.Status == BookingStatuses.Created
            && BookingLeadTime.IsTooSoon(row.CheckInDate, row.DropOffTime, DateTimeOffset.UtcNow))
        {
            throw BookingExpiredException.ServiceTooClose(bookingId);
        }

        var allowed = actor == BookingStatusActor.Provider
            ? BookingStatuses.ProviderSettable
            : BookingStatuses.ParentSettable;
        if (!allowed.Contains(newStatus))
        {
            throw new BookingStatusNotAllowedException(newStatus, actor);
        }

        if (BookingStatuses.Terminal.Contains(row.Status))
        {
            throw new BookingStatusTerminalException(bookingId, row.Status);
        }

        if (row.Status == newStatus)
        {
            throw new BookingStatusUnchangedException(bookingId, newStatus);
        }

        var now = DateTimeOffset.UtcNow;
        if (BookingStatuses.NoShow.Contains(newStatus))
        {
            if (!BookingStatuses.NoShowReportableFrom.Contains(row.Status))
            {
                throw new BookingNotStartableException(bookingId);
            }

            var startsAtUtc = new DateTimeOffset(row.CheckInDate.ToDateTime(row.DropOffTime), TimeSpan.Zero);
            if (now < startsAtUtc.AddMinutes(30))
            {
                throw new BookingNoShowTooEarlyException(bookingId);
            }
        }

        var from = row.Status;
        row.Status = newStatus;
        row.UpdatedAtUtc = now;
        if (newStatus is BookingStatuses.ProviderCancelled or BookingStatuses.ParentCancelled)
        {
            row.CancelledAtUtc = now;
        }
        AppendHistory(bookingId, from, newStatus, actor.ToString(), actorId, note);

        return Task.FromResult(ToResult(row));
    }

    public Task<IReadOnlyList<NightStayBookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? onDate,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NightStayBookingResult> list = bookings.Values
            .Where(b => b.ProviderId == providerId)
            .Where(b => onDate is null || (onDate.Value >= b.CheckInDate && onDate.Value < b.CheckOutDate))
            .OrderByDescending(b => b.CheckInDate)
            .ThenByDescending(b => b.CheckOutDate)
            .Select(ToResult)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<NightStayBookingListItemResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        // Dev fallback: no cancellation policy is resolved in-memory; the location
        // block carries whatever snapshot the create captured (provider address).
        IReadOnlyList<NightStayBookingListItemResult> list = bookings.Values
            .Where(b => b.PetParentId == petParentId)
            .OrderByDescending(b => b.CheckInDate)
            .ThenByDescending(b => b.CheckOutDate)
            .Select(b => new NightStayBookingListItemResult(
                ToResult(b),
                PricePerNight: b.PricePerNight,
                CancellationPolicyHours: null,
                Location: new BookingLocationResult(
                    b.LocationType, b.SnapshotAddressLine, b.SnapshotCity,
                    b.SnapshotZipCode, b.SnapshotLatitude, b.SnapshotLongitude)))
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<BookingStatusHistoryEntry>> ListStatusHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        if (!history.TryGetValue(bookingId, out var list))
        {
            return Task.FromResult<IReadOnlyList<BookingStatusHistoryEntry>>(Array.Empty<BookingStatusHistoryEntry>());
        }

        lock (list)
        {
            IReadOnlyList<BookingStatusHistoryEntry> snapshot = list
                .OrderBy(e => e.ChangedAtUtc)
                .ToArray();
            return Task.FromResult(snapshot);
        }
    }

    private void AppendHistory(
        Guid bookingId,
        string? fromStatus,
        string toStatus,
        string actor,
        Guid? actorId,
        string? note)
    {
        var list = history.GetOrAdd(bookingId, _ => new List<BookingStatusHistoryEntry>());
        lock (list)
        {
            list.Add(new BookingStatusHistoryEntry(
                Guid.NewGuid(), bookingId, fromStatus, toStatus, actor, actorId, note, DateTimeOffset.UtcNow));
        }
    }

    // The job lifecycle (start-OTP, evidence, modifications) is not implemented in
    // the in-memory dev fallback — it requires the SQL-backed path.
    private static NotSupportedException NotInMemory()
        => new("The night-stay job lifecycle requires the SQL-backed store.");

    public Task<StartOtpResult> IssueStartOtpAsync(Guid bookingId, string newCode, int ttlMinutes, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<NightStayBookingResult> StartJobAsync(Guid bookingId, Guid providerId, string newCode, int ttlMinutes, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<NightStayBookingResult> VerifyStartOtpAsync(Guid bookingId, Guid providerId, string otpCode, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<NightStayBookingResult> CompleteAsync(Guid bookingId, Guid providerId, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<NightStayBookingResult> MarkPaidAsync(Guid bookingId, Guid providerId, decimal amount, decimal pawfrontFee, string paymentMethod, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<NightStayBookingResult> RequestModificationAsync(Guid bookingId, BookingStatusActor actor, Guid actorId,
        DateOnly checkInDate, DateOnly checkOutDate, string? note,
        BookingAcknowledgedTerms? acknowledgedTerms, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<NightStayBookingResult> RespondModificationAsync(Guid bookingId, BookingStatusActor actor, Guid actorId,
        bool accept, int capacity, string? note, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<NightStayBookingModificationResult?> GetPendingModificationAsync(Guid bookingId, CancellationToken cancellationToken)
        => Task.FromResult<NightStayBookingModificationResult?>(null);

    public Task<BookingEvidenceResult> AddEvidenceAsync(Guid bookingId, Guid providerId, string photoUrl, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<IReadOnlyList<BookingEvidenceResult>> ListEvidenceAsync(Guid bookingId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<BookingEvidenceResult>>(Array.Empty<BookingEvidenceResult>());

    private static NightStayBookingResult ToResult(Row row) =>
        new(row.NightStayBookingId,
            row.ProviderId,
            row.PetParentId,
            row.ServiceId,
            row.ServiceCategory,
            row.SubCategory,
            row.CheckInDate,
            row.CheckOutDate,
            row.DropOffTime,
            row.PickUpTime,
            row.Status,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.CancelledAtUtc,
            row.PetId);

    private sealed class Row
    {
        public Guid NightStayBookingId { get; init; }
        public Guid ProviderId { get; init; }
        public Guid PetParentId { get; init; }
        public Guid? PetId { get; init; }
        public Guid ServiceId { get; init; }
        public required string ServiceCategory { get; init; }
        public required string SubCategory { get; init; }
        public DateOnly CheckInDate { get; init; }
        public DateOnly CheckOutDate { get; init; }
        public TimeOnly DropOffTime { get; init; }
        public TimeOnly PickUpTime { get; init; }
        public string? JobNotes { get; init; }
        public string? LocationType { get; init; }
        public decimal? PricePerNight { get; init; }
        public string? SnapshotAddressLine { get; init; }
        public string? SnapshotCity { get; init; }
        public string? SnapshotZipCode { get; init; }
        public decimal? SnapshotLatitude { get; init; }
        public decimal? SnapshotLongitude { get; init; }
        public required string Status { get; set; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public DateTimeOffset? CancelledAtUtc { get; set; }
    }
}
