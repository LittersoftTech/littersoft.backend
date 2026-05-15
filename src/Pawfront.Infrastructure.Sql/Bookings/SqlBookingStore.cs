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
        string serviceCategory,
        string subCategory,
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
        command.Parameters.AddWithValue("@ServiceCategory", serviceCategory);
        command.Parameters.AddWithValue("@SubCategory", subCategory);
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
            throw new BookingCapacityExceededException(providerId, bookingDate, startTime, endTime);
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
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.ListBookingsByProvider", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);

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
        Guid providerId,
        DateOnly bookingDate,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.GetBookingsForDate", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
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

    private static BookingResult ReadBookingRow(SqlDataReader reader)
    {
        return new BookingResult(
            BookingId: reader.GetGuid(0),
            ProviderId: reader.GetGuid(1),
            PetParentId: reader.GetGuid(2),
            ServiceCategory: reader.GetString(3),
            SubCategory: reader.GetString(4),
            BookingDate: DateOnly.FromDateTime(reader.GetDateTime(5)),
            StartTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(6)),
            EndTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(7)),
            Status: reader.GetString(8),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(9), TimeSpan.Zero),
            UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero),
            CancelledAtUtc: reader.IsDBNull(11)
                ? null
                : new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero));
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
