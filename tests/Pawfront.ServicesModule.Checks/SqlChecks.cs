using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Pawfront.Application.Bookings;
using Pawfront.Application.ProviderTrust;
using Pawfront.Application.Jobs;

static class SqlChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        const string cs = @"Server=(localdb)\MSSQLLocalDB;Database=PawfrontServicesModuleChecks;Integrated Security=true;Encrypt=false";
        var sql = typeof(Pawfront.Infrastructure.Sql.SqlServiceRegistration).Assembly;
        T Make<T>(string type, params object?[] args) => (T)Activator.CreateInstance(sql.GetType(type)!, args)!;
        var store = Make<IBookingSqlStore>("Pawfront.Infrastructure.Sql.Bookings.SqlBookingStore", cs, null);
        var nightStore = Make<INightStayBookingSqlStore>("Pawfront.Infrastructure.Sql.Bookings.SqlNightStayBookingStore", cs, null);
        var trust = Make<IProviderTrustService>("Pawfront.Infrastructure.Sql.ProviderTrust.SqlProviderTrustService", cs, null);
        var jobs = Make<IProviderJobStore>("Pawfront.Infrastructure.Sql.Jobs.SqlProviderJobStore", cs, null);
        await using var connection = new SqlConnection(cs); await connection.OpenAsync();
        async Task Exec(string text, params (string Name,object Value)[] parameters)
        {
            await using var cmd = new SqlCommand(text,connection);
            foreach(var (name,value) in parameters) cmd.Parameters.AddWithValue(name,value);
            await cmd.ExecuteNonQueryAsync();
        }
        async Task<int> Count(string text, Guid id)
        {
            await using var cmd = new SqlCommand(text,connection); cmd.Parameters.AddWithValue("@Id",id);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        var provider=Guid.NewGuid(); var parent=Guid.NewGuid(); var service=Guid.NewGuid(); var nightService=Guid.NewGuid();
        await Exec("""
            DECLARE @A uniqueidentifier=NEWID(), @P uniqueidentifier=NEWID();
            INSERT Provider.ProviderAuthIdentities(ProviderAuthIdentityId,FirebaseUserId,AuthProvider,Email)
            VALUES(@A,CONVERT(nvarchar(36),@Provider),N'EmailPassword',CONCAT(@Provider,N'@test.invalid'));
            INSERT Provider.Providers(ProviderId,ProviderAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth)
            VALUES(@Provider,@A,N'Test',N'Provider',N'Other',N'+41',LEFT(CONVERT(nvarchar(36),@Provider),32),'1990-01-01');
            UPDATE Provider.ProviderAuthIdentities SET ProviderId=@Provider WHERE ProviderAuthIdentityId=@A;
            INSERT Provider.ProviderServiceRegistrations(ProviderId,ServiceCategory,SubCategory,Latitude,Longitude) VALUES(@Provider,N'PetSitter',N'PetHotel',47,8);
            INSERT Parent.ParentAuthIdentities(ParentAuthIdentityId,FirebaseUserId,AuthProvider,Email)
            VALUES(@P,CONVERT(nvarchar(36),@Parent),N'EmailPassword',CONCAT(@Parent,N'@test.invalid'));
            INSERT Parent.PetParents(PetParentId,ParentAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth,AddressLine,Latitude,Longitude,ZipCode,City,Description)
            VALUES(@Parent,@P,N'Test',N'Parent',N'Other',N'+41',LEFT(CONVERT(nvarchar(36),@Parent),32),'1990-01-01',N'Address',47,8,N'8000',N'Zurich',N'Test');
            INSERT Provider.ProviderServices(ServiceId,ProviderId,ServiceCategory,SubCategory,ServiceType)
            VALUES(@Service,@Provider,N'PetSitter',N'PetHotel',N'DayCare'),(@NightService,@Provider,N'PetSitter',N'PetHotel',N'NightStay');
            """,("@Provider",provider),("@Parent",parent),("@Service",service),("@NightService",nightService));
        async Task<Guid> Seed(bool night, string status, DateTime start)
        {
            var id=Guid.NewGuid();
            if(night)
                await Exec("""
                    INSERT Booking.NightStayBookings(NightStayBookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,CheckInDate,CheckOutDate,DropOffTime,PickUpTime,Status)
                    VALUES(@Id,@Provider,@Parent,@Service,N'PetSitter',N'PetHotel',CAST(@Start AS date),DATEADD(day,1,CAST(@Start AS date)),CAST(@Start AS time),'11:00',@Status);
                    """,("@Id",id),("@Provider",provider),("@Parent",parent),("@Service",nightService),("@Start",start),("@Status",status));
            else
                await Exec("""
                    INSERT Booking.Bookings(BookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,BookingDate,StartTime,EndTime,Status)
                    VALUES(@Id,@Provider,@Parent,@Service,N'PetSitter',N'PetHotel',CAST(@Start AS date),CAST(@Start AS time),CAST(DATEADD(minute,15,@Start) AS time),@Status);
                    """,("@Id",id),("@Provider",provider),("@Parent",parent),("@Service",service),("@Start",start),("@Status",status));
            return id;
        }
        var now=DateTime.UtcNow;
        // Choose whole seconds to match time(0) persisted schedule precision.
        DateTime Round(DateTime value)=>new(value.Year,value.Month,value.Day,value.Hour,value.Minute,0,DateTimeKind.Utc);
        foreach(var night in new[]{false,true})
        {
            var label=night?"Night stay":"Single day";
            foreach(var actor in new[]{"Parent","Provider"})
            foreach(var earlier in new[]{false,true})
            {
                var original=Round(now.AddHours(6)); var proposed=original.AddMinutes(earlier?-30:30);
                var id=await Seed(night,"CONFIRMED",original);
                var who=actor=="Parent"?parent:provider;
                await Exec(night?"""
                    INSERT Booking.NightStayBookingModifications(NightStayBookingId,RequestedByActor,RequestedByActorId,ProposedCheckInDate,ProposedCheckOutDate,HasAcknowledgedTerms,AcknowledgedDropOffTime,CreatedAtUtc)
                    VALUES(@Id,@Actor,@Who,CAST(@Proposed AS date),DATEADD(day,1,CAST(@Proposed AS date)),1,CAST(@Proposed AS time),@Requested);
                    UPDATE Booking.NightStayBookings SET Status=CONCAT(N'MODIFICATION_REQUEST_BY_',UPPER(@Actor)) WHERE NightStayBookingId=@Id;
                    """:"""
                    INSERT Booking.BookingModifications(BookingId,RequestedByActor,RequestedByActorId,ProposedBookingDate,ProposedStartTime,ProposedEndTime,CreatedAtUtc)
                    VALUES(@Id,@Actor,@Who,CAST(@Proposed AS date),CAST(@Proposed AS time),CAST(DATEADD(minute,15,@Proposed) AS time),@Requested);
                    UPDATE Booking.Bookings SET Status=CONCAT(N'MODIFICATION_REQUEST_BY_',UPPER(@Actor)) WHERE BookingId=@Id;
                    """,("@Id",id),("@Actor",actor),("@Who",who),("@Proposed",proposed),("@Requested",now));
                var expected=night
                    ? BookingAcceptanceDeadline.ComputeModification(new DateTimeOffset(now), new DateTimeOffset(original), new DateTimeOffset(actor=="Parent"?proposed:original))
                    : new DateTimeOffset((earlier?proposed:original).AddHours(-2));
                DateTimeOffset? listed, detail;
                if(night)
                {
                    listed=(await nightStore.ListByProviderAsync(provider,null,default)).Single(b=>b.NightStayBookingId==id).RequestExpiresAtUtc;
                    detail=(await nightStore.GetDetailAsync(id,default))!.RequestExpiresAtUtc;
                }
                else
                {
                    listed=(await store.ListByProviderAsync(provider,null,default)).Single(b=>b.BookingId==id).RequestExpiresAtUtc;
                    detail=(await store.GetDetailAsync(id,default))!.RequestExpiresAtUtc;
                }
                check(listed==expected && detail==expected,$"{label} {actor} {(earlier?"earlier":"later")} proposal list/detail expose correct expiry");
                check((await jobs.ListAsync(new(provider,Take:100),0,default)).Items.Single(j=>j.BookingId==id).RequestExpiresAtUtc==expected,
                    $"{label} {actor} Agenda jobs list exposes matching expiry");
                // Expire only the PROPOSED slot while original remains outside the cutoff.
                var expired=Round(now.AddMinutes(90));
                await Exec(night?"UPDATE Booking.NightStayBookingModifications SET ProposedCheckInDate=CAST(@Start AS date),AcknowledgedDropOffTime=CAST(@Start AS time) WHERE NightStayBookingId=@Id;"
                    :"UPDATE Booking.BookingModifications SET ProposedBookingDate=CAST(@Start AS date),ProposedStartTime=CAST(@Start AS time),ProposedEndTime=CAST(DATEADD(minute,15,@Start) AS time) WHERE BookingId=@Id;",("@Id",id),("@Start",expired));
                if(night && actor=="Provider")
                    await Exec("UPDATE Booking.NightStayBookings SET CheckInDate=CAST(@Start AS date),DropOffTime=CAST(@Start AS time) WHERE NightStayBookingId=@Id",("@Id",id),("@Start",expired));
                try
                {
                    if(night) await nightStore.RespondModificationAsync(id,actor=="Parent"?BookingStatusActor.Provider:BookingStatusActor.Parent,actor=="Parent"?provider:parent,true,10,null,default);
                    else await store.RespondModificationAsync(id,actor=="Parent"?BookingStatusActor.Provider:BookingStatusActor.Parent,actor=="Parent"?provider:parent,true,10,null,default);
                    throw new Exception("Expired proposal accepted");
                }
                catch(BookingModificationExpiredException){check(true,$"{label} {actor} response rejected after its expiry deadline");}
                await Exec("EXEC Booking.RevertExpiredModificationRequests;");
                check(night?(await nightStore.GetAsync(id,default)) is { Status: "CONFIRMED", RequestExpiresAtUtc: null }:(await store.GetAsync(id,default)) is { Status: "CONFIRMED", RequestExpiresAtUtc: null },
                    $"{label} {actor} automatically reverts at same cutoff");
            }
            var created=await Seed(night,"CREATED",Round(now.AddHours(4)));
            var deadline=night
                ? BookingAcceptanceDeadline.Compute(new DateTimeOffset(now),new DateTimeOffset(Round(now.AddHours(4))))
                : new DateTimeOffset(Round(now.AddHours(4)).AddHours(-2));
            // Database creation has its own timestamp; use persisted time for the 24-hour arm.
            if(night) deadline=BookingAcceptanceDeadline.Compute((await nightStore.GetAsync(created,default))!.CreatedAtUtc,new DateTimeOffset(Round(now.AddHours(4))));
            check(night?(await nightStore.GetAsync(created,default))!.RequestExpiresAtUtc==deadline:(await store.GetAsync(created,default))!.RequestExpiresAtUtc==deadline,
                $"{label} new request exposes acceptance expiry");
            var late=await Seed(night,"CREATED",Round(now.AddMinutes(90)));
            await Exec("EXEC Booking.ExpireStaleCreatedBookings;");
            check(night?(await nightStore.GetAsync(late,default))!.Status=="EXPIRED":(await store.GetAsync(late,default))!.Status=="EXPIRED",
                $"{label} new request follows its own expiry rule");
            if(night)
            {
                await Exec("UPDATE Booking.NightStayBookings SET CreatedAtUtc=DATEADD(hour,-25,SYSUTCDATETIME()) WHERE NightStayBookingId=@Id; EXEC Booking.ExpireStaleCreatedBookings;",("@Id",late));
                check((await nightStore.GetAsync(late,default))!.Status=="EXPIRED","Night stay new request still expires after 24 hours");
            }
        }
        foreach(var night in new[]{false,true})
        foreach(var actor in new[]{BookingStatusActor.Parent,BookingStatusActor.Provider})
        {
            var original=Round(DateTime.UtcNow.AddHours(2.5));
            var id=await Seed(night,"CONFIRMED",original);
            var actorId=actor==BookingStatusActor.Parent?parent:provider;
            try
            {
                if(night) await nightStore.RequestModificationAsync(id,actor,actorId,DateOnly.FromDateTime(original),DateOnly.FromDateTime(original).AddDays(1),null,null,default);
                else await store.RequestModificationAsync(id,actor,actorId,DateOnly.FromDateTime(original),TimeOnly.FromDateTime(original),TimeOnly.FromDateTime(original.AddMinutes(15)),null,null,default);
                throw new Exception("Modification sent inside three hours");
            }
            catch(BookingModificationWindowClosedException ex)
            { check(ex.Message.Contains("3 hours"),$"{night} {actor} late request has the three-hour error"); }
            // The same slot remains answerable: a proposal sent an hour ago had 3.5 hours of notice.
            await Exec(night?"""
                INSERT Booking.NightStayBookingModifications(NightStayBookingId,RequestedByActor,RequestedByActorId,ProposedCheckInDate,ProposedCheckOutDate,CreatedAtUtc)
                SELECT NightStayBookingId,@Actor,@Who,CheckInDate,CheckOutDate,DATEADD(hour,-1,SYSUTCDATETIME()) FROM Booking.NightStayBookings WHERE NightStayBookingId=@Id;
                UPDATE Booking.NightStayBookings SET Status=CONCAT(N'MODIFICATION_REQUEST_BY_',UPPER(@Actor)) WHERE NightStayBookingId=@Id;
                """:"""
                INSERT Booking.BookingModifications(BookingId,RequestedByActor,RequestedByActorId,ProposedBookingDate,ProposedStartTime,ProposedEndTime,CreatedAtUtc)
                SELECT BookingId,@Actor,@Who,BookingDate,StartTime,EndTime,DATEADD(hour,-1,SYSUTCDATETIME()) FROM Booking.Bookings WHERE BookingId=@Id;
                UPDATE Booking.Bookings SET Status=CONCAT(N'MODIFICATION_REQUEST_BY_',UPPER(@Actor)) WHERE BookingId=@Id;
                """,("@Id",id),("@Actor",actor.ToString()),("@Who",actorId));
            var responder=actor==BookingStatusActor.Parent?BookingStatusActor.Provider:BookingStatusActor.Parent;
            var responderId=responder==BookingStatusActor.Parent?parent:provider;
            var status=night
                ? (await nightStore.RespondModificationAsync(id,responder,responderId,true,10000,null,default)).Status
                : (await store.RespondModificationAsync(id,responder,responderId,true,10000,null,default)).Status;
            check(status.EndsWith("ACCEPTED_MODIFICATION"),$"{night} {actor} response between two and three hours is accepted");
        }
        var clearedDay=DateTime.UtcNow.Date.AddDays(3);
        var cleared=await Seed(true,"CONFIRMED",clearedDay.AddHours(12));
        await Exec("""
            INSERT Booking.NightStayBookingModifications(NightStayBookingId,RequestedByActor,RequestedByActorId,ProposedCheckInDate,ProposedCheckOutDate,HasAcknowledgedTerms,AcknowledgedDropOffTime)
            VALUES(@Id,N'Parent',@Parent,@Day,DATEADD(day,1,@Day),1,NULL);
            UPDATE Booking.NightStayBookings SET Status=N'MODIFICATION_REQUEST_BY_PARENT' WHERE NightStayBookingId=@Id;
            """,("@Id",cleared),("@Parent",parent),("@Day",clearedDay));
        // Move the request clock close enough to make the start cutoff win over 24 hours.
        await Exec("UPDATE Booking.NightStayBookingModifications SET CreatedAtUtc=DATEADD(hour,-1,@Day) WHERE NightStayBookingId=@Id",("@Id",cleared),("@Day",clearedDay));
        check((await nightStore.GetAsync(cleared,default))!.RequestExpiresAtUtc==new DateTimeOffset(clearedDay.AddHours(-2)),"Cleared proposed handover time uses midnight for modification expiry");
        // Trust approval survives a normal offering save, but removal/replacement invalidates it.
        await trust.SyncOfferingDocumentAsync(provider,"https://test.invalid/first.png",default);
        var evidence=(await trust.GetAsync(provider,default))!;
        await trust.ChangeAsync(provider,"Accept",null,evidence.RevisionId,null,null,default);
        await trust.SyncOfferingDocumentAsync(provider,"https://test.invalid/first.png",default);
        check((await trust.GetBadgesAsync([provider],default)).ContainsKey(provider),"Saving same document preserves approved badge");
        await trust.SyncOfferingDocumentAsync(provider,"https://test.invalid/replacement.png",default);
        check(!(await trust.GetBadgesAsync([provider],default)).ContainsKey(provider),"Replacing identity invalidates previous approval");
        evidence=(await trust.GetAsync(provider,default))!;
        await trust.ChangeAsync(provider,"Accept",null,evidence.RevisionId,null,null,default);
        await trust.SyncOfferingDocumentAsync(provider,null,default);
        check(await trust.GetAsync(provider,default) is null && !(await trust.GetBadgesAsync([provider],default)).ContainsKey(provider),"Removing identity clears document and badge");
        await trust.SyncOfferingDocumentAsync(provider,null,default);
        check(await trust.GetAsync(provider,default) is null,"Removing identity repeatedly is harmless");

        // Wait is allowed after the slot ended, retains the code, and is idempotent.
        // Midnight can be only seconds away: use today's midnight as a past slot.
        foreach(var night in new[]{false,true})
        foreach(var status in new[]{"CONFIRMED","START_JOB"})
        {
            var id=await Seed(night,status,now.Date);
            if(status=="START_JOB")
                await Exec(night?"INSERT Booking.NightStayBookingStartOtps(NightStayBookingId,OtpCode,ExpiresAtUtc) VALUES(@Id,'123456',DATEADD(hour,1,SYSUTCDATETIME()));"
                    :"INSERT Booking.BookingStartOtps(BookingId,OtpCode,ExpiresAtUtc) VALUES(@Id,'123456',DATEADD(hour,1,SYSUTCDATETIME()));",("@Id",id));
            var fix=new CapturedLocation(47,8,null,null);
            for(var retry=0;retry<2;retry++)
            {
                if(night) check((await nightStore.StartJobAsync(id,provider,"999999",10,fix,default,true)).Status=="START_JOB",$"Night stay wait from {status}, retry {retry}");
                else check((await store.StartJobAsync(id,provider,"999999",10,fix,default,true)).Status=="START_JOB",$"Single day wait from {status}, retry {retry}");
            }
            var prefix=night?"NightStayBooking":"Booking"; var key=night?"NightStayBookingId":"BookingId";
            check(await Count($"SELECT COUNT(*) FROM Booking.{prefix}StatusHistory WHERE {key}=@Id AND Note=N'Waiting for customer'",id)==1,"Wait recorded once");
            check(await Count($"SELECT COUNT(*) FROM Booking.{prefix}StartOtps WHERE {key}=@Id",id)==1,"Wait retry does not rotate or duplicate start code");
            if(status=="START_JOB") check(await Count($"SELECT COUNT(*) FROM Booking.{prefix}StartOtps WHERE {key}=@Id AND OtpCode=N'123456'",id)==1,"Existing start code preserved");
        }
        await HttpChecks.Run(provider,store,nightStore,check,Seed);
        await TicketWindowChecks.Run(cs,provider,parent,store,nightStore,connection,Seed,check);
        await WorkingHoursChecks.Run(provider,store,nightStore,connection,Seed,check);
        foreach(var night in new[]{false,true})
        {
            var beforeStart=await Seed(night,"CONFIRMED",DateTime.UtcNow.Date.AddDays(1));
            try
            {
                if(night) await nightStore.StartJobAsync(beforeStart,provider,"123456",10,new(47,8),default,true);
                else await store.StartJobAsync(beforeStart,provider,"123456",10,new(47,8),default,true);
                throw new Exception("Wait accepted before scheduled start");
            }
            catch(Exception ex) when(ex is BookingNotStartableException or BookingStartNotOnServiceDateException)
            {check(true,$"{(night?"Night stay":"Single day")} wait rejected before slot start");}
            var id=await Seed(night,"CONFIRMED",DateTime.UtcNow.Date);
            if(night) await nightStore.StartJobAsync(id,provider,"123456",10,new(47,8),default,true);
            else await store.StartJobAsync(id,provider,"123456",10,new(47,8),default,true);
            await Exec(night?"UPDATE Booking.NightStayBookings SET CheckInDate=DATEADD(day,-1,CheckInDate) WHERE NightStayBookingId=@Id;"
                :"UPDATE Booking.Bookings SET BookingDate=DATEADD(day,-1,BookingDate) WHERE BookingId=@Id;",("@Id",id));
            await Exec("EXEC Booking.SettleUnstartedJobsAsNoShow;");
            check(night?(await nightStore.GetAsync(id,default))!.Status=="PARENT_NO_SHOW":(await store.GetAsync(id,default))!.Status=="PARENT_NO_SHOW",
                $"{(night?"Night stay":"Single day")} waiting settles as customer no-show");
        }
    }
}
