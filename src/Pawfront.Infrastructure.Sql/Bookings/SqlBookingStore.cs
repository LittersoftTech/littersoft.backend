using System.Data;
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

    public async Task<IReadOnlyList<BookingResult>> ListByPetParentAsync(
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

        return await ReadAllAsync(command, cancellationToken);
    }

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

    public async Task<BookingResult> StartWithOtpAsync(
        Guid bookingId, Guid providerId, string otpCode, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.StartBookingWithOtp", connection)
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
        catch (SqlException exception) when (exception.Number == 51134)
        {
            throw new InvalidStartOtpException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51135)
        {
            throw new StartOtpExpiredException(bookingId);
        }
    }

    public async Task<BookingResult> RequestModificationAsync(
        Guid bookingId, BookingStatusActor actor, Guid actorId,
        DateOnly bookingDate, TimeOnly startTime, TimeOnly endTime,
        string? note, CancellationToken cancellationToken)
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
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero));
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
            PetPhotoUrl: reader.IsDBNull(37) ? null : reader.GetString(37));
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
