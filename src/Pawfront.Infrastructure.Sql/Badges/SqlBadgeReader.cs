using System.Text.Json;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Badges;
using Pawfront.Application.Configuration;
using Pawfront.Application.ParentPets;
namespace Pawfront.Infrastructure.Sql.Badges;

internal sealed class SqlBadgeReader(string? configuredConnectionString, IPawfrontSecretProvider? secretProvider) : IBadgeReader
{
    public async Task<BadgeSnapshot> ReadAsync(IReadOnlyCollection<Guid> parentIds, IReadOnlyCollection<Guid> petIds,
        IReadOnlyCollection<Guid> eventBookingIds, CancellationToken cancellationToken)
    {
        var parents = new HashSet<Guid>(); var pets = new HashSet<Guid>(); var bookings = new HashSet<Guid>();
        if (parentIds.Count + petIds.Count + eventBookingIds.Count == 0) return new(parents, pets, bookings);
        var cs = configuredConnectionString ?? (secretProvider is null
            ? throw new InvalidOperationException("SQL connection is not configured.")
            : await secretProvider.GetSqlConnectionStringAsync(cancellationToken));
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT i.PetParentId FROM Parent.ParentIdentities i
            JOIN OPENJSON(@Parents) WITH (Id UNIQUEIDENTIFIER '$') ids ON ids.Id=i.PetParentId WHERE i.Status=N'Added';
            SELECT p.PetId, p.MicrochipId FROM Parent.Pets p
            JOIN OPENJSON(@Pets) WITH (Id UNIQUEIDENTIFIER '$') ids ON ids.Id=p.PetId;
            SELECT b.BookingId FROM Event.EventBookings b
            JOIN OPENJSON(@Bookings) WITH (Id UNIQUEIDENTIFIER '$') ids ON ids.Id=b.BookingId
            JOIN Parent.ParentIdentities i ON i.PetParentId=b.BookerAccountId AND i.Status=N'Added'
            WHERE b.BookerType=N'PetParent';
            """, connection);
        command.Parameters.AddWithValue("@Parents", JsonSerializer.Serialize(parentIds));
        command.Parameters.AddWithValue("@Pets", JsonSerializer.Serialize(petIds));
        command.Parameters.AddWithValue("@Bookings", JsonSerializer.Serialize(eventBookingIds));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) parents.Add(reader.GetGuid(0));
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            if (!reader.IsDBNull(1) && MicrochipId.IsValid(reader.GetString(1))) pets.Add(reader.GetGuid(0));
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) bookings.Add(reader.GetGuid(0));
        return new(parents, pets, bookings);
    }
}
