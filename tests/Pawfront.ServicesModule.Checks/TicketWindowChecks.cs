using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pawfront.Application.Bookings;
using Pawfront.Application.Support;
using Pawfront.Application.Storage;

static class TicketWindowChecks
{
    public static async Task Run(string cs,Guid provider,Guid parent,IBookingSqlStore bookings,
        INightStayBookingSqlStore stays,SqlConnection connection,Func<bool,string,DateTime,Task<Guid>> seed,Action<bool,string> check)
    {
        var tickets=(ISupportTicketStore)Activator.CreateInstance(typeof(Pawfront.Infrastructure.Sql.SqlServiceRegistration).Assembly
            .GetType("Pawfront.Infrastructure.Sql.Support.SqlSupportTicketStore")!,cs,null)!;
        var uploads=0;
        var narratives=DispatchProxy.Create<ISupportTicketNarrativeStore,HttpChecks.EndpointProxy>();
        ((HttpChecks.EndpointProxy)narratives).Call=(method,values)=>Task.FromResult(
            new SupportTicketNarrative((Guid)values![0]!,[],DateTimeOffset.UtcNow,DateTimeOffset.UtcNow));
        var blobs=DispatchProxy.Create<IPawfrontBlobStorage,HttpChecks.EndpointProxy>();
        ((HttpChecks.EndpointProxy)blobs).Call=(method,values)=>
        {
            uploads++;
            return Task.FromResult("https://test.invalid/incident.jpg");
        };
        var serviceType=typeof(ISupportTicketService).Assembly.GetType("Pawfront.Application.Support.SupportTicketService")!;
        var service=(ISupportTicketService)Activator.CreateInstance(serviceType,tickets,narratives,blobs,
            Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(serviceType)))!;
        async Task Execute(string text,params (string Name,object Value)[] values)
        {
            await using var command=new SqlCommand(text,connection);
            foreach(var (name,value) in values) command.Parameters.AddWithValue(name,value);
            await command.ExecuteNonQueryAsync();
        }
        foreach(var night in new[]{false,true})
        foreach(var scenario in new[]{"late-paid","early-paid","early-paid-expired","start-job","ending","paid-23h59","paid-24h01","unpaid-recent","unpaid-old","not-ended","ongoing","early-completed","confirmed-future","confirmed-running","created","declined","cancelled-unconfirmed","cancelled-confirmed","pending-confirmed"})
        foreach(var photos in new[]{false,true})
        {
            var now=DateTime.UtcNow;
            var paid=scenario.Contains("paid") && !scenario.StartsWith("unpaid");
            var status=scenario switch
            {
                "created"=>"CREATED", "declined"=>"PROVIDER_DECLINED",
                "cancelled-unconfirmed" or "cancelled-confirmed"=>"PARENT_CANCELLED",
                "pending-confirmed"=>"MODIFICATION_REQUEST_BY_PARENT",
                "confirmed-future" or "confirmed-running"=>"CONFIRMED",
                "start-job"=>"START_JOB", "ending"=>"ENDING", "ongoing"=>"IN_PROGRESS", _=>paid?"PAID":"COMPLETED"
            };
            var id=await seed(night,status=="PARENT_CANCELLED"?"CONFIRMED":status,now.Date.AddDays(-3));
            if(status=="PARENT_CANCELLED")
                await Execute(night?"UPDATE Booking.NightStayBookings SET Status='PARENT_CANCELLED',CancelledAtUtc=SYSUTCDATETIME() WHERE NightStayBookingId=@Id;"
                    :"UPDATE Booking.Bookings SET Status='PARENT_CANCELLED',CancelledAtUtc=SYSUTCDATETIME() WHERE BookingId=@Id;",("@Id",id));
            if(scenario is "cancelled-confirmed" or "pending-confirmed")
                await Execute(night?"INSERT Booking.NightStayBookingStatusHistory(NightStayBookingId,ToStatus,ChangedByActor,ChangedByActorId) VALUES(@Id,'CONFIRMED','Provider',@Provider);"
                    :"INSERT Booking.BookingStatusHistory(BookingId,ToStatus,ChangedByActor,ChangedByActorId) VALUES(@Id,'CONFIRMED','Provider',@Provider);",("@Id",id),("@Provider",provider));
            var end=scenario switch
            {
                "late-paid"=>now.AddDays(-3),
                "early-paid" or "early-paid-expired" or "early-completed" or "not-ended" or "confirmed-future"=>now.AddDays(2),
                "confirmed-running"=>now.AddMinutes(5),
                "paid-23h59"=>now.AddMinutes(-1439),
                "paid-24h01"=>now.AddMinutes(-1441),
                "unpaid-old"=>now.AddHours(-25),
                _=>now.AddHours(-1)
            };
            if(end.TimeOfDay<TimeSpan.FromMinutes(1)) end=end.Date.AddMinutes(1);
            await Execute(night?"""
                UPDATE Booking.NightStayBookings SET CheckInDate=DATEADD(day,-1,CAST(@End AS date)),CheckOutDate=CAST(@End AS date),PickUpTime=CAST(@End AS time)
                WHERE NightStayBookingId=@Id;
                """:"""
                UPDATE Booking.Bookings SET BookingDate=CAST(@End AS date),StartTime='00:00',EndTime=CAST(@End AS time) WHERE BookingId=@Id;
                """,("@End",end),("@Id",id));
            if(scenario=="early-completed")
                await Execute(night?"INSERT Booking.NightStayBookingStatusHistory(NightStayBookingId,FromStatus,ToStatus,ChangedByActor,ChangedByActorId,ChangedAtUtc) VALUES(@Id,'IN_PROGRESS','COMPLETED','Provider',@Provider,DATEADD(hour,-1,SYSUTCDATETIME()));"
                    :"INSERT Booking.BookingStatusHistory(BookingId,FromStatus,ToStatus,ChangedByActor,ChangedByActorId,ChangedAtUtc) VALUES(@Id,'IN_PROGRESS','COMPLETED','Provider',@Provider,DATEADD(hour,-1,SYSUTCDATETIME()));",("@Id",id),("@Provider",provider));
            DateTimeOffset? paidAt=null;
            if(paid)
            {
                var age=scenario is "paid-24h01" or "early-paid-expired"?1441:scenario=="paid-23h59"?1439:60;
                await Execute("""
                    INSERT Booking.BookingPayments(BookingType,BookingId,ProviderId,PetParentId,Amount,PaymentMethod,PaidAtUtc)
                    VALUES(@Type,@Id,@Provider,@Parent,100,'Cash',DATEADD(minute,-@Age,SYSUTCDATETIME()));
                    """,("@Type",night?"NightStay":"SingleDay"),("@Id",id),("@Provider",provider),("@Parent",parent),("@Age",age));
                await using var command=new SqlCommand("SELECT PaidAtUtc FROM Booking.BookingPayments WHERE BookingId=@Id",connection);
                command.Parameters.AddWithValue("@Id",id);
                paidAt=new((DateTime)(await command.ExecuteScalarAsync())!,TimeSpan.Zero);
            }
            object listRow;
            if(night)
            {
                listRow=(await stays.ListByProviderAsync(provider,null,default)).Single(x=>x.NightStayBookingId==id);
                check(((NightStayBookingResult)listRow).PaidAtUtc==paidAt && (await stays.GetDetailAsync(id,default))!.PaidAtUtc==paidAt,
                    $"Night stay {scenario} list/detail use exact payment ledger time");
                check((await stays.ListByPetParentAsync(parent,default)).Single(x=>x.Booking.NightStayBookingId==id).Booking.PaidAtUtc==paidAt,"Parent night-stay list includes payment time");
            }
            else
            {
                listRow=(await bookings.ListByProviderAsync(provider,null,default)).Single(x=>x.BookingId==id);
                check(((BookingResult)listRow).PaidAtUtc==paidAt && (await bookings.GetDetailAsync(id,default))!.PaidAtUtc==paidAt,
                    $"Single day {scenario} list/detail use exact payment ledger time");
                check((await bookings.ListByPetParentAsync(parent,default)).Single(x=>x.Booking.BookingId==id).Booking.PaidAtUtc==paidAt,"Parent single-day list includes payment time");
            }
            foreach(var host in new[]{"Pawfront.Api","Pawfront.PetParentApi"})
            {
                var endpoints=night?"NightStayBookingEndpoints":host=="Pawfront.Api"?"BookingEndpoints":"PetParentEndpoints";
                var mapper=Assembly.Load(host).GetType($"{host}.Endpoints.{endpoints}")!
                    .GetMethods(BindingFlags.Static|BindingFlags.NonPublic).Single(m=>m.ReturnType.Name==(night?"NightStayBookingResponse":"BookingResponse") && m.GetParameters()[0].ParameterType==listRow.GetType());
                var args=mapper.GetParameters().Select((p,i)=>i==0?listRow:p.ParameterType.IsValueType?Activator.CreateInstance(p.ParameterType):null).ToArray();
                var dto=mapper.Invoke(null,args)!;
                var json=JsonSerializer.SerializeToElement(dto,dto.GetType(),new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var timestamp=json.GetProperty("paymentDetails").GetProperty("paidAtUtc");
                check(paidAt is null?timestamp.ValueKind==JsonValueKind.Null:timestamp.GetDateTimeOffset()==paidAt,$"{host} list exposes paymentDetails.paidAtUtc");
            }
            var expected=scenario is "created" or "declined" or "cancelled-unconfirmed"?"TicketWindowNotOpen":scenario is "early-paid-expired" or "paid-24h01" or "unpaid-old"?"TicketWindowExpired":null;
            var uploadsBefore=uploads;
            try
            {
                var command=new CreateSupportTicketCommand("BookingIncident","Provider",provider,night?"NightStay":"SingleDay",id,null,"Test incident",null,null);
                using var photo=new MemoryStream([1]);
                var outcome=photos
                    ? (await service.CreateWithPhotosAsync(command,[new("incident.jpg","image/jpeg",1,photo)],default)).Outcome
                    : (await service.CreateAsync(command,default)).Outcome;
                check(expected is null && outcome==CreateSupportTicketOutcome.Created,$"{(night?"Night stay":"Single day")} {scenario} ticket allowed (photos: {photos})");
                check(uploads-uploadsBefore==(photos?1:0),"Allowed photo flow uploads its evidence");
            }
            catch(SupportBookingTicketWindowException ex)
            {
                check(ex.Code==expected,$"{(night?"Night stay":"Single day")} {scenario} rejected with {ex.Code}");
                check(uploads==uploadsBefore,"Rejected report does not upload photos");
            }
        }
        foreach(var host in new[]{"Pawfront.Api","Pawfront.PetParentApi"})
        foreach(var notOpen in new[]{false,true})
        {
            var type=Assembly.Load(host).GetType($"{host}.GlobalExceptionHandler")!;
            var logger=Activator.CreateInstance(typeof(NullLogger<>).MakeGenericType(type));
            var handler=(IExceptionHandler)Activator.CreateInstance(type,logger)!;
            using var services=new ServiceCollection().AddLogging().BuildServiceProvider();
            var context=new DefaultHttpContext{RequestServices=services}; context.Response.Body=new MemoryStream();
            var exception=new SupportBookingTicketWindowException(notOpen);
            await handler.TryHandleAsync(context,exception,default);
            context.Response.Body.Position=0; using var json=await JsonDocument.ParseAsync(context.Response.Body);
            check(context.Response.StatusCode==409 && json.RootElement.GetProperty("error").GetProperty("code").GetString()==exception.Code,
                $"{host} returns HTTP 409 {exception.Code}");
        }
    }
}
