using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Closures;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Sql.Closures;

internal sealed class SqlProviderClosureStore(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderClosureSqlStore
{
    public async Task<CreateClosureResult> CreateAsync(
        CreateProviderClosureCommand command,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var sqlCommand = new SqlCommand("Provider.CreateClosure", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        sqlCommand.Parameters.AddWithValue("@ProviderId", command.ProviderId);
        sqlCommand.Parameters.AddWithValue("@StartDate", command.StartDate.ToDateTime(TimeOnly.MinValue));
        sqlCommand.Parameters.AddWithValue("@EndDate",   command.EndDate.ToDateTime(TimeOnly.MinValue));
        sqlCommand.Parameters.AddWithValue("@StartTime",
            command.StartTime is null ? DBNull.Value : (object)command.StartTime.Value.ToTimeSpan());
        sqlCommand.Parameters.AddWithValue("@EndTime",
            command.EndTime is null ? DBNull.Value : (object)command.EndTime.Value.ToTimeSpan());
        sqlCommand.Parameters.AddWithValue("@Reason",
            command.Reason is null ? DBNull.Value : (object)command.Reason);

        var closureIdParam = new SqlParameter("@ClosureId", SqlDbType.UniqueIdentifier)
        {
            Direction = ParameterDirection.InputOutput,
            Value = DBNull.Value
        };
        sqlCommand.Parameters.Add(closureIdParam);

        try
        {
            // The sproc emits exactly one result set in either branch:
            //   - on conflict: rows of (BookingId, PetParentId, BookingDate, StartTime, EndTime), output param NULL
            //   - on success: one row of the new closure, output param set
            await using var reader = await sqlCommand.ExecuteReaderAsync(cancellationToken);

            // Discriminate by column count (sproc returns 5 cols on conflict, 8 on success).
            // Reading FieldCount before any ReadAsync is safe.
            var isConflictShape = reader.FieldCount == 5;

            if (isConflictShape)
            {
                var conflicts = new List<ConflictingBooking>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    conflicts.Add(new ConflictingBooking(
                        BookingId: reader.GetGuid(0),
                        PetParentId: reader.GetGuid(1),
                        BookingDate: DateOnly.FromDateTime(reader.GetDateTime(2)),
                        StartTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(3)),
                        EndTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(4))));
                }
                return new CreateClosureResult.BookingsExist(conflicts);
            }

            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Closure row was not returned after insert.");
            }

            var closure = new ProviderClosure(
                ClosureId: reader.GetGuid(0),
                ProviderId: reader.GetGuid(1),
                StartDate: DateOnly.FromDateTime(reader.GetDateTime(2)),
                EndDate: DateOnly.FromDateTime(reader.GetDateTime(3)),
                StartTime: reader.IsDBNull(4) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(4)),
                EndTime: reader.IsDBNull(5) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(5)),
                Reason: reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero));

            return new CreateClosureResult.Created(closure);
        }
        catch (SqlException exception) when (exception.Number == 51070)
        {
            throw new ProviderClosureProviderNotFoundException(command.ProviderId);
        }
    }

    public async Task<IReadOnlyList<ProviderClosure>> ListAsync(
        Guid providerId,
        DateOnly? from,
        DateOnly? to,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Provider.ListClosures", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@From",
            from is null ? DBNull.Value : (object)from.Value.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue("@To",
            to is null ? DBNull.Value : (object)to.Value.ToDateTime(TimeOnly.MinValue));

        var rows = new List<ProviderClosure>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ProviderClosure(
                ClosureId: reader.GetGuid(0),
                ProviderId: reader.GetGuid(1),
                StartDate: DateOnly.FromDateTime(reader.GetDateTime(2)),
                EndDate: DateOnly.FromDateTime(reader.GetDateTime(3)),
                StartTime: reader.IsDBNull(4) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(4)),
                EndTime: reader.IsDBNull(5) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(5)),
                Reason: reader.IsDBNull(6) ? null : reader.GetString(6),
                CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero)));
        }
        return rows;
    }

    public async Task DeleteAsync(Guid providerId, Guid closureId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Provider.DeleteClosure", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@ClosureId",  closureId);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number == 51071)
        {
            throw new ProviderClosureNotFoundException(closureId);
        }
    }

    public async Task<IReadOnlyList<ActiveClosure>> GetActiveClosuresForDateAsync(
        Guid providerId,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Provider.GetActiveClosuresForDate", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@Date", date.ToDateTime(TimeOnly.MinValue));

        var rows = new List<ActiveClosure>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        // Sproc returns ClosureId, StartDate, EndDate, StartTime, EndTime, Reason.
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ActiveClosure(
                ClosureId: reader.GetGuid(0),
                StartTime: reader.IsDBNull(3) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(3)),
                EndTime: reader.IsDBNull(4) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(4)),
                Reason: reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return rows;
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
