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
        command.Parameters.AddWithValue("@IsPaid", input.IsPaid);
        command.Parameters.AddWithValue("@Price", input.Price is null ? DBNull.Value : input.Price.Value);
        command.Parameters.AddWithValue("@CancellationPolicy", input.CancellationPolicy);
        command.Parameters.AddWithValue("@EventLink", DbValue(input.EventLink));
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

    public async Task<EventSqlSnapshot> CreateByParentAsync(
        CreateParentEventSqlInput input,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.CreatePetParentEvent", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@PetParentId", input.PetParentId);
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
        command.Parameters.AddWithValue("@IsPaid", input.IsPaid);
        command.Parameters.AddWithValue("@Price", input.Price is null ? DBNull.Value : input.Price.Value);
        command.Parameters.AddWithValue("@CancellationPolicy", input.CancellationPolicy);
        command.Parameters.AddWithValue("@EventLink", DbValue(input.EventLink));
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
        catch (SqlException exception) when (exception.Number == 51207)
        {
            throw new EventPetParentNotFoundException(input.PetParentId);
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

        return await ReadEventDetailAsync(reader, cancellationToken);
    }

    public async Task<EventSqlSnapshot> UpdateAsync(
        UpdateEventSqlInput input,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.UpdateEvent", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@EventId", input.EventId);
        command.Parameters.AddWithValue("@ProviderId", input.ProviderId);
        AddEditableEventParameters(command, input.EventCategory, input.IsChildFriendly, input.Title,
            input.Description, input.BannerImageUrl, input.EventType, input.StartDate, input.EndDate,
            input.StartTime, input.EndTime, input.IsPaid, input.Price, input.CancellationPolicy,
            input.Amenities);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Event row was not returned after update.");
            }
            return await ReadEventDetailAsync(reader, cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 51216)
        {
            throw new EventNotFoundException(input.EventId);
        }
    }

    public async Task<EventSqlSnapshot> UpdateByParentAsync(
        UpdateParentEventSqlInput input,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.UpdatePetParentEvent", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@EventId", input.EventId);
        command.Parameters.AddWithValue("@PetParentId", input.PetParentId);
        AddEditableEventParameters(command, input.EventCategory, input.IsChildFriendly, input.Title,
            input.Description, input.BannerImageUrl, input.EventType, input.StartDate, input.EndDate,
            input.StartTime, input.EndTime, input.IsPaid, input.Price, input.CancellationPolicy,
            input.Amenities);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Event row was not returned after update.");
            }
            return await ReadEventDetailAsync(reader, cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 51217)
        {
            throw new EventNotFoundException(input.EventId);
        }
    }

    private static void AddEditableEventParameters(
        SqlCommand command,
        string eventCategory, bool isChildFriendly, string title, string description,
        string? bannerImageUrl, string eventType, DateOnly startDate, DateOnly endDate,
        TimeOnly startTime, TimeOnly endTime, bool isPaid, decimal? price, string cancellationPolicy,
        IReadOnlyCollection<string> amenities)
    {
        command.Parameters.AddWithValue("@EventCategory", eventCategory);
        command.Parameters.AddWithValue("@IsChildFriendly", isChildFriendly);
        command.Parameters.AddWithValue("@Title", title);
        command.Parameters.AddWithValue("@Description", description);
        command.Parameters.AddWithValue("@BannerImageUrl", DbValue(bannerImageUrl));
        command.Parameters.AddWithValue("@EventType", eventType);
        command.Parameters.AddWithValue("@StartDate", startDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@EndDate", endDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@StartTime", startTime.ToTimeSpan());
        command.Parameters.AddWithValue("@EndTime", endTime.ToTimeSpan());
        command.Parameters.AddWithValue("@IsPaid", isPaid);
        command.Parameters.AddWithValue("@Price", price is null ? DBNull.Value : price.Value);
        command.Parameters.AddWithValue("@CancellationPolicy", cancellationPolicy);
        command.Parameters.AddWithValue("@AmenitiesJson", JsonSerializer.Serialize(amenities));
    }

    /// <summary>
    /// Reads the full event-detail shape from GetEvent / UpdateEvent's four
    /// result sets (event row already advanced to its first row): result set 1
    /// event, 2 amenities, 3 payment options (payout methods), 4 attendee names.
    /// </summary>
    private static async Task<EventSqlSnapshot> ReadEventDetailAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var snapshot = ReadEventRow(reader, amenities: Array.Empty<string>());

        // Result set 2: amenities.
        var amenities = await ReadAmenitiesAsync(reader, cancellationToken);

        // Result set 3: payment options (payout methods).
        var paymentOptions = new List<string>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                paymentOptions.Add(reader.GetString(0));
            }
        }

        // Result set 4: attendee names (non-cancelled bookings, names only).
        var attendees = new List<EventAttendeeSummary>();
        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                attendees.Add(new EventAttendeeSummary(reader.GetString(0), reader.GetInt32(1)));
            }
        }

        return snapshot with
        {
            Amenities = amenities,
            PaymentOptions = paymentOptions,
            Attendees = attendees
        };
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

    public async Task<IReadOnlyList<EventSqlSnapshot>> ListByPetParentAsync(
        Guid petParentId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.ListEventsByPetParent", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@PetParentId", petParentId);

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

    public async Task<IReadOnlyList<EventSqlSnapshot>> ListAsync(
        EventListFilter filter,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.ListEvents", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        command.Parameters.AddWithValue("@EventCategory", DbValue(filter.EventCategory));
        command.Parameters.AddWithValue("@EventType", DbValue(filter.EventType));
        command.Parameters.AddWithValue("@StartDate",
            filter.StartDate is null ? DBNull.Value : (object)filter.StartDate.Value.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@EndDate",
            filter.EndDate is null ? DBNull.Value : (object)filter.EndDate.Value.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@IsChildFriendly",
            filter.IsChildFriendly is null ? DBNull.Value : (object)filter.IsChildFriendly.Value);
        command.Parameters.AddWithValue("@AmenitiesJson",
            filter.Amenities is { Count: > 0 }
                ? (object)JsonSerializer.Serialize(filter.Amenities)
                : DBNull.Value);
        // Free-text title filter — the sproc escapes LIKE metacharacters and
        // does a case-insensitive "contains" match.
        command.Parameters.AddWithValue("@Title", DbValue(filter.Title));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1: event rows.
        var rows = new List<EventSqlSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadEventRow(reader, amenities: Array.Empty<string>()));
        }

        // Result set 2: (EventId, Amenity) pairs.
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

    public async Task<IReadOnlyList<EventSqlSnapshot>> ListTrendingAsync(
        int take,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.ListTrendingEvents", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@Take", take);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // Result set 1: event rows, already ordered by trending score.
        var rows = new List<EventSqlSnapshot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadEventRow(reader, amenities: Array.Empty<string>()));
        }

        // Result set 2: (EventId, Amenity) pairs.
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

        // Preserve the SQL trending order (rows list) while attaching amenities.
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
            ProviderId: reader.IsDBNull(1) ? null : reader.GetGuid(1),
            PetParentId: reader.IsDBNull(2) ? null : reader.GetGuid(2),
            EventCategory: reader.GetString(3),
            IsChildFriendly: reader.GetBoolean(4),
            Title: reader.GetString(5),
            Description: reader.GetString(6),
            BannerImageUrl: reader.IsDBNull(7) ? null : reader.GetString(7),
            Amenities: amenities,
            EventType: reader.GetString(8),
            StartDate: DateOnly.FromDateTime(reader.GetDateTime(9)),
            EndDate: DateOnly.FromDateTime(reader.GetDateTime(10)),
            StartTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(11)),
            EndTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(12)),
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(13), TimeSpan.Zero),
            UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(14), TimeSpan.Zero),
            // Engagement counters appended after the timestamps in every
            // event-returning sproc's result set 1.
            Counters: new EventCounters(
                ViewCount: reader.GetInt32(15),
                ShareCount: reader.GetInt32(16),
                InquiryCount: reader.GetInt32(17)),
            // Ticketing appended after the counters in every event-returning
            // sproc's result set 1 (top-level, returned for all event types).
            IsPaid: reader.GetBoolean(18),
            Price: reader.IsDBNull(19) ? null : reader.GetDecimal(19),
            // Cancellation policy appended after ticketing in result set 1.
            CancellationPolicy: reader.GetString(20),
            // Joining link (online events) — appended as the last column of
            // result set 1 in every event-returning sproc (index 24).
            EventLink: reader.IsDBNull(24) ? null : reader.GetString(24),
            // Organiser display fields appended last in result set 1 — joined
            // from Provider.Providers / Parent.PetParents. ImageUrl is always
            // DBNull for provider-organised events (no profile-photo column).
            OrganizerName: reader.IsDBNull(21) ? null : reader.GetString(21),
            OrganizerImageUrl: reader.IsDBNull(22) ? null : reader.GetString(22),
            // Total tickets booked (non-cancelled) — last column of result set 1
            // in every event-returning sproc.
            TotalBookings: reader.GetInt32(23));
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

    public async Task<IReadOnlyList<string>> SavePayoutMethodsAsync(
        Guid eventId,
        bool acceptsCash,
        bool acceptsDigital,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Event.SaveEventPayoutMethods", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@EventId", eventId);
        command.Parameters.AddWithValue("@AcceptsCash", acceptsCash);
        command.Parameters.AddWithValue("@AcceptsDigital", acceptsDigital);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var saved = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                saved.Add(reader.GetString(0));
            }
            return saved;
        }
        catch (SqlException exception) when (exception.Number == 51098)
        {
            throw new EventNotFoundException(eventId);
        }
        catch (SqlException exception) when (exception.Number == 51099)
        {
            throw new EventNotPaidException(eventId);
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
