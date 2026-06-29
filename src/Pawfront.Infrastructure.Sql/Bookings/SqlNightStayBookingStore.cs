using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Bookings;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Sql.Bookings;

internal sealed class SqlNightStayBookingStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : INightStayBookingSqlStore
{
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
        int capacity,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.CreateNightStayBooking", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@PetParentId", petParentId);
        command.Parameters.AddWithValue("@PetId", petId is null ? DBNull.Value : (object)petId.Value);
        command.Parameters.AddWithValue("@ServiceId", serviceId);
        command.Parameters.AddWithValue("@ServiceCategory", serviceCategory);
        command.Parameters.AddWithValue("@SubCategory", subCategory);
        command.Parameters.AddWithValue("@CheckInDate", checkInDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@CheckOutDate", checkOutDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@DropOffTime", dropOffTime.ToTimeSpan());
        command.Parameters.AddWithValue("@PickUpTime", pickUpTime.ToTimeSpan());
        command.Parameters.AddWithValue("@Capacity", capacity);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Night stay booking row was not returned after create.");
            }
            return ReadRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51230)
        {
            throw new BookingProviderNotFoundException(providerId);
        }
        catch (SqlException exception) when (exception.Number == 51231)
        {
            throw new BookingProviderInactiveException(providerId);
        }
        catch (SqlException exception) when (exception.Number == 51232)
        {
            throw new BookingPetParentNotFoundException(petParentId);
        }
        catch (SqlException exception) when (exception.Number == 51233)
        {
            throw new BookingPetInvalidException(petId!.Value, petParentId);
        }
        catch (SqlException exception) when (exception.Number == 51234)
        {
            throw new BookingServiceInvalidException(serviceId, providerId);
        }
        catch (SqlException exception) when (exception.Number == 51235)
        {
            throw new NightStayCapacityExceededException(serviceId, checkInDate, checkOutDate);
        }
    }

    public async Task<NightStayBookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.GetNightStayBooking", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@NightStayBookingId", bookingId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return ReadRow(reader);
    }

    public async Task<NightStayBookingDetailRow?> GetDetailAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.GetNightStayBookingDetail", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@NightStayBookingId", bookingId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return ReadDetailRow(reader);
    }

    public async Task<NightStayBookingResult> CancelAsync(
        Guid bookingId,
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.CancelNightStayBooking", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@NightStayBookingId", bookingId);
        command.Parameters.AddWithValue("@PetParentId", petParentId);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Night stay booking row was not returned after cancel.");
            }
            return ReadRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51236)
        {
            throw new NightStayBookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51237)
        {
            throw new NightStayBookingCancellationForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51238)
        {
            throw new NightStayBookingAlreadyCancelledException(bookingId);
        }
    }

    public async Task<IReadOnlyList<NightStayBookingResult>> ListByProviderAsync(
        Guid providerId,
        DateOnly? onDate,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.ListNightStayBookingsByProvider", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@ServiceId", DBNull.Value);
        command.Parameters.AddWithValue("@OnDate",
            onDate is null ? DBNull.Value : (object)onDate.Value.ToDateTime(TimeOnly.MinValue));

        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<NightStayBookingResult>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.ListNightStayBookingsByPetParent", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@PetParentId", petParentId);

        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task<NightStayBookingResult> UpdateStatusAsync(
        Guid bookingId,
        string newStatus,
        BookingStatusActor actor,
        Guid actorId,
        string? note,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.UpdateNightStayBookingStatus", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@NightStayBookingId", bookingId);
        command.Parameters.AddWithValue("@NewStatus", newStatus);
        command.Parameters.AddWithValue("@Actor", actor.ToString());
        command.Parameters.AddWithValue("@ActorId", actorId);
        command.Parameters.AddWithValue("@Note", note is null ? DBNull.Value : (object)note);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Night stay booking row was not returned after status update.");
            }
            return ReadRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51240)
        {
            throw new NightStayBookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51241)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51242)
        {
            throw new BookingStatusNotAllowedException(newStatus, actor);
        }
        catch (SqlException exception) when (exception.Number == 51243)
        {
            throw new BookingStatusTerminalException(bookingId, newStatus);
        }
        catch (SqlException exception) when (exception.Number == 51244)
        {
            throw new BookingStatusUnchangedException(bookingId, newStatus);
        }
        catch (SqlException exception) when (exception.Number == 51245)
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

        await using var command = new SqlCommand("Booking.ListNightStayBookingStatusHistory", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@NightStayBookingId", bookingId);

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
        await using var command = new SqlCommand("Booking.IssueNightStayBookingStartOtp", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@NightStayBookingId", bookingId);
        command.Parameters.AddWithValue("@NewCode", newCode);
        command.Parameters.AddWithValue("@TtlMinutes", ttlMinutes);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Start OTP row was not returned.");
            }
            return SqlBookingStore.ReadStartOtp(reader);
        }
        catch (SqlException exception) when (exception.Number == 51250)
        {
            throw new NightStayBookingNotFoundException(bookingId);
        }
    }

    public async Task<NightStayBookingResult> StartWithOtpAsync(
        Guid bookingId, Guid providerId, string otpCode, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.StartNightStayBookingWithOtp", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@NightStayBookingId", bookingId);
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@OtpCode", otpCode);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Night stay booking row was not returned after start.");
            }
            return ReadRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51251)
        {
            throw new NightStayBookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51252)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51253)
        {
            throw new BookingNotStartableException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51254)
        {
            throw new InvalidStartOtpException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51255)
        {
            throw new StartOtpExpiredException(bookingId);
        }
    }

    public async Task<NightStayBookingResult> RequestModificationAsync(
        Guid bookingId, BookingStatusActor actor, Guid actorId,
        DateOnly checkInDate, DateOnly checkOutDate, string? note, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.RequestNightStayBookingModification", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@NightStayBookingId", bookingId);
        command.Parameters.AddWithValue("@Actor", actor.ToString());
        command.Parameters.AddWithValue("@ActorId", actorId);
        command.Parameters.AddWithValue("@ProposedCheckInDate", checkInDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@ProposedCheckOutDate", checkOutDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@Note", note is null ? DBNull.Value : (object)note);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Night stay booking row was not returned after modification request.");
            }
            return ReadRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51260)
        {
            throw new NightStayBookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51261)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51262)
        {
            throw new BookingNotModifiableException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51263)
        {
            throw new BookingModificationConflictException(bookingId);
        }
    }

    public async Task<NightStayBookingModificationResult?> GetPendingModificationAsync(
        Guid bookingId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.GetPendingNightStayBookingModification", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@NightStayBookingId", bookingId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new NightStayBookingModificationResult(
            NightStayBookingModificationId: reader.GetGuid(0),
            NightStayBookingId: reader.GetGuid(1),
            RequestedByActor: reader.GetString(2),
            RequestedByActorId: reader.GetGuid(3),
            ProposedCheckInDate: DateOnly.FromDateTime(reader.GetDateTime(4)),
            ProposedCheckOutDate: DateOnly.FromDateTime(reader.GetDateTime(5)),
            Note: reader.IsDBNull(6) ? null : reader.GetString(6),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero));
    }

    public async Task<NightStayBookingResult> RespondModificationAsync(
        Guid bookingId, BookingStatusActor actor, Guid actorId,
        bool accept, int capacity, string? note, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.RespondNightStayBookingModification", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@NightStayBookingId", bookingId);
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
                throw new InvalidOperationException("Night stay booking row was not returned after modification response.");
            }
            return ReadRow(reader);
        }
        catch (SqlException exception) when (exception.Number == 51265)
        {
            throw new NightStayBookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51266)
        {
            throw new BookingStatusForbiddenException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51267)
        {
            throw new NoPendingModificationException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51268)
        {
            throw new BookingModificationCapacityException(bookingId);
        }
    }

    public async Task<BookingEvidenceResult> AddEvidenceAsync(
        Guid bookingId, Guid providerId, string photoUrl, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.AddNightStayBookingEvidence", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@NightStayBookingId", bookingId);
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@PhotoUrl", photoUrl);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Evidence row was not returned after insert.");
            }
            return SqlBookingStore.ReadEvidence(reader);
        }
        catch (SqlException exception) when (exception.Number == 51270)
        {
            throw new NightStayBookingNotFoundException(bookingId);
        }
    }

    public async Task<IReadOnlyList<BookingEvidenceResult>> ListEvidenceAsync(
        Guid bookingId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Booking.ListNightStayBookingEvidence", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@NightStayBookingId", bookingId);

        var rows = new List<BookingEvidenceResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(SqlBookingStore.ReadEvidence(reader));
        }
        return rows;
    }

    private static async Task<IReadOnlyList<NightStayBookingResult>> ReadAllAsync(
        SqlCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<NightStayBookingResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadRow(reader));
        }
        return rows;
    }

    private static NightStayBookingResult ReadRow(SqlDataReader reader) =>
        new(NightStayBookingId: reader.GetGuid(0),
            ProviderId: reader.GetGuid(1),
            PetParentId: reader.GetGuid(2),
            ServiceId: reader.GetGuid(3),
            ServiceCategory: reader.GetString(4),
            SubCategory: reader.GetString(5),
            CheckInDate: DateOnly.FromDateTime(reader.GetDateTime(6)),
            CheckOutDate: DateOnly.FromDateTime(reader.GetDateTime(7)),
            DropOffTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(8)),
            PickUpTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(9)),
            Status: reader.GetString(10),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero),
            UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
            CancelledAtUtc: reader.IsDBNull(13)
                ? null
                : new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero),
            PetId: reader.IsDBNull(14) ? null : reader.GetGuid(14));

    private static NightStayBookingDetailRow ReadDetailRow(SqlDataReader reader) =>
        new(NightStayBookingId: reader.GetGuid(0),
            JobNumber: reader.GetInt32(1),
            ProviderId: reader.GetGuid(2),
            PetParentId: reader.GetGuid(3),
            ServiceId: reader.GetGuid(4),
            ServiceCategory: reader.GetString(5),
            SubCategory: reader.GetString(6),
            CheckInDate: DateOnly.FromDateTime(reader.GetDateTime(7)),
            CheckOutDate: DateOnly.FromDateTime(reader.GetDateTime(8)),
            DropOffTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(9)),
            PickUpTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(10)),
            Status: reader.GetString(11),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
            UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero),
            CancelledAtUtc: reader.IsDBNull(14)
                ? null
                : new DateTimeOffset(reader.GetDateTime(14), TimeSpan.Zero),
            PetId: reader.IsDBNull(15) ? null : reader.GetGuid(15),
            PayoutStatus: reader.GetString(16),
            PayoutId: reader.IsDBNull(17) ? null : reader.GetString(17),
            ParentFirstName: reader.IsDBNull(18) ? null : reader.GetString(18),
            ParentLastName: reader.IsDBNull(19) ? null : reader.GetString(19),
            ParentGender: reader.IsDBNull(20) ? null : reader.GetString(20),
            ParentMobileCountryCode: reader.IsDBNull(21) ? null : reader.GetString(21),
            ParentMobileNumber: reader.IsDBNull(22) ? null : reader.GetString(22),
            ParentPhotoUrl: reader.IsDBNull(23) ? null : reader.GetString(23),
            PetProfileName: reader.IsDBNull(24) ? null : reader.GetString(24),
            PetType: reader.IsDBNull(25) ? null : reader.GetString(25),
            PetGender: reader.IsDBNull(26) ? null : reader.GetString(26),
            PetPhotoUrl: reader.IsDBNull(27) ? null : reader.GetString(27));

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
