using System.Collections.Concurrent;
using Pawfront.Application.Bookings;

namespace Pawfront.Infrastructure.Sql.Bookings;

/// <summary>
/// In-memory implementation of <see cref="IBookingSqlStore"/> used in the dev fallback path
/// (no SQL conn string + Key Vault disabled). Behaviour mirrors the SQL stored procedures
/// for the happy path plus the typed exceptions; capacity check is race-safe via a per-service
/// async lock instead of UPDLOCK+HOLDLOCK, and is scoped by ServiceId so DayCare and NightStay
/// each have independent capacity.
/// </summary>
internal sealed class InMemoryBookingStore(InMemoryNightStayBookingStore nightStayStore) : IBookingSqlStore
{
    private readonly ConcurrentDictionary<Guid, BookingRow> bookings = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> serviceLocks = new();
    private readonly ConcurrentDictionary<Guid, List<BookingStatusHistoryEntry>> history = new();
    private readonly ConcurrentDictionary<Guid, PrescriptionRow> prescriptions = new();
    private int jobSequence;

    // A booking holds its slot unless it has been cancelled by either party.
    private static bool IsActive(BookingRow row) => !BookingStatuses.Cancelled.Contains(row.Status);

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
                Guid.NewGuid(),
                bookingId,
                fromStatus,
                toStatus,
                actor,
                actorId,
                note,
                DateTimeOffset.UtcNow));
        }
    }

    public async Task<BookingResult> CreateAsync(
        Guid providerId,
        Guid petParentId,
        Guid? petId,
        Guid serviceId,
        string serviceCategory,
        string subCategory,
        string? serviceItemCode,
        DateOnly bookingDate,
        TimeOnly startTime,
        TimeOnly endTime,
        string? jobNotes,
        string? locationType,
        decimal? pricePerHour,
        ProviderAddressSnapshot? providerAddress,
        int capacity,
        CancellationToken cancellationToken)
    {
        var serviceLock = serviceLocks.GetOrAdd(serviceId, _ => new SemaphoreSlim(1, 1));
        await serviceLock.WaitAsync(cancellationToken);
        try
        {
            // Reject a duplicate booking for the same pet (same service, overlapping
            // window) — mirrors THROW 51069 in [Booking].[CreateBooking].
            if (petId is not null && bookings.Values.Any(b =>
                    b.ServiceId == serviceId
                    && b.PetId == petId
                    && b.BookingDate == bookingDate
                    && IsActive(b)
                    && b.StartTime < endTime
                    && b.EndTime > startTime))
            {
                throw new PetAlreadyBookedException(petId.Value, serviceId, bookingDate, startTime, endTime);
            }

            // Count overlapping active (non-cancelled) bookings for this service+date.
            var concurrent = bookings.Values.Count(b =>
                b.ServiceId == serviceId
                && b.BookingDate == bookingDate
                && IsActive(b)
                && b.StartTime < endTime
                && b.EndTime > startTime);

            if (concurrent >= capacity)
            {
                throw new BookingCapacityExceededException(serviceId, bookingDate, startTime, endTime);
            }

            var now = DateTimeOffset.UtcNow;
            var row = new BookingRow
            {
                BookingId = Guid.NewGuid(),
                JobNumber = Interlocked.Increment(ref jobSequence),
                ProviderId = providerId,
                PetParentId = petParentId,
                PetId = petId,
                ServiceId = serviceId,
                ServiceCategory = serviceCategory,
                SubCategory = subCategory,
                ServiceItemCode = serviceItemCode,
                BookingDate = bookingDate,
                StartTime = startTime,
                EndTime = endTime,
                JobNotes = jobNotes,
                LocationType = locationType,
                PricePerHour = pricePerHour,
                // Dev fallback: the parent's address isn't joined in-memory, so a
                // ParentLocation snapshot stays null (falls back to live on read).
                // A ProviderLocation booking snapshots the resolved provider address.
                SnapshotAddressLine = providerAddress?.AddressLine,
                SnapshotCity = providerAddress?.City,
                SnapshotZipCode = providerAddress?.ZipCode,
                SnapshotLatitude = providerAddress?.Latitude,
                SnapshotLongitude = providerAddress?.Longitude,
                Status = BookingStatuses.Created,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CancelledAtUtc = null,
                Source = "App"
            };

            bookings[row.BookingId] = row;
            AppendHistory(row.BookingId, null, row.Status, "System", null, "Booking created");
            return ToResult(row);
        }
        finally
        {
            serviceLock.Release();
        }
    }

    public async Task<BookingResult> CreateCustomAsync(
        Guid providerId,
        Guid serviceId,
        string serviceCategory,
        string subCategory,
        string customerName,
        string customerMobileCountryCode,
        string customerMobile,
        string animalType,
        string petName,
        DateOnly bookingDate,
        TimeOnly startTime,
        TimeOnly endTime,
        string serviceLocation,
        string? customerLocation,
        decimal pricePerHour,
        string? jobNotes,
        int capacity,
        CancellationToken cancellationToken)
    {
        var serviceLock = serviceLocks.GetOrAdd(serviceId, _ => new SemaphoreSlim(1, 1));
        await serviceLock.WaitAsync(cancellationToken);
        try
        {
            var concurrent = bookings.Values.Count(b =>
                b.ServiceId == serviceId
                && b.BookingDate == bookingDate
                && IsActive(b)
                && b.StartTime < endTime
                && b.EndTime > startTime);

            if (concurrent >= capacity)
            {
                throw new BookingCapacityExceededException(serviceId, bookingDate, startTime, endTime);
            }

            var now = DateTimeOffset.UtcNow;
            var row = new BookingRow
            {
                BookingId = Guid.NewGuid(),
                JobNumber = Interlocked.Increment(ref jobSequence),
                ProviderId = providerId,
                PetParentId = null,
                ServiceId = serviceId,
                ServiceCategory = serviceCategory,
                SubCategory = subCategory,
                ServiceItemCode = null,
                BookingDate = bookingDate,
                StartTime = startTime,
                EndTime = endTime,
                // Provider-added walk-in is the provider's own job — already confirmed.
                Status = BookingStatuses.Confirmed,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                CancelledAtUtc = null,
                Source = "Custom",
                CustomerName = customerName,
                CustomerMobileCountryCode = customerMobileCountryCode,
                CustomerMobile = customerMobile,
                AnimalType = animalType,
                PetName = petName,
                ServiceLocation = serviceLocation,
                CustomerLocation = customerLocation,
                PricePerHour = pricePerHour,
                JobNotes = jobNotes
            };

            bookings[row.BookingId] = row;
            AppendHistory(row.BookingId, null, row.Status, "System", null, "Booking created");
            return ToResult(row);
        }
        finally
        {
            serviceLock.Release();
        }
    }

    public Task<BookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        bookings.TryGetValue(bookingId, out var row);
        return Task.FromResult(row is null ? null : (BookingResult?)ToResult(row));
    }

    public Task<BookingDetailRow?> GetDetailAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        // Dev fallback: no parent/pet join data is available in-memory, so those
        // fields stay null (App-booking customer/pet details aren't populated here).
        if (!bookings.TryGetValue(bookingId, out var row))
        {
            return Task.FromResult<BookingDetailRow?>(null);
        }

        var detail = new BookingDetailRow(
            BookingId: row.BookingId,
            JobNumber: row.JobNumber,
            ProviderId: row.ProviderId,
            PetParentId: row.PetParentId,
            ServiceId: row.ServiceId,
            ServiceCategory: row.ServiceCategory,
            SubCategory: row.SubCategory,
            BookingDate: row.BookingDate,
            StartTime: row.StartTime,
            EndTime: row.EndTime,
            Status: row.Status,
            CreatedAtUtc: row.CreatedAtUtc,
            UpdatedAtUtc: row.UpdatedAtUtc,
            CancelledAtUtc: row.CancelledAtUtc,
            ServiceItemCode: row.ServiceItemCode,
            Source: row.Source,
            CustomerName: row.CustomerName,
            CustomerMobileCountryCode: row.CustomerMobileCountryCode,
            CustomerMobile: row.CustomerMobile,
            AnimalType: row.AnimalType,
            PetName: row.PetName,
            ServiceLocation: row.ServiceLocation,
            CustomerLocation: row.CustomerLocation,
            PricePerHour: row.PricePerHour,
            JobNotes: row.JobNotes,
            PetId: row.PetId,
            PayoutStatus: "Pending",
            PayoutId: null,
            ParentFirstName: null,
            ParentLastName: null,
            ParentGender: null,
            ParentMobileCountryCode: null,
            ParentMobileNumber: null,
            ParentPhotoUrl: null,
            PetProfileName: null,
            PetType: null,
            PetGender: null,
            PetPhotoUrl: null,
            ProviderFirstName: null,
            ProviderLastName: null,
            ProviderGender: null,
            ProviderMobileCountryCode: null,
            ProviderMobileNumber: null,
            PetBreed: null,
            PetVaccinationStatus: null,
            PetVaccinationType: null,
            PetVaccinationDose: null,
            PetPrescription: null,
            LocationType: row.LocationType,
            // Vet prescription (dev fallback: NextConsultationDate isn't resolved
            // in-memory — it lives on the SQL PetNextConsultations join).
            HasPrescription: prescriptions.ContainsKey(row.BookingId),
            PrescriptionText: prescriptions.TryGetValue(row.BookingId, out var rx) ? rx.PrescriptionText : null,
            IsPetVaccinated: prescriptions.TryGetValue(row.BookingId, out var rx2) ? rx2.IsPetVaccinated : null,
            PrescriptionVaccinations: prescriptions.TryGetValue(row.BookingId, out var rx3) ? rx3.Vaccinations : null,
            NextConsultationDate: null,
            // Dev fallback: no provider cancellation policy is resolved in-memory.
            CancellationPolicyHours: null,
            SnapshotAddressLine: row.SnapshotAddressLine,
            SnapshotCity: row.SnapshotCity,
            SnapshotZipCode: row.SnapshotZipCode,
            SnapshotLatitude: row.SnapshotLatitude,
            SnapshotLongitude: row.SnapshotLongitude);
        return Task.FromResult<BookingDetailRow?>(detail);
    }

    public Task<BookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        if (!bookings.TryGetValue(bookingId, out var row))
        {
            throw new BookingNotFoundException(bookingId);
        }

        if (row.PetParentId != petParentId)
        {
            throw new BookingCancellationForbiddenException(bookingId);
        }

        if (BookingStatuses.Cancelled.Contains(row.Status))
        {
            throw new BookingAlreadyCancelledException(bookingId);
        }

        var now = DateTimeOffset.UtcNow;
        var from = row.Status;
        row.Status = BookingStatuses.ParentCancelled;
        row.CancelledAtUtc = now;
        row.UpdatedAtUtc = now;
        AppendHistory(bookingId, from, row.Status, "Parent", petParentId, null);

        return Task.FromResult(ToResult(row));
    }

    public Task<BookingResult> UpdateStatusAsync(
        Guid bookingId,
        string newStatus,
        BookingStatusActor actor,
        Guid actorId,
        string? note,
        CancellationToken cancellationToken)
    {
        if (!bookings.TryGetValue(bookingId, out var row))
        {
            throw new BookingNotFoundException(bookingId);
        }

        // Actor must be a party to the booking.
        var ownsBooking = actor == BookingStatusActor.Provider
            ? row.ProviderId == actorId
            : row.PetParentId is not null && row.PetParentId == actorId;
        if (!ownsBooking)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }

        // A booking left pending (CREATED) for 24+ hours has expired: reject the
        // attempted transition — mirror of the SQL sproc's guard (THROW 51129).
        // Reject only; the EXPIRED status is written by the scheduled external
        // job, which this dev fallback has no equivalent of, so the row simply
        // stays in CREATED.
        if (row.Status == BookingStatuses.Created
            && DateTimeOffset.UtcNow >= row.CreatedAtUtc.AddHours(24))
        {
            throw BookingExpiredException.NeverAccepted(bookingId);
        }

        // BR-53: still CREATED with under 2 hours to the service — mirror of the
        // sproc's THROW 51153, and reject-only for the same reason.
        if (row.Status == BookingStatuses.Created
            && BookingLeadTime.IsTooSoon(row.BookingDate, row.StartTime, DateTimeOffset.UtcNow))
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

            var startsAtUtc = new DateTimeOffset(row.BookingDate.ToDateTime(row.StartTime), TimeSpan.Zero);
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

    public Task<IReadOnlyList<BookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? date,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BookingResult> list = bookings.Values
            .Where(b => b.ProviderId == providerId)
            .Where(b => date is null || b.BookingDate == date.Value)
            .OrderByDescending(b => b.BookingDate)
            .ThenByDescending(b => b.StartTime)
            .Select(ToResult)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<BookingListItemResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        // Dev fallback: no cancellation policy is resolved in-memory; the location
        // block carries whatever snapshot the create captured (provider address).
        IReadOnlyList<BookingListItemResult> list = bookings.Values
            .Where(b => b.PetParentId == petParentId)
            .OrderByDescending(b => b.BookingDate)
            .ThenByDescending(b => b.StartTime)
            .Select(b => new BookingListItemResult(
                ToResult(b),
                CancellationPolicyHours: null,
                Location: new BookingLocationResult(
                    b.LocationType, b.SnapshotAddressLine, b.SnapshotCity,
                    b.SnapshotZipCode, b.SnapshotLatitude, b.SnapshotLongitude)))
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<BookingWindow>> GetBookingsForDateAsync(
        Guid serviceId,
        DateOnly bookingDate,
        CancellationToken cancellationToken)
    {
        // A night-stay covering this date occupies its NightStay bucket for the
        // whole night — surfaced as full-day windows, mirroring the SQL sproc.
        var nightStayWindow = new BookingWindow(TimeOnly.MinValue, new TimeOnly(23, 59, 59));
        var nightStayCount = nightStayStore.CountActiveStaysCoveringNight(serviceId, bookingDate);

        IReadOnlyList<BookingWindow> list = bookings.Values
            .Where(b =>
                b.ServiceId == serviceId
                && b.BookingDate == bookingDate
                && IsActive(b))
            .OrderBy(b => b.StartTime)
            .Select(b => new BookingWindow(b.StartTime, b.EndTime))
            .Concat(Enumerable.Repeat(nightStayWindow, nightStayCount))
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<AgendaBookingRow>> GetAgendaForDateAsync(
        Guid serviceId,
        DateOnly bookingDate,
        CancellationToken cancellationToken)
    {
        // Same rows as GetBookingsForDateAsync above, carrying the identity the
        // daily agenda needs — mirrors [Booking].[GetAgendaForDate].
        IReadOnlyList<AgendaBookingRow> list = bookings.Values
            .Where(b =>
                b.ServiceId == serviceId
                && b.BookingDate == bookingDate
                && IsActive(b))
            .Select(b => new AgendaBookingRow(
                "SingleDay",
                b.BookingId,
                b.JobNumber,
                b.PetParentId,
                b.StartTime,
                b.EndTime,
                b.Status))
            .Concat(nightStayStore.ListActiveStaysCoveringNight(serviceId, bookingDate))
            .OrderBy(r => r.StartTime)
            .ThenBy(r => r.EndTime)
            .ToArray();
        return Task.FromResult(list);
    }

    // The job lifecycle (start/end OTP, evidence, modifications) is not implemented
    // in the in-memory dev fallback — it requires the SQL-backed path.
    private static NotSupportedException NotInMemory()
        => new("The booking job lifecycle requires the SQL-backed store.");

    public Task<StartOtpResult> IssueStartOtpAsync(Guid bookingId, string newCode, int ttlMinutes, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<BookingResult> StartJobAsync(Guid bookingId, Guid providerId, string newCode, int ttlMinutes, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<BookingResult> VerifyStartOtpAsync(Guid bookingId, Guid providerId, string otpCode, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<BookingResult> CompleteAsync(Guid bookingId, Guid providerId, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<BookingResult> MarkPaidAsync(Guid bookingId, Guid providerId, decimal amount, decimal pawfrontFee, string paymentMethod, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<BookingResult> RequestModificationAsync(Guid bookingId, BookingStatusActor actor, Guid actorId,
        DateOnly bookingDate, TimeOnly startTime, TimeOnly endTime, string? note,
        BookingAcknowledgedTerms? acknowledgedTerms, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<BookingResult> RespondModificationAsync(Guid bookingId, BookingStatusActor actor, Guid actorId,
        bool accept, int capacity, string? note, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<BookingModificationResult?> GetPendingModificationAsync(Guid bookingId, CancellationToken cancellationToken)
        => Task.FromResult<BookingModificationResult?>(null);

    public Task<BookingEvidenceResult> AddEvidenceAsync(Guid bookingId, Guid providerId, string photoUrl, CancellationToken cancellationToken)
        => throw NotInMemory();

    public Task<IReadOnlyList<BookingEvidenceResult>> ListEvidenceAsync(Guid bookingId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<BookingEvidenceResult>>(Array.Empty<BookingEvidenceResult>());

    public Task<BookingPrescriptionResult> UpsertPrescriptionAsync(
        Guid bookingId,
        Guid providerId,
        string? prescriptionText,
        bool isPetVaccinated,
        IReadOnlyList<string> vaccinations,
        CancellationToken cancellationToken)
    {
        if (!bookings.TryGetValue(bookingId, out var row))
        {
            throw new BookingNotFoundException(bookingId);
        }
        if (row.ProviderId != providerId)
        {
            throw new BookingPrescriptionForbiddenException(bookingId);
        }
        if (!string.Equals(row.ServiceCategory, "Vet", StringComparison.Ordinal))
        {
            throw new BookingPrescriptionNotVetException(bookingId);
        }
        if (!BookingStatuses.PrescriptionAllowedFrom.Contains(row.Status))
        {
            throw new BookingPrescriptionInvalidStateException(bookingId);
        }

        var now = DateTimeOffset.UtcNow;
        var stored = prescriptions.AddOrUpdate(
            bookingId,
            _ => new PrescriptionRow
            {
                PrescriptionText = prescriptionText,
                IsPetVaccinated = isPetVaccinated,
                Vaccinations = vaccinations,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            },
            (_, existing) => new PrescriptionRow
            {
                PrescriptionText = prescriptionText,
                IsPetVaccinated = isPetVaccinated,
                Vaccinations = vaccinations,
                CreatedAtUtc = existing.CreatedAtUtc,
                UpdatedAtUtc = now
            });

        // NextConsultationDate isn't resolved in the in-memory fallback.
        return Task.FromResult(new BookingPrescriptionResult(
            bookingId, stored.PrescriptionText, stored.IsPetVaccinated, stored.Vaccinations,
            NextConsultationDate: null, stored.CreatedAtUtc, stored.UpdatedAtUtc));
    }

    private static BookingResult ToResult(BookingRow row) =>
        new(row.BookingId,
            row.ProviderId,
            row.PetParentId,
            row.ServiceId,
            row.ServiceCategory,
            row.SubCategory,
            row.BookingDate,
            row.StartTime,
            row.EndTime,
            row.Status,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.CancelledAtUtc,
            row.ServiceItemCode,
            row.Source,
            row.CustomerName,
            row.CustomerMobileCountryCode,
            row.CustomerMobile,
            row.AnimalType,
            row.PetName,
            row.ServiceLocation,
            row.CustomerLocation,
            row.PricePerHour,
            row.JobNotes,
            row.PetId);

    private sealed class BookingRow
    {
        public Guid BookingId { get; init; }
        public int JobNumber { get; init; }
        public Guid ProviderId { get; init; }
        public Guid? PetParentId { get; init; }
        public Guid? PetId { get; init; }
        public Guid ServiceId { get; init; }
        public required string ServiceCategory { get; init; }
        public required string SubCategory { get; init; }
        public string? ServiceItemCode { get; init; }
        public DateOnly BookingDate { get; init; }
        public TimeOnly StartTime { get; init; }
        public TimeOnly EndTime { get; init; }
        public required string Status { get; set; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; set; }
        public DateTimeOffset? CancelledAtUtc { get; set; }
        public required string Source { get; init; }
        public string? CustomerName { get; init; }
        public string? CustomerMobileCountryCode { get; init; }
        public string? CustomerMobile { get; init; }
        public string? AnimalType { get; init; }
        public string? PetName { get; init; }
        public string? ServiceLocation { get; init; }
        public string? CustomerLocation { get; init; }
        public decimal? PricePerHour { get; init; }
        public string? JobNotes { get; init; }
        public string? LocationType { get; init; }
        public string? SnapshotAddressLine { get; init; }
        public string? SnapshotCity { get; init; }
        public string? SnapshotZipCode { get; init; }
        public decimal? SnapshotLatitude { get; init; }
        public decimal? SnapshotLongitude { get; init; }
    }

    private sealed class PrescriptionRow
    {
        public string? PrescriptionText { get; init; }
        public bool IsPetVaccinated { get; init; }
        public required IReadOnlyList<string> Vaccinations { get; init; }
        public DateTimeOffset CreatedAtUtc { get; init; }
        public DateTimeOffset UpdatedAtUtc { get; init; }
    }
}
