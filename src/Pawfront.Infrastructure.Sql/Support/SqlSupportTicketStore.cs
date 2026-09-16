using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Earnings;
using Pawfront.Application.Support;

namespace Pawfront.Infrastructure.Sql.Support;

/// <summary>
/// Reads and writes the support-ticket INDEX through the <c>Support</c> schema's
/// procedures. Also serves <see cref="ISupportLegalHoldReader"/> and
/// <see cref="IMySupportTicketLookup"/>: both ask this same table one narrow question, and
/// there is no reason to open a second store for either.
/// </summary>
/// <remarks>
/// The ticket row is projected identically by four procedures — <c>CreateTicket</c> (after
/// its <c>Outcome</c> column), <c>GetTicket</c>, <c>AddTicketPhoto</c> and
/// <c>RecordTicketClarification</c> — so <see cref="ReadTicket"/> is the single reader for
/// all of them. If a procedure's column list changes, all four must change together.
/// <c>ListTickets</c> projects the same block and then appends <c>TotalCount</c>, so it is
/// a fifth that must move with them — and its own ordinal moves when the block grows.
/// </remarks>
internal sealed class SqlSupportTicketStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider)
    : ISupportTicketStore, ISupportLegalHoldReader, IMySupportTicketLookup
{
    public async Task<CreateSupportTicketResult> CreateAsync(
        CreateSupportTicketCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var sqlCommand = new SqlCommand("[Support].[CreateTicket]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        sqlCommand.Parameters.AddWithValue("@TicketType", command.TicketType);
        sqlCommand.Parameters.AddWithValue("@RaisedByType", command.RaisedByType);
        sqlCommand.Parameters.AddWithValue("@ActorId", command.ActorId);
        sqlCommand.Parameters.AddWithValue(
            "@BookingType", (object?)command.BookingType ?? DBNull.Value);
        sqlCommand.Parameters.AddWithValue(
            "@BookingId", (object?)command.BookingId ?? DBNull.Value);
        sqlCommand.Parameters.AddWithValue(
            "@ConversationId", (object?)command.ConversationId ?? DBNull.Value);
        sqlCommand.Parameters.AddWithValue(
            "@Category", (object?)command.Category ?? DBNull.Value);
        sqlCommand.Parameters.AddWithValue(
            "@Reason", (object?)command.Reason ?? DBNull.Value);
        sqlCommand.Parameters.AddWithValue(
            "@EventId", (object?)command.EventId ?? DBNull.Value);

        try
        {
            await using var reader = await sqlCommand.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Support.CreateTicket returned no ticket row.");
            }

            // Result set 1 is prefixed with [Outcome], so every ticket column here sits one
            // ordinal later than in the other three procedures.
            var outcome = reader.GetString(0) == "TicketAlreadyOpen"
                ? CreateSupportTicketOutcome.TicketAlreadyOpen
                : CreateSupportTicketOutcome.Created;

            var ticket = ReadTicket(reader, offset: 1);
            var photos = await ReadPhotosAsync(reader, cancellationToken);

            return new CreateSupportTicketResult(outcome, ticket with { Photos = photos });
        }
        catch (SqlException exception) when (exception.Number == 51340)
        {
            throw new SupportBookingNotFoundException(command.BookingId ?? Guid.Empty);
        }
        catch (SqlException exception) when (exception.Number == 51341)
        {
            throw new SupportForbiddenException();
        }
        catch (SqlException exception) when (exception.Number == 51342)
        {
            throw new SupportConversationNotFoundException(command.ConversationId ?? Guid.Empty);
        }
        catch (SqlException exception) when (exception.Number == 51343)
        {
            throw new ArgumentException("Invalid support ticket request.", nameof(command));
        }
        catch (SqlException exception) when (exception.Number == 51348)
        {
            throw new SupportNotAppBookingException(command.BookingId ?? Guid.Empty);
        }
        catch (SqlException exception) when (exception.Number == 51355)
        {
            throw new SupportEventNotFoundException(command.EventId ?? Guid.Empty);
        }
    }

    public async Task<SupportTicketRecord?> GetAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Support].[GetTicket]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@TicketId", ticketId);
        command.Parameters.AddWithValue("@ActorType", actorType);
        command.Parameters.AddWithValue("@ActorId", actorId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // The procedure RETURNs without emitting anything when the ticket is unknown OR
        // the caller is not a party — deliberately the same answer, so an id cannot be
        // probed. Null carries that through unchanged.
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var ticket = ReadTicket(reader, offset: 0);
        var photos = await ReadPhotosAsync(reader, cancellationToken);

        return ticket with { Photos = photos };
    }

    public async Task<SupportTicketListResult> ListAsync(
        SupportTicketQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Support].[ListTickets]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ActorType", query.ActorType);
        command.Parameters.AddWithValue("@ActorId", query.ActorId);
        command.Parameters.AddWithValue(
            "@Statuses",
            query.Statuses is { Count: > 0 }
                ? string.Join(',', query.Statuses)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "@SortBy", query.SortBy == SupportTicketSortBy.CreatedAt ? "CreatedAt" : "UpdatedAt");
        command.Parameters.AddWithValue(
            "@SortDirection",
            query.SortDirection == EarningsSortDirection.Ascending ? "Asc" : "Desc");
        command.Parameters.AddWithValue("@Skip", query.Skip);
        command.Parameters.AddWithValue("@Take", query.Take);

        var items = new List<SupportTicketRecord>();
        var totalCount = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(ReadTicket(reader, offset: 0));

            // COUNT(*) OVER () — the same value on every row, so one round trip serves both
            // the page and its total. An empty page leaves it 0, which is correct.
            // Ordinal 17, not 14: [Category], [Reason] and [EventId] are appended to the
            // ticket block ahead of it so that block stays identical to the other five
            // procedures'.
            totalCount = reader.GetInt32(17);
        }

        // The list carries no photos: the card shows type, status and subject, all of which
        // are on the row. Fetching a gallery per card would be a fan-out for images the
        // list does not render.
        return new SupportTicketListResult(items, totalCount, query.Skip, query.Take);
    }

    public async Task<SupportTicketRecord> AddPhotoAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        string photoUrl,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Support].[AddTicketPhoto]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@TicketId", ticketId);
        command.Parameters.AddWithValue("@ActorType", actorType);
        command.Parameters.AddWithValue("@ActorId", actorId);
        command.Parameters.AddWithValue("@PhotoUrl", photoUrl);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Support.AddTicketPhoto returned no ticket row.");
            }

            var ticket = ReadTicket(reader, offset: 0);
            var photos = await ReadPhotosAsync(reader, cancellationToken);

            return ticket with { Photos = photos };
        }
        catch (SqlException exception) when (exception.Number == 51344)
        {
            throw new SupportTicketNotFoundException(ticketId);
        }
        catch (SqlException exception) when (exception.Number == 51345)
        {
            throw new SupportTicketPhotoLimitReachedException(ticketId, SupportTicketLimits.MaxPhotos);
        }
        catch (SqlException exception) when (exception.Number == 51346)
        {
            throw new SupportTicketPhotoNotBookingIncidentException(ticketId);
        }
        catch (SqlException exception) when (exception.Number == 51347)
        {
            throw new SupportTicketClosedException(ticketId);
        }
    }

    public async Task<SupportTicketRecord> RecordClarificationAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Support].[RecordTicketClarification]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@TicketId", ticketId);
        command.Parameters.AddWithValue("@ActorType", actorType);
        command.Parameters.AddWithValue("@ActorId", actorId);

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    "Support.RecordTicketClarification returned no ticket row.");
            }

            // One result set here — the updated row. Photos are unchanged by a
            // clarification, so the caller keeps whatever it already had.
            return ReadTicket(reader, offset: 0);
        }
        catch (SqlException exception) when (exception.Number == 51344)
        {
            throw new SupportTicketNotFoundException(ticketId);
        }
        catch (SqlException exception) when (exception.Number == 51347)
        {
            throw new SupportTicketClosedException(ticketId);
        }
        catch (SqlException exception) when (exception.Number == 51349)
        {
            throw new SupportNoClarificationRequestedException(ticketId);
        }
    }

    public async Task<ConversationLegalHold?> GetConversationHoldAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("[Support].[GetConversationLegalHold]", connection)
        {
            CommandType = System.Data.CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ConversationId", conversationId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // No row means the thread is free — the overwhelmingly common case, and an index
        // seek on IX_Tickets_OpenConversation that finds nothing.
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ConversationLegalHold(
            TicketId: reader.GetGuid(0),
            TicketNumber: reader.GetInt32(1),
            TicketType: reader.GetString(2),
            Status: reader.GetString(3));
    }

    /// <summary>
    /// The caller's own open tickets, keyed by subject — what puts
    /// <c>isTicketRaisedByMe</c> on a booking, an event or a conversation.
    /// </summary>
    /// <remarks>
    /// Deliberately never throws. This decorates a list; a support-table hiccup must cost
    /// the caller a Report button they should not have been offered, not the whole page.
    /// </remarks>
    public async Task<MySupportTicketSubjects> GetAsync(
        string actorType,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        if (!SupportRaisedByTypes.IsKnown(actorType) || actorId == Guid.Empty)
        {
            return MySupportTicketSubjects.Empty;
        }

        try
        {
            await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
            await connection.OpenAsync(cancellationToken);

            await using var command = new SqlCommand("[Support].[ListMyOpenTicketSubjects]", connection)
            {
                CommandType = System.Data.CommandType.StoredProcedure
            };
            command.Parameters.AddWithValue("@RaisedByType", actorType);
            command.Parameters.AddWithValue("@ActorId", actorId);

            var subjects = new List<MySupportTicketSubject>();

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                subjects.Add(new MySupportTicketSubject(
                    TicketId: reader.GetGuid(0),
                    TicketNumber: reader.GetInt32(1),
                    TicketType: reader.GetString(2),
                    BookingType: reader.IsDBNull(3) ? null : reader.GetString(3),
                    BookingId: reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    ConversationId: reader.IsDBNull(5) ? null : reader.GetGuid(5),
                    EventId: reader.IsDBNull(6) ? null : reader.GetGuid(6)));
            }

            return new MySupportTicketSubjects(subjects);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return MySupportTicketSubjects.Empty;
        }
    }

    /// <summary>
    /// The 17-column ticket projection shared by six procedures.
    /// <paramref name="offset"/> is 1 for <c>Support.CreateTicket</c>, whose result set is
    /// prefixed with its <c>Outcome</c> discriminator, and 0 for the rest.
    /// </summary>
    private static SupportTicketRecord ReadTicket(SqlDataReader reader, int offset)
        => new(
            TicketId: reader.GetGuid(offset),
            TicketNumber: reader.GetInt32(offset + 1),
            TicketType: reader.GetString(offset + 2),
            // Nullable since the reporter-only kinds ('AppIssue' / 'EventIncident') store
            // just the side that raised them.
            ProviderId: reader.IsDBNull(offset + 3) ? null : reader.GetGuid(offset + 3),
            PetParentId: reader.IsDBNull(offset + 4) ? null : reader.GetGuid(offset + 4),
            RaisedByType: reader.GetString(offset + 5),
            BookingType: reader.IsDBNull(offset + 6) ? null : reader.GetString(offset + 6),
            BookingId: reader.IsDBNull(offset + 7) ? null : reader.GetGuid(offset + 7),
            PetId: reader.IsDBNull(offset + 8) ? null : reader.GetGuid(offset + 8),
            ConversationId: reader.IsDBNull(offset + 9) ? null : reader.GetGuid(offset + 9),
            Status: reader.GetString(offset + 10),
            // Appended after ClosedAtUtc by every one of the six procedures, so adding
            // them shifted no ordinal above. Named arguments, so the record's own
            // parameter order is free to read better than the projection's.
            Category: reader.IsDBNull(offset + 14) ? null : reader.GetString(offset + 14),
            Reason: reader.IsDBNull(offset + 15) ? null : reader.GetString(offset + 15),
            EventId: reader.IsDBNull(offset + 16) ? null : reader.GetGuid(offset + 16),
            Photos: [],
            CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(offset + 11), TimeSpan.Zero),
            UpdatedAtUtc: new DateTimeOffset(reader.GetDateTime(offset + 12), TimeSpan.Zero),
            ClosedAtUtc: reader.IsDBNull(offset + 13)
                ? null
                : new DateTimeOffset(reader.GetDateTime(offset + 13), TimeSpan.Zero));

    private static async Task<IReadOnlyList<SupportTicketPhotoRecord>> ReadPhotosAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var photos = new List<SupportTicketPhotoRecord>();

        if (!await reader.NextResultAsync(cancellationToken))
        {
            return photos;
        }

        while (await reader.ReadAsync(cancellationToken))
        {
            photos.Add(new SupportTicketPhotoRecord(
                TicketPhotoId: reader.GetGuid(0),
                TicketId: reader.GetGuid(1),
                PhotoUrl: reader.GetString(2),
                CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero)));
        }

        return photos;
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
