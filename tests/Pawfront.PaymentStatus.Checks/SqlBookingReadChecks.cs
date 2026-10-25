using System.Reflection;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Bookings;

internal static class SqlBookingReadChecks
{
    public static async Task<int> RunAsync()
    {
        const string cs = @"Server=(localdb)\MSSQLLocalDB;Database=PawfrontBookingFixChecks;Integrated Security=true;Encrypt=false;TrustServerCertificate=true";
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            checks++;
        }
        var infrastructure = Assembly.Load("Pawfront.Infrastructure.Sql");
        T Store<T>(string type) => (T)Activator.CreateInstance(infrastructure.GetType(type)!, cs, null)!;
        var day = Store<IBookingSqlStore>("Pawfront.Infrastructure.Sql.Bookings.SqlBookingStore");
        var night = Store<INightStayBookingSqlStore>("Pawfront.Infrastructure.Sql.Bookings.SqlNightStayBookingStore");
        await using var c = new SqlConnection(cs);
        await c.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT TOP(1) BookingId, ProviderId, PetParentId, CashNotReceivedAtUtc
            FROM [Booking].[Bookings] WHERE CashNotReceivedAtUtc IS NOT NULL
            ORDER BY CreatedAtUtc DESC
            """, c);
        Guid id, provider, parent;
        DateTimeOffset reported;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new Exception("Run tests/booking-mobile-regressions.sql first.");
            id = reader.GetGuid(0); provider = reader.GetGuid(1); parent = reader.GetGuid(2);
            reported = new(reader.GetDateTime(3), TimeSpan.Zero);
        }
        Check((await day.GetAsync(id, default))!.CashNotReceivedAtUtc == reported, "SQL booking read retains timestamp");
        Check((await day.GetDetailAsync(id, default))!.CashNotReceivedAtUtc == reported, "SQL booking detail retains timestamp");
        Check((await day.ListByProviderAsync(provider, null, default)).Single(b => b.BookingId == id).CashNotReceivedAtUtc == reported,
            "SQL provider booking list retains timestamp without shifting customer columns");
        Check((await day.ListByPetParentAsync(parent, default)).Single(b => b.Booking.BookingId == id).Booking.CashNotReceivedAtUtc == reported,
            "SQL parent booking list retains timestamp without shifting snapshots");
        var providerStays = await night.ListByProviderAsync(provider, null, default);
        Check(providerStays.Any(row => row.CashNotReceivedAtUtc is not null), "SQL night provider list exposes non-null report time");
        foreach (var row in providerStays)
        {
            Check((await night.GetAsync(row.NightStayBookingId, default))!.CashNotReceivedAtUtc == row.CashNotReceivedAtUtc,
                "SQL night provider list agrees with booking read");
            Check((await night.GetDetailAsync(row.NightStayBookingId, default))!.CashNotReceivedAtUtc == row.CashNotReceivedAtUtc,
                "SQL night detail agrees with provider list");
        }
        foreach (var row in await night.ListByPetParentAsync(parent, default))
        {
            Check((await night.GetAsync(row.Booking.NightStayBookingId, default))!.CancellationReason == row.Booking.CancellationReason,
                "SQL night parent list retains cancellation reason");
        }
        var providerBookings = await day.ListByProviderAsync(provider, null, default);
        Check(providerBookings.Any(b => b.ProviderModificationUsed), "Existing provider modification history consumes allowance");
        foreach (var row in providerBookings)
        {
            Check((await day.GetAsync(row.BookingId, default))!.ProviderModificationUsed == row.ProviderModificationUsed,
                "Provider list and single read agree on modification allowance");
            Check((await day.GetDetailAsync(row.BookingId, default))!.ProviderModificationUsed == row.ProviderModificationUsed,
                "Detail and list agree on modification allowance");
        }
        foreach (var row in await day.ListByPetParentAsync(parent, default))
            Check((await day.GetAsync(row.Booking.BookingId, default))!.ProviderModificationUsed == row.Booking.ProviderModificationUsed,
                "Parent list agrees on modification allowance");
        var pair = Store<IParentProviderBookingReader>("Pawfront.Infrastructure.Sql.Bookings.SqlParentProviderBookingReader");
        Check((await pair.ListAsync(provider, parent, 0, 20, default)).Jobs.Single(b => b.BookingId == id).CashNotReceivedAtUtc == reported,
            "SQL shared booking history retains timestamp");
        return checks;
    }
}
