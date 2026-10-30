using System.Text.Json;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Bookings;

static class WorkingHoursChecks
{
    public static async Task Run(Guid provider, IBookingSqlStore bookings, INightStayBookingSqlStore stays,
        SqlConnection connection, Func<bool, string, DateTime, Task<Guid>> seed, Action<bool, string> check)
    {
        async Task Execute(string sql, params (string Name, object Value)[] values)
        {
            await using var command = new SqlCommand(sql, connection);
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }
        async Task Save(bool open, string close = "23:59:59")
        {
            var json = JsonSerializer.Serialize(Enumerable.Range(0, 7).Select(day => new
            {
                dayOfWeek = day, isOpen = open,
                startTime = open ? "00:00:00" : null, endTime = open ? close : null
            }));
            await Execute("EXEC Provider.SaveProviderWeeklyAvailability @ProviderId=@Provider,@AvailabilityJson=@Json;",
                ("@Provider", provider), ("@Json", json));
        }
        async Task<DateTime> Closing(bool night, Guid id)
        {
            await using var command = new SqlCommand(night
                ? "SELECT WorkingHoursClosesAtUtc FROM Booking.NightStayBookings WHERE NightStayBookingId=@Id;"
                : "SELECT WorkingHoursClosesAtUtc FROM Booking.Bookings WHERE BookingId=@Id;", connection);
            command.Parameters.AddWithValue("@Id", id);
            return (DateTime)(await command.ExecuteScalarAsync())!;
        }

        await Save(true);
        foreach (var night in new[] { false, true })
        {
            var day = DateTime.UtcNow.Date.AddDays(2);
            var accepted = await seed(night, "CREATED", day.AddHours(12));
            await Execute(night
                ? "EXEC Booking.UpdateNightStayBookingStatus @NightStayBookingId=@Id,@NewStatus='CONFIRMED',@Actor='Provider',@ActorId=@Provider;"
                : "EXEC Booking.UpdateBookingStatus @BookingId=@Id,@NewStatus='CONFIRMED',@Actor='Provider',@ActorId=@Provider;",
                ("@Id", accepted), ("@Provider", provider));
            check(await Closing(night, accepted) == day.AddDays(1).AddSeconds(-1), "Acceptance freezes the working day's closing instant");

            // These simulate existing confirmed bookings from before rollout.
            var legacy = await seed(night, "CONFIRMED", DateTime.UtcNow.Date.AddHours(23));
            await Save(true, "00:00:01");
            check(await Closing(night, accepted) == day.AddDays(1).AddSeconds(-1), "Shortening hours preserves accepted hours");
            check(await Closing(night, legacy) == DateTime.UtcNow.Date.AddDays(1).AddSeconds(-1), "Availability edit captures legacy hours before replacing them");
            await Save(false);
            check(await Closing(night, accepted) == day.AddDays(1).AddSeconds(-1), "Closing the weekday preserves accepted hours");

            // Waiting and completion must work after both a shortening and closure.
            if (night)
            {
                await stays.StartJobAsync(legacy, provider, "123456", 10, new(47, 8), default, true);
                await stays.VerifyStartOtpAsync(legacy, provider, "123456", new(47, 8), default);
                check((await stays.CompleteAsync(legacy, provider, default)).Status == "COMPLETED", "Honoured night stay can wait, start and complete after weekly closure");
            }
            else
            {
                await bookings.StartJobAsync(legacy, provider, "123456", 10, new(47, 8), default);
                await bookings.StartJobAsync(legacy, provider, "123456", 10, new(47, 8), default, true);
                await bookings.VerifyStartOtpAsync(legacy, provider, "123456", new(47, 8), default);
                check((await bookings.CompleteAsync(legacy, provider, default)).Status == "COMPLETED", "Honoured job can start, wait and complete after weekly closure");
            }
            await Save(true, "18:00:00");
            var proposedDay = day.AddDays(2);
            await Execute(night
                ? "EXEC Booking.RequestNightStayBookingModification @NightStayBookingId=@Id,@Actor='Parent',@ActorId=@Parent,@ProposedCheckInDate=@Day,@ProposedCheckOutDate=@Out;"
                : "EXEC Booking.RequestBookingModification @BookingId=@Id,@Actor='Parent',@ActorId=@Parent,@ProposedBookingDate=@Day,@ProposedStartTime='12:00',@ProposedEndTime='12:15';",
                ("@Id", accepted), ("@Parent", (night ? (await stays.GetAsync(accepted, default))!.PetParentId : (await bookings.GetAsync(accepted, default))!.PetParentId)!),
                ("@Day", proposedDay), ("@Out", proposedDay.AddDays(1)));
            await Execute(night
                ? "EXEC Booking.RespondNightStayBookingModification @NightStayBookingId=@Id,@Actor='Provider',@ActorId=@Provider,@Accept=1,@Capacity=10000;"
                : "EXEC Booking.RespondBookingModification @BookingId=@Id,@Actor='Provider',@ActorId=@Provider,@Accept=1,@Capacity=10000;",
                ("@Id", accepted), ("@Provider", provider));
            check(await Closing(night, accepted) == proposedDay.AddHours(18), "Accepted reschedule captures the new day's current hours");
            await Save(true);
        }

        // Both sweep branches must use the saved cutoff, including when the
        // provider's current closing time would yield the opposite result.
        foreach (var status in new[] { "CONFIRMED", "START_JOB", "IN_PROGRESS" })
        {
            var id = await seed(false, status, DateTime.UtcNow.Date);
            await Execute("""
                UPDATE Booking.Bookings SET WorkingHoursOpensAtUtc=CAST(BookingDate AS datetime2),
                    WorkingHoursClosesAtUtc=DATEADD(hour,1,SYSUTCDATETIME()) WHERE BookingId=@Id;
                """, ("@Id", id));
            await Save(true, "00:00:01");
            await Execute("EXEC Booking.SettleUnstartedJobsAsNoShow;");
            check((await bookings.GetAsync(id, default))!.Status == status, "Shortened hours cannot settle an honoured booking early");
            await Execute("""
                UPDATE Booking.Bookings SET WorkingHoursClosesAtUtc=DATEADD(minute,-1,SYSUTCDATETIME()) WHERE BookingId=@Id;
                """, ("@Id", id));
            await Save(true);
            await Execute("EXEC Booking.SettleUnstartedJobsAsNoShow;");
            var expected = status == "CONFIRMED" ? "PROVIDER_NO_SHOW" : status == "START_JOB" ? "PARENT_NO_SHOW" : "JOB_INCOMPLETE";
            check((await bookings.GetAsync(id, default))!.Status == expected, "Sweep settles at the saved closing time even after hours are extended");
        }
        var early = await seed(false, "CONFIRMED", DateTime.UtcNow.AddMinutes(30));
        check((await bookings.StartJobAsync(early, provider, "123456", 10, new(47, 8), default)).Status == "START_JOB",
            "Start permits any time within the accepted hours before the scheduled slot");
        var outside = await seed(false,"CONFIRMED",DateTime.UtcNow.Date.AddHours(23));
        await Execute("UPDATE Booking.Bookings SET WorkingHoursOpensAtUtc=DATEADD(hour,1,SYSUTCDATETIME()),WorkingHoursClosesAtUtc=DATEADD(hour,2,SYSUTCDATETIME()) WHERE BookingId=@Id",("@Id",outside));
        try { await bookings.StartJobAsync(outside,provider,"123456",10,new(47,8),default); throw new Exception("Started before accepted opening"); }
        catch (BookingStartOutsideWorkingHoursException) { check(true,"Start rejects before accepted opening"); }

        await Execute("UPDATE Provider.ProviderWeeklyAvailability SET TimeZone='Asia/Kolkata',WindowsTimeZone='India Standard Time',StartTime='00:00',EndTime='08:00' WHERE ProviderId=@Provider;", ("@Provider", provider));
        var zoned = await seed(false, "CREATED", new DateTime(2030, 1, 6, 23, 30, 0, DateTimeKind.Utc));
        await bookings.UpdateStatusAsync(zoned, "CONFIRMED", BookingStatusActor.Provider, provider, null, null, default);
        check(await Closing(false, zoned) == new DateTime(2030, 1, 7, 2, 30, 0), "Snapshot preserves a local working day crossing UTC midnight");
        await Save(false);
        check(await Closing(false, zoned) == new DateTime(2030, 1, 7, 2, 30, 0), "Timezone edits cannot reinterpret the accepted closing instant");

        // Do not change the no-saved-hours assumptions of the other SQL checks.
        await Execute("DELETE Provider.ProviderWeeklyAvailability WHERE ProviderId=@Provider;", ("@Provider", provider));
    }
}
