using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Bookings;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Sql.Bookings;

internal sealed class SqlBookingStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IBookingSqlStore
{
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
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.CreateBooking", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@PetParentId", petParentId);
        command.Parameters.AddWithValue("@PetId", petId is null ? DBNull.Value : (object)petId.Value);
        command.Parameters.AddWithValue("@ServiceId", serviceId);
        command.Parameters.AddWithValue("@ServiceCategory", serviceCategory);
        command.Parameters.AddWithValue("@SubCategory", subCategory);
        command.Parameters.AddWithValue("@ServiceItemCode",
            serviceItemCode is null ? DBNull.Value : (object)serviceItemCode);
        command.Parameters.AddWithValue("@BookingDate", bookingDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@StartTime", startTime.ToTimeSpan());
        command.Parameters.AddWithValue("@EndTime", endTime.ToTimeSpan());
        command.Parameters.AddWithValue("@JobNotes",
            jobNotes is null ? DBNull.Value : (object)jobNotes);
        command.Parameters.AddWithValue("@LocationType",
            locationType is null ? DBNull.Value : (object)locationType);
        command.Parameters.AddWithValue("@PricePerHour",
            pricePerHour is null ? DBNull.Value : (object)pricePerHour.Value);
        AddProviderAddressSnapshotParameters(command, providerAddress);
        command.Parameters.AddWithValue("@Capacity", capacity);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Booking row was not returned after create.");
            }
            return ReadBookingRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51060)
        {
            throw new BookingPetParentNotFoundException(petParentId);
        }
        catch (SqlException exception) when (exception.Number == 51061)
        {
            throw new BookingProviderNotFoundException(providerId);
        }
        catch (SqlException exception) when (exception.Number == 51062)
        {
            throw new BookingCapacityExceededException(serviceId, bookingDate, startTime, endTime);
        }
        catch (SqlException exception) when (exception.Number == 51066)
        {
            throw new BookingServiceInvalidException(serviceId, providerId);
        }
        catch (SqlException exception) when (exception.Number == 51067)
        {
            throw new BookingProviderInactiveException(providerId);
        }
        catch (SqlException exception) when (exception.Number == 51068)
        {
            throw new BookingPetInvalidException(petId!.Value, petParentId);
        }
        catch (SqlException exception) when (exception.Number == 51069)
        {
            throw new PetAlreadyBookedException(petId!.Value, serviceId, bookingDate, startTime, endTime);
        }
    }

    public async Task<BookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.GetBooking", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return ReadBookingRow(reader);
    }

    public async Task<BookingDetailRow?> GetDetailAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.GetBookingDetail", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return ReadDetailRow(reader);
    }

    public async Task<BookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.CancelBooking", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@PetParentId", petParentId);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Booking row was not returned after cancel.");
            }
            return ReadBookingRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51063)
        {
            throw new BookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51064)
        {
            throw new BookingCancellationForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51065)
        {
            throw new BookingAlreadyCancelledException(bookingId);
        }
    }

    public async Task<IReadOnlyList<BookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? date,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.ListBookingsByProvider", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@ServiceId", DBNull.Value);
        command.Parameters.AddWithValue("@BookingDate",
            date is null ? DBNull.Value : (object)date.Value.ToDateTime(TimeOnly.MinValue));

        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<BookingListItemResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.ListBookingsByPetParent", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@PetParentId", petParentId);

        var rows = new List<BookingListItemResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadListItemRow(reader));
        }
        return rows;
    }

    // The list sproc appends the frozen-at-creation extras AFTER the standard
    // booking-row columns (ordinals 25-31), so the shared ReadBookingRow reader
    // stays untouched for every other sproc.
    private static BookingListItemResult ReadListItemRow(SqlDataReader reader) =>
        new(ReadBookingRow(reader),
            CancellationPolicyHours: reader.IsDBNull(26) ? null : reader.GetInt32(26),
            Location: new BookingLocationResult(
                LocationType: reader.IsDBNull(25) ? null : reader.GetString(25),
                AddressLine: reader.IsDBNull(27) ? null : reader.GetString(27),
                City: reader.IsDBNull(28) ? null : reader.GetString(28),
                ZipCode: reader.IsDBNull(29) ? null : reader.GetString(29),
                Latitude: reader.IsDBNull(30) ? null : reader.GetDecimal(30),
                Longitude: reader.IsDBNull(31) ? null : reader.GetDecimal(31)));

    public async Task<IReadOnlyList<BookingWindow>> GetBookingsForDateAsync(
        Guid serviceId,
        DateOnly bookingDate,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.GetBookingsForDate", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ServiceId", serviceId);
        command.Parameters.AddWithValue("@BookingDate", bookingDate.ToDateTime(TimeOnly.MinValue));

        var windows = new List<BookingWindow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            windows.Add(new BookingWindow(
                TimeOnly.FromTimeSpan(reader.GetTimeSpan(0)),
                TimeOnly.FromTimeSpan(reader.GetTimeSpan(1))));
        }
        return windows;
    }

    public async Task<IReadOnlyList<AgendaBookingRow>> GetAgendaForDateAsync(
        Guid serviceId,
        DateOnly bookingDate,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.GetAgendaForDate", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ServiceId", serviceId);
        command.Parameters.AddWithValue("@BookingDate", bookingDate.ToDateTime(TimeOnly.MinValue));

        var rows = new List<AgendaBookingRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AgendaBookingRow(
                reader.GetString(0),
                reader.GetGuid(1),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                TimeOnly.FromTimeSpan(reader.GetTimeSpan(4)),
                TimeOnly.FromTimeSpan(reader.GetTimeSpan(5)),
                reader.GetString(6)));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<BookingResult>> ReadAllAsync(
        SqlCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<BookingResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadBookingRow(reader));
        }
        return rows;
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
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.CreateCustomBooking", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@ServiceId", serviceId);
        command.Parameters.AddWithValue("@ServiceCategory", serviceCategory);
        command.Parameters.AddWithValue("@SubCategory", subCategory);
        command.Parameters.AddWithValue("@CustomerName", customerName);
        command.Parameters.AddWithValue("@CustomerMobileCountryCode", customerMobileCountryCode);
        command.Parameters.AddWithValue("@CustomerMobile", customerMobile);
        command.Parameters.AddWithValue("@AnimalType", animalType);
        command.Parameters.AddWithValue("@PetName", petName);
        command.Parameters.AddWithValue("@BookingDate", bookingDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@StartTime", startTime.ToTimeSpan());
        command.Parameters.AddWithValue("@EndTime", endTime.ToTimeSpan());
        command.Parameters.AddWithValue("@ServiceLocation", serviceLocation);
        command.Parameters.AddWithValue("@CustomerLocation",
            customerLocation is null ? DBNull.Value : (object)customerLocation);
        command.Parameters.AddWithValue("@PricePerHour", pricePerHour);
        command.Parameters.AddWithValue("@JobNotes",
            jobNotes is null ? DBNull.Value : (object)jobNotes);
        command.Parameters.AddWithValue("@Capacity", capacity);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Booking row was not returned after create.");
            }
            return ReadBookingRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51061)
        {
            throw new BookingProviderNotFoundException(providerId);
        }
        catch (SqlException exception) when (exception.Number == 51062)
        {
            throw new BookingCapacityExceededException(serviceId, bookingDate, startTime, endTime);
        }
        catch (SqlException exception) when (exception.Number == 51066)
        {
            throw new BookingServiceInvalidException(serviceId, providerId);
        }
        catch (SqlException exception) when (exception.Number == 51067)
        {
            throw new BookingProviderInactiveException(providerId);
        }
    }

    public async Task<BookingResult> UpdateStatusAsync(
        Guid bookingId,
        string newStatus,
        BookingStatusActor actor,
        Guid actorId,
        string? note,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.UpdateBookingStatus", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@NewStatus", newStatus);
        command.Parameters.AddWithValue("@Actor", actor.ToString());
        command.Parameters.AddWithValue("@ActorId", actorId);
        command.Parameters.AddWithValue("@Note", note is null ? DBNull.Value : (object)note);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Booking row was not returned after status update.");
            }
            return ReadBookingRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51120)
        {
            throw new BookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51121)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51122)
        {
            throw new BookingStatusNotAllowedException(newStatus, actor);
        }
        catch (SqlException exception) when (exception.Number == 51123)
        {
            throw new BookingStatusTerminalException(bookingId, newStatus);
        }
        catch (SqlException exception) when (exception.Number == 51124)
        {
            throw new BookingStatusUnchangedException(bookingId, newStatus);
        }
        catch (SqlException exception) when (exception.Number == 51125)
        {
            // Defensive: the Application layer already validated the status/actor.
            throw new UnsupportedBookingStatusException(newStatus);
        }
        catch (SqlException exception) when (exception.Number == 51126)
        {
            // Engine from-state guard (e.g. a no-show reported on a job that
            // already started, or an accept on a non-CREATED booking).
            throw new BookingNotStartableException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51128)
        {
            throw new BookingNoShowTooEarlyException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51129)
        {
            // The booking has sat in CREATED for 24+ hours, so the attempted
            // transition (e.g. accept) is rejected. The sproc rejects only — the
            // stored status is still CREATED until the scheduled external job
            // settles it to EXPIRED.
            throw BookingExpiredException.NeverAccepted(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51153)
        {
            // BR-53: still CREATED with under 2 hours to the service. Same
            // reject-only posture as 51129 above.
            throw BookingExpiredException.ServiceTooClose(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51149)
        {
            // Cancel attempted while the job is already underway (IN_PROGRESS / ENDING).
            throw new BookingJobInProgressException(bookingId);
        }
    }

    public async Task<IReadOnlyList<BookingStatusHistoryEntry>> ListStatusHistoryAsync(
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.ListBookingStatusHistory", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);

        var entries = new List<BookingStatusHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new BookingStatusHistoryEntry(
                BookingStatusHistoryId: reader.GetGuid(0),
                BookingId: reader.GetGuid(1),
                FromStatus: reader.IsDBNull(2) ? null : reader.GetString(2),
                ToStatus: reader.GetString(3),
                ChangedByActor: reader.GetString(4),
                ChangedByActorId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
                Note: reader.IsDBNull(6) ? null : reader.GetString(6),
                ChangedAtUtc: new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero)));
        }
        return entries;
    }

    public async Task<StartOtpResult> IssueStartOtpAsync(
        Guid bookingId, string newCode, int ttlMinutes, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.IssueBookingStartOtp", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@NewCode", newCode);
        command.Parameters.AddWithValue("@TtlMinutes", ttlMinutes);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Start OTP row was not returned.");
            }
            return ReadStartOtp(reader);
        }
        catch (SqlException exception) when (exception.Number == 51130)
        {
            throw new BookingNotFoundException(bookingId);
        }
    }

    public async Task<BookingResult> StartJobAsync(
        Guid bookingId, Guid providerId, string newCode, int ttlMinutes, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.StartBooking", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@NewCode", newCode);
        command.Parameters.AddWithValue("@TtlMinutes", ttlMinutes);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Booking row was not returned after start.");
            }
            return ReadBookingRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51131)
        {
            throw new BookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51132)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51133)
        {
            throw new BookingNotStartableException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51144)
        {
            throw new BookingStartNotOnServiceDateException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51137)
        {
            throw new BookingStartOutsideWorkingHoursException(bookingId);
        }
    }

    public async Task<BookingResult> VerifyStartOtpAsync(
        Guid bookingId, Guid providerId, string otpCode, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.VerifyBookingStartOtp", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@OtpCode", otpCode);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Booking row was not returned after start-OTP verification.");
            }
            return ReadBookingRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51131)
        {
            throw new BookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51132)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51138)
        {
            throw new BookingNotStartableException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51134)
        {
            throw new InvalidStartOtpException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51135)
        {
            throw new StartOtpExpiredException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51136)
        {
            // The 6th wrong OTP attempt cancelled the job.
            throw new OtpAttemptsExceededException(bookingId);
        }
    }

    public async Task<BookingResult> CompleteAsync(
        Guid bookingId, Guid providerId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.CompleteBooking", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@ProviderId", providerId);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Booking row was not returned after completion.");
            }
            return ReadBookingRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51131)
        {
            throw new BookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51132)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51133)
        {
            throw new BookingNotCompletableException(bookingId);
        }
    }

    public async Task<BookingResult> MarkPaidAsync(
        Guid bookingId, Guid providerId, decimal amount, decimal pawfrontFee,
        string paymentMethod, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.MarkBookingPaid", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@Amount", amount);
        command.Parameters.AddWithValue("@PawfrontFee", pawfrontFee);
        command.Parameters.AddWithValue("@PaymentMethod", paymentMethod);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Booking row was not returned after payment.");
            }
            return ReadBookingRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51160)
        {
            throw new BookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51161)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51162)
        {
            throw new BookingNotPayableException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51163)
        {
            throw new BookingPaymentNotAppException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51164)
        {
            throw new BookingAlreadyPaidException(bookingId);
        }
    }

    public async Task<BookingResult> RequestModificationAsync(
        Guid bookingId, BookingStatusActor actor, Guid actorId,
        DateOnly bookingDate, TimeOnly startTime, TimeOnly endTime,
        string? note, BookingAcknowledgedTerms? acknowledgedTerms, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.RequestBookingModification", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@Actor", actor.ToString());
        command.Parameters.AddWithValue("@ActorId", actorId);
        command.Parameters.AddWithValue("@ProposedBookingDate", bookingDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@ProposedStartTime", startTime.ToTimeSpan());
        command.Parameters.AddWithValue("@ProposedEndTime", endTime.ToTimeSpan());
        command.Parameters.AddWithValue("@Note", note is null ? DBNull.Value : (object)note);
        AddAcknowledgedTermsParameters(
            command, acknowledgedTerms, "@AcknowledgedPricePerHour", includeStayTimes: false);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Booking row was not returned after modification request.");
            }
            return ReadBookingRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51140)
        {
            throw new BookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51141)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51142)
        {
            throw new BookingNotModifiableException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51143)
        {
            throw new BookingModificationConflictException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51151)
        {
            throw new BookingModificationWindowClosedException(bookingId);
        }
    }

    public async Task<BookingResult> RespondModificationAsync(
        Guid bookingId, BookingStatusActor actor, Guid actorId,
        bool accept, int capacity, string? note, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.RespondBookingModification", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@Actor", actor.ToString());
        command.Parameters.AddWithValue("@ActorId", actorId);
        command.Parameters.AddWithValue("@Accept", accept);
        command.Parameters.AddWithValue("@Capacity", capacity);
        command.Parameters.AddWithValue("@Note", note is null ? DBNull.Value : (object)note);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Booking row was not returned after modification response.");
            }
            return ReadBookingRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51145)
        {
            throw new BookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51146)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51147)
        {
            throw new NoPendingModificationException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51148)
        {
            throw new BookingModificationCapacityException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51152)
        {
            // The proposal — from either party — passed its 2-hour cutoff, so the
            // response is rejected. The sproc rejects only — the booking is still
            // parked in whichever MODIFICATION_REQUEST_BY_* status it was in
            // until the scheduled external job reverts it to CONFIRMED.
            throw new BookingModificationExpiredException(bookingId);
        }
    }

    public async Task<BookingEvidenceResult> AddEvidenceAsync(
        Guid bookingId, Guid providerId, string photoUrl, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.AddBookingEvidence", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@PhotoUrl", photoUrl);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Evidence row was not returned after insert.");
            }
            return ReadEvidence(reader);
        }
        catch (SqlException exception) when (exception.Number == 51150)
        {
            throw new BookingNotFoundException(bookingId);
        }
    }

    public async Task<IReadOnlyList<BookingEvidenceResult>> ListEvidenceAsync(
        Guid bookingId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.ListBookingEvidence", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);

        var rows = new List<BookingEvidenceResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadEvidence(reader));
        }
        return rows;
    }

    public async Task<BookingPrescriptionResult> UpsertPrescriptionAsync(
        Guid bookingId,
        Guid providerId,
        string? prescriptionText,
        bool isPetVaccinated,
        IReadOnlyList<string> vaccinations,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.UpsertBookingPrescription", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@PrescriptionText",
            prescriptionText is null ? DBNull.Value : (object)prescriptionText);
        command.Parameters.AddWithValue("@IsPetVaccinated", isPetVaccinated);
        command.Parameters.AddWithValue("@Vaccinations",
            vaccinations.Count == 0 ? DBNull.Value : (object)SerializeVaccinations(vaccinations));

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Prescription row was not returned after upsert.");
            }
            return ReadPrescription(reader);
        }
        catch (SqlException exception) when (exception.Number == 51290)
        {
            throw new BookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51291)
        {
            throw new BookingPrescriptionForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51292)
        {
            throw new BookingPrescriptionNotVetException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51293)
        {
            throw new BookingPrescriptionInvalidStateException(bookingId);
        }
    }

    public async Task<BookingModificationResult?> GetPendingModificationAsync(
        Guid bookingId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.GetPendingBookingModification", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new BookingModificationResult(
            BookingModificationId: reader.GetGuid(0),
            BookingId: reader.GetGuid(1),
            RequestedByActor: reader.GetString(2),
            RequestedByActorId: reader.GetGuid(3),
            ProposedBookingDate: DateOnly.FromDateTime(reader.GetDateTime(4)),
            ProposedStartTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(5)),
            ProposedEndTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(6)),
            Note: reader.IsDBNull(7) ? null : reader.GetString(7),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero),
            AcknowledgedTerms: ReadAcknowledgedTerms(reader, offset: 9, includeStayTimes: false));
    }

    internal static StartOtpResult ReadStartOtp(SqlDataReader reader) =>
        new(BookingStartOtpId: reader.GetGuid(0),
            BookingId: reader.GetGuid(1),
            OtpCode: reader.GetString(2),
            Status: reader.GetString(3),
            IssuedAtUtc: new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
            ExpiresAtUtc: new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero));

    internal static BookingEvidenceResult ReadEvidence(SqlDataReader reader) =>
        new(BookingEvidenceId: reader.GetGuid(0),
            BookingId: reader.GetGuid(1),
            PhotoUrl: reader.GetString(2),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero));

    // Binds the five @SnapshotProvider* create-sproc params from the resolved
    // provider address (ProviderLocation bookings). All null for ParentLocation /
    // no-location bookings — the sproc then snapshots the parent's address (or none).
    internal static void AddProviderAddressSnapshotParameters(
        SqlCommand command, ProviderAddressSnapshot? providerAddress)
    {
        command.Parameters.AddWithValue("@SnapshotProviderAddressLine",
            providerAddress?.AddressLine is { } line ? (object)line : DBNull.Value);
        command.Parameters.AddWithValue("@SnapshotProviderCity",
            providerAddress?.City is { } city ? (object)city : DBNull.Value);
        command.Parameters.AddWithValue("@SnapshotProviderZipCode",
            providerAddress?.ZipCode is { } zip ? (object)zip : DBNull.Value);
        command.Parameters.AddWithValue("@SnapshotProviderLatitude",
            providerAddress?.Latitude is { } lat ? (object)lat : DBNull.Value);
        command.Parameters.AddWithValue("@SnapshotProviderLongitude",
            providerAddress?.Longitude is { } lng ? (object)lng : DBNull.Value);
    }

    // Binds the @Acknowledged* modification-request params. @HasAcknowledgedTerms
    // is the discriminator the accept-side sproc keys off — a null term set means
    // "nothing staged, leave the booking's frozen terms alone", which is NOT the
    // same as staging NULLs (a null cancellation policy means "no restriction").
    // The price parameter is named per booking kind (per hour vs per night), and
    // only a stay carries drop-off / pick-up times.
    internal static void AddAcknowledgedTermsParameters(
        SqlCommand command,
        BookingAcknowledgedTerms? terms,
        string priceParameterName,
        bool includeStayTimes)
    {
        command.Parameters.AddWithValue("@HasAcknowledgedTerms", terms is not null);
        command.Parameters.AddWithValue(priceParameterName,
            terms?.UnitPrice is { } price ? (object)price : DBNull.Value);
        command.Parameters.AddWithValue("@AcknowledgedCancellationPolicyHours",
            terms?.CancellationPolicyHours is { } hours ? (object)hours : DBNull.Value);
        if (includeStayTimes)
        {
            command.Parameters.AddWithValue("@AcknowledgedDropOffTime",
                terms?.DropOffTime is { } dropOff ? (object)dropOff.ToTimeSpan() : DBNull.Value);
            command.Parameters.AddWithValue("@AcknowledgedPickUpTime",
                terms?.PickUpTime is { } pickUp ? (object)pickUp.ToTimeSpan() : DBNull.Value);
        }

        command.Parameters.AddWithValue("@AcknowledgedAddressLine",
            terms?.AddressLine is { } line ? (object)line : DBNull.Value);
        command.Parameters.AddWithValue("@AcknowledgedCity",
            terms?.City is { } city ? (object)city : DBNull.Value);
        command.Parameters.AddWithValue("@AcknowledgedZipCode",
            terms?.ZipCode is { } zip ? (object)zip : DBNull.Value);
        command.Parameters.AddWithValue("@AcknowledgedLatitude",
            terms?.Latitude is { } lat ? (object)lat : DBNull.Value);
        command.Parameters.AddWithValue("@AcknowledgedLongitude",
            terms?.Longitude is { } lng ? (object)lng : DBNull.Value);
    }

    // Reads the acknowledged-terms tail of a GetPending*Modification result set.
    // Returns null when nothing was staged. Column indexes are 0-based from the
    // HasAcknowledgedTerms column; a stay's set includes drop-off / pick-up.
    internal static BookingAcknowledgedTerms? ReadAcknowledgedTerms(
        SqlDataReader reader, int offset, bool includeStayTimes)
    {
        if (reader.IsDBNull(offset) || !reader.GetBoolean(offset))
        {
            return null;
        }

        var priceIndex = offset + 1;
        var policyIndex = offset + 2;
        var next = offset + 3;
        TimeOnly? dropOff = null;
        TimeOnly? pickUp = null;
        if (includeStayTimes)
        {
            dropOff = reader.IsDBNull(next) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(next));
            pickUp = reader.IsDBNull(next + 1) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(next + 1));
            next += 2;
        }

        return new BookingAcknowledgedTerms(
            UnitPrice: reader.IsDBNull(priceIndex) ? null : reader.GetDecimal(priceIndex),
            CancellationPolicyHours: reader.IsDBNull(policyIndex) ? null : reader.GetInt32(policyIndex),
            DropOffTime: dropOff,
            PickUpTime: pickUp,
            AddressLine: reader.IsDBNull(next) ? null : reader.GetString(next),
            City: reader.IsDBNull(next + 1) ? null : reader.GetString(next + 1),
            ZipCode: reader.IsDBNull(next + 2) ? null : reader.GetString(next + 2),
            Latitude: reader.IsDBNull(next + 3) ? null : reader.GetDecimal(next + 3),
            Longitude: reader.IsDBNull(next + 4) ? null : reader.GetDecimal(next + 4));
    }

    private static BookingResult ReadBookingRow(SqlDataReader reader)
    {
        return new BookingResult(
            BookingId: reader.GetGuid(0),
            ProviderId: reader.GetGuid(1),
            PetParentId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
            ServiceId: reader.GetGuid(3),
            ServiceCategory: reader.GetString(4),
            SubCategory: reader.GetString(5),
            BookingDate: DateOnly.FromDateTime(reader.GetDateTime(6)),
            StartTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(7)),
            EndTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(8)),
            Status: reader.GetString(9),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero),
            UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero),
            CancelledAtUtc: reader.IsDBNull(12)
                ? null
                : new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
            ServiceItemCode: reader.IsDBNull(13) ? null : reader.GetString(13),
            Source: reader.GetString(14),
            CustomerName: reader.IsDBNull(15) ? null : reader.GetString(15),
            CustomerMobileCountryCode: reader.IsDBNull(16) ? null : reader.GetString(16),
            CustomerMobile: reader.IsDBNull(17) ? null : reader.GetString(17),
            AnimalType: reader.IsDBNull(18) ? null : reader.GetString(18),
            PetName: reader.IsDBNull(19) ? null : reader.GetString(19),
            ServiceLocation: reader.IsDBNull(20) ? null : reader.GetString(20),
            CustomerLocation: reader.IsDBNull(21) ? null : reader.GetString(21),
            PricePerHour: reader.IsDBNull(22) ? null : reader.GetDecimal(22),
            JobNotes: reader.IsDBNull(23) ? null : reader.GetString(23),
            PetId: reader.IsDBNull(24) ? null : reader.GetGuid(24));
    }

    private static BookingDetailRow ReadDetailRow(SqlDataReader reader)
    {
        return new BookingDetailRow(
            BookingId: reader.GetGuid(0),
            JobNumber: reader.GetInt32(1),
            ProviderId: reader.GetGuid(2),
            PetParentId: reader.IsDBNull(3) ? null : reader.GetGuid(3),
            ServiceId: reader.GetGuid(4),
            ServiceCategory: reader.GetString(5),
            SubCategory: reader.GetString(6),
            BookingDate: DateOnly.FromDateTime(reader.GetDateTime(7)),
            StartTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(8)),
            EndTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(9)),
            Status: reader.GetString(10),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero),
            UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
            CancelledAtUtc: reader.IsDBNull(13)
                ? null
                : new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero),
            ServiceItemCode: reader.IsDBNull(14) ? null : reader.GetString(14),
            Source: reader.GetString(15),
            CustomerName: reader.IsDBNull(16) ? null : reader.GetString(16),
            CustomerMobileCountryCode: reader.IsDBNull(17) ? null : reader.GetString(17),
            CustomerMobile: reader.IsDBNull(18) ? null : reader.GetString(18),
            AnimalType: reader.IsDBNull(19) ? null : reader.GetString(19),
            PetName: reader.IsDBNull(20) ? null : reader.GetString(20),
            ServiceLocation: reader.IsDBNull(21) ? null : reader.GetString(21),
            CustomerLocation: reader.IsDBNull(22) ? null : reader.GetString(22),
            PricePerHour: reader.IsDBNull(23) ? null : reader.GetDecimal(23),
            JobNotes: reader.IsDBNull(24) ? null : reader.GetString(24),
            PetId: reader.IsDBNull(25) ? null : reader.GetGuid(25),
            PayoutStatus: reader.GetString(26),
            PayoutId: reader.IsDBNull(27) ? null : reader.GetString(27),
            ParentFirstName: reader.IsDBNull(28) ? null : reader.GetString(28),
            ParentLastName: reader.IsDBNull(29) ? null : reader.GetString(29),
            ParentGender: reader.IsDBNull(30) ? null : reader.GetString(30),
            ParentMobileCountryCode: reader.IsDBNull(31) ? null : reader.GetString(31),
            ParentMobileNumber: reader.IsDBNull(32) ? null : reader.GetString(32),
            ParentPhotoUrl: reader.IsDBNull(33) ? null : reader.GetString(33),
            PetProfileName: reader.IsDBNull(34) ? null : reader.GetString(34),
            PetType: reader.IsDBNull(35) ? null : reader.GetString(35),
            PetGender: reader.IsDBNull(36) ? null : reader.GetString(36),
            PetPhotoUrl: reader.IsDBNull(37) ? null : reader.GetString(37),
            ProviderFirstName: reader.IsDBNull(38) ? null : reader.GetString(38),
            ProviderLastName: reader.IsDBNull(39) ? null : reader.GetString(39),
            ProviderGender: reader.IsDBNull(40) ? null : reader.GetString(40),
            ProviderMobileCountryCode: reader.IsDBNull(41) ? null : reader.GetString(41),
            ProviderMobileNumber: reader.IsDBNull(42) ? null : reader.GetString(42),
            PetBreed: reader.IsDBNull(43) ? null : reader.GetString(43),
            PetVaccinationStatus: reader.IsDBNull(44) ? null : reader.GetString(44),
            PetVaccinationType: reader.IsDBNull(45) ? null : reader.GetString(45),
            PetVaccinationDose: reader.IsDBNull(46) ? null : reader.GetString(46),
            PetPrescription: reader.IsDBNull(47) ? null : reader.GetString(47),
            PetSterilizationStatus: reader.IsDBNull(48) ? null : reader.GetString(48),
            PetMedicalHistory: reader.IsDBNull(49) ? null : reader.GetString(49),
            PetTemperament: reader.IsDBNull(50) ? null : reader.GetString(50),
            LocationType: reader.IsDBNull(51) ? null : reader.GetString(51),
            ParentAddressLine: reader.IsDBNull(52) ? null : reader.GetString(52),
            ParentCity: reader.IsDBNull(53) ? null : reader.GetString(53),
            ParentZipCode: reader.IsDBNull(54) ? null : reader.GetString(54),
            ParentLatitude: reader.IsDBNull(55) ? null : reader.GetDecimal(55),
            ParentLongitude: reader.IsDBNull(56) ? null : reader.GetDecimal(56),
            HasPrescription: reader.GetInt32(57) == 1,
            PrescriptionText: reader.IsDBNull(58) ? null : reader.GetString(58),
            IsPetVaccinated: reader.IsDBNull(59) ? null : reader.GetBoolean(59),
            PrescriptionVaccinations: reader.IsDBNull(60) ? null : DeserializeVaccinations(reader.GetString(60)),
            NextConsultationDate: reader.IsDBNull(61) ? null : DateOnly.FromDateTime(reader.GetDateTime(61)),
            // Snapshots (appended last on the sproc SELECT).
            CancellationPolicyHours: reader.IsDBNull(62) ? null : reader.GetInt32(62),
            SnapshotAddressLine: reader.IsDBNull(63) ? null : reader.GetString(63),
            SnapshotCity: reader.IsDBNull(64) ? null : reader.GetString(64),
            SnapshotZipCode: reader.IsDBNull(65) ? null : reader.GetString(65),
            SnapshotLatitude: reader.IsDBNull(66) ? null : reader.GetDecimal(66),
            SnapshotLongitude: reader.IsDBNull(67) ? null : reader.GetDecimal(67),
            // Payment ledger join — null until the booking is marked PAID.
            PayoutMethod: reader.IsDBNull(68) ? null : reader.GetString(68),
            PaidAtUtc: reader.IsDBNull(69)
                ? null
                : new DateTimeOffset(reader.GetDateTime(69), TimeSpan.Zero));
    }

    private static BookingPrescriptionResult ReadPrescription(SqlDataReader reader) =>
        new(BookingId: reader.GetGuid(0),
            PrescriptionText: reader.IsDBNull(1) ? null : reader.GetString(1),
            IsPetVaccinated: reader.GetBoolean(2),
            Vaccinations: reader.IsDBNull(3) ? Array.Empty<string>() : DeserializeVaccinations(reader.GetString(3)),
            NextConsultationDate: reader.IsDBNull(4) ? null : DateOnly.FromDateTime(reader.GetDateTime(4)),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero),
            UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(6), TimeSpan.Zero));

    // Vaccinations are stored as a JSON array of names in a single column. The app
    // owns the (de)serialization — System.Text.Json, consistent with the rest of
    // the codebase.
    private static string SerializeVaccinations(IReadOnlyList<string> vaccinations) =>
        JsonSerializer.Serialize(vaccinations);

    private static IReadOnlyList<string> DeserializeVaccinations(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch (JsonException)
        {
            // Defensive: a malformed value degrades to an empty list rather than
            // failing the whole detail read.
            return Array.Empty<string>();
        }
    }

    private async Task<string> GetConnectionStringAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString;
        }

        if (secretProvider is null)
        {
            throw new InvalidOperationException(
                "SQL Server connection string is not configured and no secret provider is registered.");
        }

        return await secretProvider.GetSqlConnectionStringAsync(cancellationToken);
    }
}
