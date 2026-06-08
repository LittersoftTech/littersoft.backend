using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;
using Pawfront.Application.Configuration;
using Pawfront.Application.Events;

namespace Pawfront.Infrastructure.Sql.Events;

internal sealed class SqlEventBookingStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IEventBookingSqlStore
{
    private static readonly SqlMetaData[] AttendeeNameRowMetadata =
    {
        new("TicketNumber", SqlDbType.Int),
        new("AttendeeName", SqlDbType.NVarChar, 200)
    };

    public async Task<EventBookingResult> CreateAsync(
        CreateEventBookingSqlInput input,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.CreateEventBooking", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@EventId", input.EventId);
        command.Parameters.AddWithValue("@BookerName", input.BookerName);
        command.Parameters.AddWithValue("@BookerEmail", input.BookerEmail);
        command.Parameters.AddWithValue("@BookerMobile", DbValue(input.BookerMobile));
        command.Parameters.AddWithValue("@PaymentMethod", input.PaymentMethod);
        command.Parameters.AddWithValue("@MaximumCapacity", input.MaximumCapacity);
        command.Parameters.AddWithValue("@TotalAmount", input.TotalAmount);

        var attendeeParam = command.Parameters.AddWithValue("@AttendeeNames", BuildAttendeeTable(input.AttendeeNames));
        attendeeParam.SqlDbType = SqlDbType.Structured;
        attendeeParam.TypeName = "Event.EventBookingAttendeeNames";

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await ReadBookingWithTicketsAsync(reader, cancellationToken)
                ?? throw new InvalidOperationException("Event booking row was not returned after create.");
        }
        catch (SqlException exception) when (exception.Number == 51090)
        {
            throw new EventBookingEventNotFoundException(input.EventId);
        }
        catch (SqlException exception) when (exception.Number == 51091)
        {
            throw new EventBookingCapacityExceededException(input.EventId, input.MaximumCapacity);
        }
        catch (SqlException exception) when (exception.Number == 51094)
        {
            throw new ArgumentException(exception.Message);
        }
    }

    public async Task<IReadOnlyList<EventBookingSummary>> ListByBookerEmailAsync(
        string bookerEmail,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.ListEventBookingsByBookerEmail", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookerEmail", bookerEmail);

        var rows = new List<EventBookingSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new EventBookingSummary(
                BookingId: reader.GetGuid(0),
                EventId: reader.GetGuid(1),
                EventTitle: reader.GetString(2),
                EventCategory: reader.GetString(3),
                EventStartDate: DateOnly.FromDateTime(reader.GetDateTime(4)),
                EventStartTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(5)),
                EventBannerImageUrl: reader.IsDBNull(6) ? null : reader.GetString(6),
                BookerName: reader.GetString(7),
                BookerEmail: reader.GetString(8),
                BookerMobile: reader.IsDBNull(9) ? null : reader.GetString(9),
                TicketCount: reader.GetInt32(10),
                PaymentMethod: reader.GetString(11),
                PaymentStatus: reader.GetString(12),
                PaymentReference: reader.IsDBNull(13) ? null : reader.GetString(13),
                TotalAmount: reader.GetDecimal(14),
                Status: reader.GetString(15),
                CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(16), TimeSpan.Zero),
                UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(17), TimeSpan.Zero),
                CancelledAtUtc: reader.IsDBNull(18)
                    ? null
                    : new DateTimeOffset(reader.GetDateTime(18), TimeSpan.Zero)));
        }
        return rows;
    }

    public async Task<EventBookingResult?> GetAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.GetEventBooking", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await ReadBookingWithTicketsAsync(reader, cancellationToken);
    }

    public async Task<EventBookingResult> ConfirmPaymentAsync(
        Guid bookingId,
        string paymentStatus,
        string? paymentReference,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.ConfirmEventBookingPayment", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@BookingId", bookingId);
        command.Parameters.AddWithValue("@PaymentStatus", paymentStatus);
        command.Parameters.AddWithValue("@PaymentReference", DbValue(paymentReference));

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Event booking row was not returned after confirm.");
            }
            // ConfirmPayment only returns the booking row; tickets are unchanged so
            // we omit them. The endpoint uses a separate GET to surface tickets.
            return ReadBookingRow(reader, tickets: Array.Empty<EventBookingTicket>());
        }
        catch (SqlException exception) when (exception.Number == 51092)
        {
            throw new EventBookingNotFoundException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51093)
        {
            throw new EventBookingPaymentAlreadyConfirmedException(bookingId);
        }
        catch (SqlException exception) when (exception.Number == 51094)
        {
            throw new ArgumentException(exception.Message);
        }
    }

    private static async Task<EventBookingResult?> ReadBookingWithTicketsAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var booking = ReadBookingRow(reader, tickets: Array.Empty<EventBookingTicket>());

        var tickets = new List<EventBookingTicket>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                tickets.Add(new EventBookingTicket(
                    TicketId: reader.GetGuid(0),
                    TicketNumber: reader.GetInt32(3),
                    AttendeeName: reader.GetString(4)));
            }
        }

        return booking with { Tickets = tickets };
    }

    private static EventBookingResult ReadBookingRow(SqlDataReader reader, IReadOnlyList<EventBookingTicket> tickets)
    {
        return new EventBookingResult(
            BookingId: reader.GetGuid(0),
            EventId: reader.GetGuid(1),
            BookerName: reader.GetString(2),
            BookerEmail: reader.GetString(3),
            BookerMobile: reader.IsDBNull(4) ? null : reader.GetString(4),
            TicketCount: reader.GetInt32(5),
            PaymentMethod: reader.GetString(6),
            PaymentStatus: reader.GetString(7),
            PaymentReference: reader.IsDBNull(8) ? null : reader.GetString(8),
            TotalAmount: reader.GetDecimal(9),
            Status: reader.GetString(10),
            Tickets: tickets,
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero),
            UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
            CancelledAtUtc: reader.IsDBNull(13)
                ? null
                : new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero));
    }

    private static IEnumerable<SqlDataRecord> BuildAttendeeTable(IReadOnlyList<string> attendeeNames)
    {
        for (var i = 0; i < attendeeNames.Count; i++)
        {
            var record = new SqlDataRecord(AttendeeNameRowMetadata);
            record.SetInt32(0, i + 1);
            record.SetString(1, attendeeNames[i]);
            yield return record;
        }
    }

    public async Task<IReadOnlyList<EventAttendee>> ListAttendeesAsync(
        Guid providerId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.ListEventAttendees", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@EventId", eventId);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            var attendees = new List<EventAttendee>();
            while (await reader.ReadAsync(cancellationToken))
            {
                attendees.Add(new EventAttendee(
                    TicketId: reader.GetGuid(0),
                    BookingId: reader.GetGuid(1),
                    TicketNumber: reader.GetInt32(2),
                    AttendeeName: reader.GetString(3),
                    BookerName: reader.GetString(4),
                    BookerEmail: reader.GetString(5),
                    BookerMobile: reader.IsDBNull(6) ? null : reader.GetString(6),
                    PaymentMethod: reader.GetString(7),
                    PaymentStatus: reader.GetString(8),
                    TotalAmount: reader.GetDecimal(9),
                    CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero)));
            }
            return attendees;
        }
        catch (SqlException exception) when (exception.Number == 51095)
        {
            throw new EventNotFoundForProviderException(providerId, eventId);
        }
    }

    public async Task<EventMetrics> GetMetricsAsync(
        Guid providerId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.GetEventMetrics", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@EventId", eventId);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Metrics row was not returned.");
            }
            return new EventMetrics(
                Views: reader.GetInt32(0),
                Shares: reader.GetInt32(1),
                Inquiries: reader.GetInt32(2),
                ConfirmedAttendees: reader.GetInt32(3),
                Earnings: reader.GetDecimal(4));
        }
        catch (SqlException exception) when (exception.Number == 51095)
        {
            throw new EventNotFoundForProviderException(providerId, eventId);
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

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;
}
