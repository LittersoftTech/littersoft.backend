using Microsoft.Data.SqlClient;
using Pawfront.Application.Bookings;
using Pawfront.Application.ParentOnboarding;

static class SqlChecks
{
    public static async Task<int> RunAsync()
    {
        const string cs = @"Server=(localdb)\MSSQLLocalDB;Database=PawfrontEmailBookingChecks;Integrated Security=true;Encrypt=false";
        var assembly = typeof(Pawfront.Infrastructure.Sql.SqlServiceRegistration).Assembly;
        T Make<T>(string name) => (T)Activator.CreateInstance(assembly.GetType(name)!, cs, null)!;
        var emailReader = Make<IParentEmailStatusReader>("Pawfront.Infrastructure.Sql.ParentOnboarding.SqlParentEmailStatusReader");
        var hourly = Make<IBookingSqlStore>("Pawfront.Infrastructure.Sql.Bookings.SqlBookingStore");
        var nightly = Make<INightStayBookingSqlStore>("Pawfront.Infrastructure.Sql.Bookings.SqlNightStayBookingStore");
        var parent = Guid.NewGuid(); var auth = Guid.NewGuid(); var provider = Guid.NewGuid();
        var providerAuth = Guid.NewGuid(); var dayService = Guid.NewGuid(); var nightService = Guid.NewGuid();
        var email = $"{auth}@example.invalid";
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        async Task Execute(string sql, Guid? booking = null)
        {
            await using var command = new SqlCommand(sql, connection);
            foreach (var (name, value) in new (string, object)[] {
                ("@Parent", parent), ("@Auth", auth), ("@Provider", provider), ("@ProviderAuth", providerAuth),
                ("@DayService", dayService), ("@NightService", nightService), ("@Email", email),
                ("@Booking", booking ?? Guid.Empty) }) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }
        Check(!await emailReader.IsRegisteredAsync(email, default), "Unknown SQL email is false");
        await Execute("""
            INSERT Parent.ParentAuthIdentities(ParentAuthIdentityId,FirebaseUserId,AuthProvider,Email)
            VALUES(@Auth,CONVERT(NVARCHAR(36),@Auth),N'EmailPassword',CONCAT(N'  ',UPPER(@Email),N'  '));
            """);
        Check(!await emailReader.IsRegisteredAsync(email, default), "Firebase auth without parent profile is false");
        await Execute("""
            INSERT Parent.PetParents(PetParentId,ParentAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth,AddressLine,Latitude,Longitude,ZipCode,City,Description)
            VALUES(@Parent,@Auth,N'Test',N'Parent',N'Other',N'+41',LEFT(CONVERT(NVARCHAR(36),@Parent),16),'1990-01-01',N'Address',47,8,N'8000',N'Zurich',N'Test');
            INSERT Provider.ProviderAuthIdentities(ProviderAuthIdentityId,FirebaseUserId,AuthProvider,Email)
            VALUES(@ProviderAuth,CONVERT(NVARCHAR(36),@ProviderAuth),N'EmailPassword',CONCAT(@ProviderAuth,N'@example.invalid'));
            INSERT Provider.Providers(ProviderId,ProviderAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth)
            VALUES(@Provider,@ProviderAuth,N'Test',N'Provider',N'Other',N'+41',LEFT(CONVERT(NVARCHAR(36),@Provider),16),'1990-01-01');
            INSERT Provider.ProviderServices(ServiceId,ProviderId,ServiceCategory,SubCategory,ServiceType)
            VALUES(@DayService,@Provider,N'PetSitter',N'Freelance',N'DayCare'),(@NightService,@Provider,N'PetSitter',N'PetHotel',N'NightStay');
            """);
        Check(await emailReader.IsRegisteredAsync($"  {email}  ", default), "Profile email comparison trims and ignores case");
        await Execute("UPDATE Parent.PetParents SET IsDeleted=1 WHERE PetParentId=@Parent;");
        Check(!await emailReader.IsRegisteredAsync(email, default), "Deleted parent is false");
        await Execute("UPDATE Parent.PetParents SET IsDeleted=0 WHERE PetParentId=@Parent;");
        Check(!await emailReader.IsRegisteredAsync($"{providerAuth}@example.invalid", default), "Provider-only email is false");

        var date = new DateOnly(2099, 1, 5);
        foreach (var withTimes in new[] { false, true })
        {
            TimeOnly? dropOff = withTimes ? new(9, 0) : null;
            TimeOnly? pickUp = withTimes ? new(10, 0) : null;
            var stay = await nightly.CreateAsync(provider, parent, null, nightService, "PetSitter", "PetHotel",
                date, date.AddDays(2), dropOff, pickUp, null, null, 20m, null, 100, default);
            Check(stay.DropOffTime == dropOff && stay.PickUpTime == pickUp, "Create returns nullable or fixed times");
            var read = (await nightly.GetAsync(stay.NightStayBookingId, default))!;
            Check(read.DropOffTime == dropOff && read.PickUpTime == pickUp, "Get preserves nullable or fixed times");
            var detail = (await nightly.GetDetailAsync(stay.NightStayBookingId, default))!;
            Check(detail.DropOffTime == dropOff && detail.PickUpTime == pickUp, "Detail preserves nullable or fixed times");
            Check((await nightly.ListByProviderAsync(provider, null, default)).Any(b => b.NightStayBookingId == stay.NightStayBookingId && b.DropOffTime == dropOff), "Provider list reads nullable times");
            Check((await nightly.ListByPetParentAsync(parent, default)).Any(b => b.Booking.NightStayBookingId == stay.NightStayBookingId && b.Booking.PickUpTime == pickUp), "Parent list reads nullable times");
        }

        foreach (var night in new[] { false, true })
        foreach (var firstActor in new[] { BookingStatusActor.Parent, BookingStatusActor.Provider })
        foreach (var outcome in new[] { "accepted", "declined", "expired" })
        {
            var id = Guid.NewGuid();
            await Execute(night ? """
                INSERT Booking.NightStayBookings(NightStayBookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,CheckInDate,CheckOutDate,DropOffTime,PickUpTime,Status,PricePerNight)
                VALUES(@Booking,@Provider,@Parent,@NightService,N'PetSitter',N'PetHotel','2099-01-05','2099-01-07',NULL,NULL,N'CONFIRMED',20);
                """ : """
                INSERT Booking.Bookings(BookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,BookingDate,StartTime,EndTime,Status,Source,PricePerHour)
                VALUES(@Booking,@Provider,@Parent,@DayService,N'PetSitter',N'Freelance','2099-01-05','10:00','11:00',N'CONFIRMED',N'App',20);
                """, id);
            Guid ActorId(BookingStatusActor actor) => actor == BookingStatusActor.Parent ? parent : provider;
            var otherActor = firstActor == BookingStatusActor.Parent ? BookingStatusActor.Provider : BookingStatusActor.Parent;
            async Task Request(BookingStatusActor actor)
            {
                if (night) await nightly.RequestModificationAsync(id, actor, ActorId(actor), date, date.AddDays(2), null, null, default);
                else await hourly.RequestModificationAsync(id, actor, ActorId(actor), date, new(11, 0), new(12, 0), null, null, default);
            }
            async Task Respond(BookingStatusActor actor, bool accept)
            {
                if (night) await nightly.RespondModificationAsync(id, actor, ActorId(actor), accept, 100, null, default);
                else await hourly.RespondModificationAsync(id, actor, ActorId(actor), accept, 100, null, default);
            }
            async Task Flags(bool parentDone, bool providerDone)
            {
                await using (var command = new SqlCommand("Booking.ListProviderJobs", connection) { CommandType = System.Data.CommandType.StoredProcedure })
                {
                    command.Parameters.AddWithValue("@ProviderId", provider);
                    command.Parameters.AddWithValue("@Take", 100);
                    await using var jobs = await command.ExecuteReaderAsync();
                    await jobs.NextResultAsync();
                    var found = false;
                    while (await jobs.ReadAsync())
                        if (jobs.GetGuid(jobs.GetOrdinal("BookingId")) == id)
                        {
                            found = true;
                            Check(jobs.GetBoolean(jobs.GetOrdinal("ProviderModificationUsed")) == providerDone,
                                "Jobs list counts accepted provider modifications only");
                        }
                    Check(found, "Booking is present in the jobs list");
                }
                if (night)
                {
                    var list = await nightly.ListByProviderAsync(provider, null, default);
                    Check(list.Single(b => b.NightStayBookingId == id).ProviderModificationUsed == providerDone,
                        "Night-stay list counts accepted provider modifications only");
                    var row = (await nightly.GetDetailAsync(id, default))!;
                    Check(row.IsParentModificationDone == parentDone && row.IsProviderModificationDone == providerDone
                        && row.IsModificationDone == (parentDone || providerDone) && row.ProviderModificationUsed == providerDone, "Night-stay flags follow accepted requester's party");
                }
                else
                {
                    var list = await hourly.ListByProviderAsync(provider, null, default);
                    Check(list.Single(b => b.BookingId == id).ProviderModificationUsed == providerDone,
                        "Service list counts accepted provider modifications only");
                    var row = (await hourly.GetDetailAsync(id, default))!;
                    Check(row.IsParentModificationDone == parentDone && row.IsProviderModificationDone == providerDone
                        && row.IsModificationDone == (parentDone || providerDone) && row.ProviderModificationUsed == providerDone, "Service flags follow accepted requester's party");
                }
            }
            await Flags(false, false);
            await Request(firstActor);
            await Flags(false, false);
            if (outcome != "accepted")
            {
                if (outcome == "declined") await Respond(otherActor, false);
                else await Execute(night ? """
                    UPDATE Booking.NightStayBookingModifications SET CreatedAtUtc=DATEADD(HOUR,-25,SYSUTCDATETIME()) WHERE NightStayBookingId=@Booking;
                    EXEC Booking.RevertExpiredModificationRequests;
                    """ : """
                    UPDATE Booking.BookingModifications SET CreatedAtUtc=DATEADD(HOUR,-25,SYSUTCDATETIME()) WHERE BookingId=@Booking;
                    EXEC Booking.RevertExpiredModificationRequests;
                    """, id);
                await Flags(false, false);
                await Request(firstActor);
            }
            await Respond(otherActor, true);
            await Flags(firstActor == BookingStatusActor.Parent, firstActor == BookingStatusActor.Provider);
            try { await Request(firstActor); throw new Exception("Second accepted modification was permitted"); }
            catch (BookingModificationLimitReachedException) { checks++; }
            await Request(otherActor);
            await Respond(firstActor, true);
            await Flags(true, true);
            try { await Request(otherActor); throw new Exception("Counterparty's second accepted modification was permitted"); }
            catch (BookingModificationLimitReachedException) { checks++; }
        }
        return checks;
    }
}
