using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Events;

namespace Pawfront.Infrastructure.Sql.Events;

internal sealed class SqlEventStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IEventSqlStore
{
    public async Task<EventSqlSnapshot> CreateAsync(
        CreateEventSqlInput input,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.CreateEvent", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@ProviderId", input.ProviderId);
        command.Parameters.AddWithValue("@EventCategory", input.EventCategory);
        command.Parameters.AddWithValue("@IsChildFriendly", input.IsChildFriendly);
        command.Parameters.AddWithValue("@Title", input.Title);
        command.Parameters.AddWithValue("@Description", input.Description);
        command.Parameters.AddWithValue("@BannerImageUrl", DbValue(input.BannerImageUrl));
        command.Parameters.AddWithValue("@EventType", input.EventType);
        command.Parameters.AddWithValue("@StartDate", input.StartDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@EndDate", input.EndDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@StartTime", input.StartTime.ToTimeSpan());
        command.Parameters.AddWithValue("@EndTime", input.EndTime.ToTimeSpan());
        command.Parameters.AddWithValue("@AmenitiesJson", JsonSerializer.Serialize(input.Amenities));

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Event row was not returned after create.");
            }

            var snapshot = ReadEventRow(reader, amenities: Array.Empty<string>());
            var amenities = await ReadAmenitiesAsync(reader, cancellationToken);

            return snapshot with { Amenities = amenities };
        }
        catch (SqlException exception) when (exception.Number == 51030)
        {
            throw new EventProviderNotFoundException(input.ProviderId);
        }
    }

    public async Task<EventSqlSnapshot?> GetAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.GetEvent", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@EventId", eventId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var snapshot = ReadEventRow(reader, amenities: Array.Empty<string>());
        var amenities = await ReadAmenitiesAsync(reader, cancellationToken);

        return snapshot with { Amenities = amenities };
    }

    public async Task<IReadOnlyList<EventSqlSnapshot>> ListByProviderAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.ListEventsByProvider", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1: event rows.
        var rows = new List<EventSqlSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadEventRow(reader, amenities: Array.Empty<string>()));
        }

        // Result set 2: (EventId, Amenity) pairs — fold into the rows above.
        var amenityLookup = new Dictionary<Guid, List<string>>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var eventId = reader.GetGuid(0);
                var amenity = reader.GetString(1);
                if (!amenityLookup.TryGetValue(eventId, out var list))
                {
                    list = new List<string>();
                    amenityLookup[eventId] = list;
                }
                list.Add(amenity);
            }
        }

        var hydrated = new List<EventSqlSnapshot>(rows.Count);
        foreach (var row in rows)
        {
            hydrated.Add(amenityLookup.TryGetValue(row.EventId, out var amenities)
                ? row with { Amenities = amenities }
                : row);
        }
        return hydrated;
    }

    private static EventSqlSnapshot ReadEventRow(SqlDataReader reader, IReadOnlyCollection<string> amenities)
    {
        return new EventSqlSnapshot(
            EventId: reader.GetGuid(0),
            ProviderId: reader.GetGuid(1),
            EventCategory: reader.GetString(2),
            IsChildFriendly: reader.GetBoolean(3),
            Title: reader.GetString(4),
            Description: reader.GetString(5),
            BannerImageUrl: reader.IsDBNull(6) ? null : reader.GetString(6),
            Amenities: amenities,
            EventType: reader.GetString(7),
            StartDate: DateOnly.FromDateTime(reader.GetDateTime(8)),
            EndDate: DateOnly.FromDateTime(reader.GetDateTime(9)),
            StartTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(10)),
            EndTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(11)),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(12), TimeSpan.Zero),
            UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero));
    }

    private static async Task<List<string>> ReadAmenitiesAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var amenities = new List<string>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                amenities.Add(reader.GetString(0));
            }
        }
        return amenities;
    }

    public async Task<EventCounters> IncrementCounterAsync(
        Guid eventId,
        string counterType,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.IncrementEventCounter", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@EventId", eventId);
        command.Parameters.AddWithValue("@CounterType", counterType);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Counter row was not returned after increment.");
            }
            return new EventCounters(
                ViewCount: reader.GetInt32(0),
                ShareCount: reader.GetInt32(1),
                InquiryCount: reader.GetInt32(2));
        }
        catch (SqlException exception) when (exception.Number == 51096)
        {
            throw new EventNotFoundException(eventId);
        }
        catch (SqlException exception) when (exception.Number == 51097)
        {
            throw new ArgumentException(exception.Message, nameof(counterType));
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
