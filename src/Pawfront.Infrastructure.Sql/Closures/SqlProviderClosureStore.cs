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

        await using var sqlCommand = new SqlCommand("Provider.CreateClosures", connection)
        {
            CommandType = CommandType.StoredProcedure
        };

        sqlCommand.Parameters.AddWithValue("@ProviderId", command.ProviderId);

        var serviceIdTable = BuildServiceIdTable(command.ServiceIds);
        var serviceIdsParam = sqlCommand.Parameters.AddWithValue("@ServiceIds", serviceIdTable);
        serviceIdsParam.SqlDbType = SqlDbType.Structured;
        serviceIdsParam.TypeName = "Provider.ServiceIdList";

        sqlCommand.Parameters.AddWithValue("@StartDate", command.StartDate.ToDateTime(TimeOnly.MinValue));
        sqlCommand.Parameters.AddWithValue("@EndDate",   command.EndDate.ToDateTime(TimeOnly.MinValue));
        sqlCommand.Parameters.AddWithValue("@StartTime",
            command.StartTime is null ? DBNull.Value : (object)command.StartTime.Value.ToTimeSpan());
        sqlCommand.Parameters.AddWithValue("@EndTime",
            command.EndTime is null ? DBNull.Value : (object)command.EndTime.Value.ToTimeSpan());
        sqlCommand.Parameters.AddWithValue("@Reason",
            command.Reason is null ? DBNull.Value : (object)command.Reason);

        try
        {
            // The sproc emits exactly one result set in either branch:
            //   - on conflict: rows of (ServiceId, BookingId, PetParentId, BookingDate, StartTime, EndTime) — 6 cols
            //   - on success: N rows of (ClosureId, ProviderId, ServiceId, StartDate, EndDate, StartTime, EndTime, Reason, CreatedAtUtc) — 9 cols
            await using var reader = await sqlCommand.ExecuteReaderAsync(cancellationToken);

            var isConflictShape = reader.FieldCount == 6;

            if (isConflictShape)
            {
                var conflicts = new List<ConflictingBooking>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    conflicts.Add(new ConflictingBooking(
                        ServiceId: reader.GetGuid(0),
                        BookingId: reader.GetGuid(1),
                        PetParentId: reader.GetGuid(2),
                        BookingDate: DateOnly.FromDateTime(reader.GetDateTime(3)),
                        StartTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(4)),
                        EndTime: TimeOnly.FromTimeSpan(reader.GetTimeSpan(5))));
                }
                return new CreateClosureResult.BookingsExist(conflicts);
            }

            var closures = new List<ProviderClosure>();
            while (await reader.ReadAsync(cancellationToken))
            {
                closures.Add(new ProviderClosure(
                    ClosureId: reader.GetGuid(0),
                    ProviderId: reader.GetGuid(1),
                    ServiceId: reader.GetGuid(2),
                    StartDate: DateOnly.FromDateTime(reader.GetDateTime(3)),
                    EndDate: DateOnly.FromDateTime(reader.GetDateTime(4)),
                    StartTime: reader.IsDBNull(5) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(5)),
                    EndTime: reader.IsDBNull(6) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(6)),
                    Reason: reader.IsDBNull(7) ? null : reader.GetString(7),
                    CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero)));
            }

            if (closures.Count == 0)
            {
                throw new InvalidOperationException("No closure rows were returned after insert.");
            }

            return new CreateClosureResult.Created(closures);
        }
        catch (SqlException exception) when (exception.Number == 51070)
        {
            throw new ProviderClosureProviderNotFoundException(command.ProviderId);
        }
        catch (SqlException exception) when (exception.Number == 51072)
        {
            throw new ProviderClosureServiceInvalidException(command.ProviderId);
        }
        catch (SqlException exception) when (exception.Number == 51075)
        {
            throw new ProviderClosureEmptyServiceIdsException();
        }
    }

    public async Task<IReadOnlyList<ProviderClosure>> ListAsync(
        Guid providerId,
        Guid? serviceId,
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
        command.Parameters.AddWithValue("@ServiceId",
            serviceId is null ? DBNull.Value : (object)serviceId.Value);
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
                ServiceId: reader.GetGuid(2),
                StartDate: DateOnly.FromDateTime(reader.GetDateTime(3)),
                EndDate: DateOnly.FromDateTime(reader.GetDateTime(4)),
                StartTime: reader.IsDBNull(5) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(5)),
                EndTime: reader.IsDBNull(6) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(6)),
                Reason: reader.IsDBNull(7) ? null : reader.GetString(7),
                CreatedAtUtc: new DateTimeOffset(reader.GetDateTime(8), TimeSpan.Zero)));
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
        Guid serviceId,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand("Provider.GetActiveClosuresForDate", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.AddWithValue("@ServiceId", serviceId);
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

    private static DataTable BuildServiceIdTable(IReadOnlyCollection<Guid> serviceIds)
    {
        var table = new DataTable();
        table.Columns.Add("ServiceId", typeof(Guid));
        foreach (var id in serviceIds)
        {
            table.Rows.Add(id);
        }
        return table;
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
