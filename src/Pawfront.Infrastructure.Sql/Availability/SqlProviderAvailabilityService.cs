using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Availability;
using Pawfront.Application.Configuration;

namespace Pawfront.Infrastructure.Sql.Availability;

internal sealed class SqlProviderAvailabilityService(
    string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IProviderAvailabilityService
{
    public async Task<ProviderWeeklyAvailabilityResult> SaveAsync(
        SaveProviderWeeklyAvailabilityCommand command,
        CancellationToken cancellationToken)
    {
        ValidateCommand(command);

        var json = JsonSerializer.Serialize(
            command.Days.Select(d => new
            {
                dayOfWeek = d.DayOfWeek,
                isOpen = d.IsOpen,
                startTime = d.StartTime?.ToString("HH:mm:ss"),
                endTime = d.EndTime?.ToString("HH:mm:ss"),
                breakStartTime = d.BreakStartTime?.ToString("HH:mm:ss"),
                breakEndTime = d.BreakEndTime?.ToString("HH:mm:ss")
            }));

        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var sqlCommand = new SqlCommand("Provider.SaveProviderWeeklyAvailability", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        sqlCommand.Parameters.AddWithValue("@ProviderId", command.ProviderId);
        sqlCommand.Parameters.AddWithValue("@AvailabilityJson", json);

        try
        {
            await using var reader = await sqlCommand.ExecuteReaderAsync(cancellationToken);
            return new ProviderWeeklyAvailabilityResult(
                command.ProviderId,
                await ReadDaysAsync(reader, cancellationToken));
        }
        catch (SqlException exception) when (exception.Number == 51050)
        {
            throw new AvailabilityProviderNotFoundException(command.ProviderId);
        }
    }

    public async Task<ProviderWeeklyAvailabilityResult> GetAsync(
        Guid providerId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(await GetConnectionStringAsync(cancellationToken));
        await connection.OpenAsync(cancellationToken);

        await using var sqlCommand = new SqlCommand("Provider.GetProviderWeeklyAvailability", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        sqlCommand.Parameters.AddWithValue("@ProviderId", providerId);

        await using var reader = await sqlCommand.ExecuteReaderAsync(cancellationToken);
        return new ProviderWeeklyAvailabilityResult(
            providerId,
            await ReadDaysAsync(reader, cancellationToken));
    }

    private static async Task<List<DayAvailabilityResult>> ReadDaysAsync(
        SqlDataReader reader,
        CancellationToken cancellationToken)
    {
        var days = new List<DayAvailabilityResult>(7);
        while (await reader.ReadAsync(cancellationToken))
        {
            days.Add(new DayAvailabilityResult(
                DayOfWeek: reader.GetByte(1),
                IsOpen: reader.GetBoolean(2),
                StartTime: reader.IsDBNull(3) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(3)),
                EndTime: reader.IsDBNull(4) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(4)),
                BreakStartTime: reader.IsDBNull(5) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(5)),
                BreakEndTime: reader.IsDBNull(6) ? null : TimeOnly.FromTimeSpan(reader.GetTimeSpan(6))));
        }
        return days;
    }

    private static void ValidateCommand(SaveProviderWeeklyAvailabilityCommand command)
    {
        if (command.Days is null || command.Days.Count != 7)
        {
            throw new ArgumentException(
                "Days must contain exactly 7 entries (one per weekday).",
                nameof(command.Days));
        }

        var seen = new HashSet<int>();
        foreach (var day in command.Days)
        {
            if (day.DayOfWeek is < 0 or > 6)
            {
                throw new ArgumentException(
                    $"DayOfWeek must be between 0 (Sunday) and 6 (Saturday); got {day.DayOfWeek}.",
                    nameof(command.Days));
            }
            if (!seen.Add(day.DayOfWeek))
            {
                throw new ArgumentException(
                    $"DayOfWeek '{day.DayOfWeek}' appears more than once.",
                    nameof(command.Days));
            }

            if (!day.IsOpen)
            {
                if (day.StartTime is not null || day.EndTime is not null
                    || day.BreakStartTime is not null || day.BreakEndTime is not null)
                {
                    throw new ArgumentException(
                        $"Closed day {day.DayOfWeek} must not include time fields.",
                        nameof(command.Days));
                }
                continue;
            }

            if (day.StartTime is null || day.EndTime is null)
            {
                throw new ArgumentException(
                    $"Open day {day.DayOfWeek} requires both StartTime and EndTime.",
                    nameof(command.Days));
            }
            if (day.StartTime >= day.EndTime)
            {
                throw new ArgumentException(
                    $"Day {day.DayOfWeek}: StartTime must be earlier than EndTime.",
                    nameof(command.Days));
            }

            var hasBreakStart = day.BreakStartTime is not null;
            var hasBreakEnd = day.BreakEndTime is not null;
            if (hasBreakStart != hasBreakEnd)
            {
                throw new ArgumentException(
                    $"Day {day.DayOfWeek}: BreakStartTime and BreakEndTime must be set together (or both null).",
                    nameof(command.Days));
            }
            if (hasBreakStart)
            {
                if (day.BreakStartTime >= day.BreakEndTime)
                {
                    throw new ArgumentException(
                        $"Day {day.DayOfWeek}: BreakStartTime must be earlier than BreakEndTime.",
                        nameof(command.Days));
                }
                if (day.BreakStartTime < day.StartTime || day.BreakEndTime > day.EndTime)
                {
                    throw new ArgumentException(
                        $"Day {day.DayOfWeek}: Break must be inside the working window [{day.StartTime}, {day.EndTime}].",
                        nameof(command.Days));
                }
            }
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
}
