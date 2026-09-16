using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Bookings;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Sql.Bookings;

/// <summary>
/// SQL implementation of <see cref="IBookingLocationService"/> — the geolocation
/// fixes that do NOT ride inside a transition procedure. Everything else is written
/// by the procedure that performs the transition, so this class handles only the
/// standalone cases: a party's own position for a moment the counterparty drove,
/// and the log-only "Cash Not Received".
/// </summary>
internal sealed class SqlBookingLocationService(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IBookingLocationService
{
    public async Task<BookingLocationEventResult> RecordAsync(
        RecordBookingLocationCommand command,
        CancellationToken cancellationToken)
    {
        var trigger = BookingLocationTriggers.NormalizeRecordable(command.Trigger);
        var location = CapturedLocation.Require(command.Location, "record your location");
        var isNightStay = string.Equals(command.BookingType, BookingTypes.NightStay, StringComparison.Ordinal);

        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        var procedure = isNightStay
            ? "Booking.RecordNightStayBookingLocationEvent"
            : "Booking.RecordBookingLocationEvent";

        await using var sqlCommand = new SqlCommand(procedure, connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        sqlCommand.Parameters.AddWithValue(
            isNightStay ? "@NightStayBookingId" : "@BookingId", command.BookingId);
        sqlCommand.Parameters.AddWithValue("@Trigger", trigger);
        sqlCommand.Parameters.AddWithValue("@CapturedByType", command.Actor.ToString());
        sqlCommand.Parameters.AddWithValue("@CapturedById", command.ActorId);
        LocationParameters.Add(sqlCommand, location);

        try
        {
            await using var reader = await sqlCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Location event row was not returned.");
            }

            return ReadRow(reader);
        }
        // 51360/51363 not found, 51361/51364 not a party, 51362/51365 bad trigger.
        catch (SqlException exception) when (exception.Number is 51360)
        {
            throw new BookingNotFoundException(command.BookingId);
        }
        catch (SqlException exception) when (exception.Number is 51363)
        {
            throw new NightStayBookingNotFoundException(command.BookingId);
        }
        catch (SqlException exception) when (exception.Number is 51361 or 51364)
        {
            throw new BookingStatusForbiddenException(command.BookingId);
        }
        catch (SqlException exception) when (exception.Number is 51362 or 51365)
        {
            // Defensive: NormalizeRecordable above already rejected anything the
            // procedure would refuse here.
            throw new UnsupportedBookingLocationTriggerException(command.Trigger);
        }
    }

    public async Task<IReadOnlyList<BookingLocationEventResult>> ListAsync(
        string bookingType,
        Guid bookingId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Booking.ListBookingLocationEvents", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue(
            "@BookingType", BookingTypes.Normalize(bookingType) ?? BookingTypes.SingleDay);
        command.Parameters.AddWithValue("@BookingId", bookingId);

        var results = new List<BookingLocationEventResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadRow(reader));
        }

        return results;
    }

    private static BookingLocationEventResult ReadRow(SqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetGuid(4),
        reader.GetDecimal(5),
        reader.GetDecimal(6),
        reader.IsDBNull(7) ? null : reader.GetDecimal(7),
        reader.IsDBNull(8) ? null : new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero),
        new DateTimeOffset(reader.GetDateTime(9), TimeSpan.Zero));

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
