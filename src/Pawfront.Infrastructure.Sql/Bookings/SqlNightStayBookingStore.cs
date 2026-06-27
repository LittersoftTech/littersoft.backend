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
