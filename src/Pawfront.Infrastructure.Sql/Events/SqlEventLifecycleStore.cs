using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Events;

namespace Pawfront.Infrastructure.Sql.Events;

internal sealed class SqlEventLifecycleStore(string? configuredConnectionString,
    IPawfrontSecretProvider? secretProvider) : IEventLifecycleStore
{
    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(!string.IsNullOrWhiteSpace(configuredConnectionString)
            ? configuredConnectionString : await secretProvider!.GetSqlConnectionStringAsync(ct));
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private async Task ExecuteAsync(string procedure, CancellationToken ct, params (string, object?)[] parameters)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand(procedure, connection) { CommandType = CommandType.StoredProcedure };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task CancelAsync(Guid eventId, Guid parentId, CancellationToken ct) =>
        ExecuteAsync("Event.CancelHostedEvent", ct, ("@EventId", eventId), ("@PetParentId", parentId));
    public Task RemoveAsync(Guid eventId, Guid parentId, CancellationToken ct) =>
        ExecuteAsync("Event.RemoveHostedEvent", ct, ("@EventId", eventId), ("@PetParentId", parentId));
    public Task SetTicketPaymentAsync(Guid bookingId, int ticketNumber, Guid parentId, string paymentStatus, CancellationToken ct) =>
        ExecuteAsync("Event.SetTicketPayment", ct, ("@BookingId", bookingId), ("@TicketNumber", ticketNumber),
            ("@PetParentId", parentId), ("@PaymentStatus", paymentStatus));
    public Task SetWaitlistAsync(Guid eventId, Guid parentId, bool join, int? capacity, CancellationToken ct) =>
        ExecuteAsync("Event.SetWaitlist", ct, ("@EventId", eventId), ("@PetParentId", parentId),
            ("@Join", join), ("@MaximumCapacity", capacity));

    public async Task<IReadOnlySet<Guid>> GetWaitlistAsync(Guid parentId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("SELECT EventId FROM [Event].[Waitlist] WHERE PetParentId=@Id", connection);
        command.Parameters.AddWithValue("@Id", parentId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new HashSet<Guid>();
        while (await reader.ReadAsync(ct)) result.Add(reader.GetGuid(0));
        return result;
    }

    public async Task<EventReliability> GetReliabilityAsync(Guid accountId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("Event.GetReliability", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@AccountId", accountId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(reader.GetBoolean(0), reader.IsDBNull(1) ? null : new DateTimeOffset(reader.GetDateTime(1), TimeSpan.Zero),
            reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
    }
}
