using Pawfront.Application.Support;
using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Events;

const string connectionString = @"Server=(localdb)\MSSQLLocalDB;Database=PawfrontEventLifecycleChecks;Integrated Security=true;Encrypt=false;TrustServerCertificate=true";
int checks = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks++; Console.WriteLine($"PASS {name}"); }
async Task<List<Dictionary<string, object>>> Query(string sql, params (string, object?)[] args)
{
    await using var c = new SqlConnection(connectionString); await c.OpenAsync();
    await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 120 };
    foreach (var (name, value) in args)
    {
        var p = cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        if (value is DataTable) { p.SqlDbType = SqlDbType.Structured; p.TypeName = "Event.EventBookingAttendeeNames"; }
    }
    await using var r = await cmd.ExecuteReaderAsync();
    var rows = new List<Dictionary<string, object>>();
    while (await r.ReadAsync())
    {
        var row = new Dictionary<string, object>(); for (int i=0;i<r.FieldCount;i++) row[r.GetName(i)] = r.GetValue(i); rows.Add(row);
    }
    return rows;
}
async Task Error(int number, Func<Task> action, string name)
{
    try { await action(); throw new Exception($"Expected {number}: {name}"); }
    catch (SqlException ex) when (ex.Number == number) { Check(true,name); }
}
async Task<Guid> Parent()
{
    var id=Guid.NewGuid();
    await Query("""
        DECLARE @Auth UNIQUEIDENTIFIER=NEWID();
        INSERT [Parent].[ParentAuthIdentities](ParentAuthIdentityId,FirebaseUserId,AuthProvider,Email) VALUES(@Auth,CONVERT(nvarchar(36),@Id),N'EmailPassword',CONCAT(@Id,N'@test.invalid'));
        INSERT [Parent].[PetParents](PetParentId,ParentAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth,AddressLine,Latitude,Longitude,ZipCode,City,Description)
        VALUES(@Id,@Auth,N'Test',N'Parent',N'Other',N'+41',LEFT(CONVERT(nvarchar(36),@Id),16),'1990-01-01',N'Address',47,8,N'8000',N'Zurich',N'Test');
        """,("@Id",id));
    return id;
}
async Task<Guid> Event(Guid host, int capacity=5, int hours=72)
{
    var id=Guid.NewGuid();
    await Query("""
        DECLARE @Start DATETIME2=CONVERT(DATETIME2,DATEADD(hour,@Hours,SYSUTCDATETIME()) AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time');
        INSERT [Event].[Events](EventId,PetParentId,EventCategory,IsChildFriendly,Title,Description,EventType,StartDate,EndDate,StartTime,EndTime,IsPaid,Price,CancellationPolicy,MaximumCapacity)
        VALUES(@Id,@Host,N'MeetupsAndSocials',1,N'Test Event',N'Description',N'Physical',CAST(@Start AS DATE),CAST(@Start AS DATE),CAST(@Start AS TIME),'23:00',1,10,N'FullRefundUpTo24Hours',@Capacity);
        """,("@Id",id),("@Host",host),("@Hours",hours),("@Capacity",capacity));
    return id;
}
async Task<Guid> Book(Guid ev, Guid parent, int tickets=1)
{
    var names=new DataTable(); names.Columns.Add("TicketNumber",typeof(int)); names.Columns.Add("AttendeeName",typeof(string));
    for(int i=1;i<=tickets;i++) names.Rows.Add(i,"Attendee "+i);
    var rows=await Query("""
        EXEC [Event].[CreateEventBooking] @EventId=@Event,@BookerName=N'Test',@BookerEmail=@Email,@PaymentMethod=N'Cash',@MaximumCapacity=999,@TotalAmount=1,@AttendeeNames=@Names,@BookerAccountId=@Parent,@BookerType=N'PetParent';
        """,("@Event",ev),("@Email",parent+"@test.invalid"),("@Names",names),("@Parent",parent));
    return (Guid)rows[0]["BookingId"];
}
Task Cancel(Guid booking, Guid parent) => Query("EXEC [Event].[CancelEventBooking] @B,@Email",("@B",booking),("@Email",parent+"@test.invalid"));
Task Cash(Guid booking, Guid host, int ticket, string status) => Query("EXEC [Event].[SetTicketPayment] @B,@N,@H,@S",("@B",booking),("@N",ticket),("@H",host),("@S",status));
async Task<Dictionary<string,object>> Reliability(Guid parent) => (await Query("EXEC [Event].[GetReliability] @Id",("@Id",parent)))[0];
// The exact boundary and timezone offsets do not depend on the test machine clock.
var winterDate = new DateOnly(2026,1,15); var summerDate = new DateOnly(2026,7,15); var sixPm = new TimeOnly(18,0);
var winterClose = EventBookingWindow.CloseAtUtc(winterDate,sixPm);
Check(winterClose == new DateTimeOffset(2026,1,15,16,0,0,TimeSpan.Zero),"winter 18:00 Zurich closes at 16:00 UTC");
Check(EventBookingWindow.CloseAtUtc(summerDate,sixPm) == new DateTimeOffset(2026,7,15,15,0,0,TimeSpan.Zero),"summer 18:00 Zurich closes at 15:00 UTC");
Check(!EventBookingWindow.IsClosed(winterDate,sixPm,winterClose.AddTicks(-1)) && EventBookingWindow.IsClosed(winterDate,sixPm,winterClose),"booking closes at the exact one-hour boundary");
foreach (var date in new[]{new DateOnly(2026,3,29),new DateOnly(2026,10,25)})
{
    var time = new TimeOnly(2,30);
    var sqlClose = (DateTime)(await Query("SELECT DATEADD(hour,-1,[Event].[EventStartUtc](@D,@T)) AS CloseAt",("@D",date.ToDateTime(TimeOnly.MinValue)),("@T",time.ToTimeSpan())))[0]["CloseAt"];
    Check(EventBookingWindow.CloseAtUtc(date,time).UtcDateTime==sqlClose,$"client cutoff matches SQL across DST transition {date}");
}
var host=await Parent(); var buyer=await Parent(); var watcher=await Parent();
var ev=await Event(host,2); var booking=await Book(ev,buyer,2);
Check((decimal)(await Query("SELECT TotalAmount FROM [Event].[EventBookings] WHERE BookingId=@B",("@B",booking)))[0]["TotalAmount"]==20,"server snapshots price, ignores stale caller amount/capacity");
await Error(51091,()=>Book(ev,watcher),"sold-out capacity is enforced");
await Query("EXEC [Event].[SetWaitlist] @E,@P,1,999",("@E",ev),("@P",watcher));
await Cancel(booking,buyer);
Check((int)(await Reliability(buyer))["ConsecutiveNegativeMarks"]==0,"on-time cancellation has no reliability effect");
Check((int)(await Query("SELECT COUNT(*) AS N FROM [Notification].[NotificationOutbox] WHERE RecipientId=@P AND NotificationType=N'EVENT_SEATS_AVAILABLE'",("@P",watcher)))[0]["N"]==1,"seat release enqueues waitlist notification");
var rebook=await Book(ev,buyer,2);
await Error(51469,()=>Book(ev,buyer),"duplicate live booking is rejected");
var sqlAssembly=Assembly.Load("Pawfront.Infrastructure.Sql");
var store=(IEventBookingSqlStore)Activator.CreateInstance(sqlAssembly.GetType("Pawfront.Infrastructure.Sql.Events.SqlEventBookingStore")!,connectionString,null)!;
var detail=await store.GetAsync(booking,default);
Check(detail!.Status=="CancelledByParent" && detail.CancelledAtUtc is not null,"cancelled detail keeps status/date and parses ticket result sets");
var eventStore=(IEventSqlStore)Activator.CreateInstance(sqlAssembly.GetType("Pawfront.Infrastructure.Sql.Events.SqlEventStore")!,connectionString,null)!;
var current=(await eventStore.GetAsync(ev,default))!;
Check(current.Attendees!.Single(a=>a.TicketNumber==1).BookingId==rebook,"attendees carry booking identity and payment state");
var bookingService = (IEventBookingService)Activator.CreateInstance(Assembly.Load("Pawfront.Application").GetType("Pawfront.Application.Events.EventBookingService")!,new object?[]{null,store,null,null,null,null})!;
Check(await bookingService.HasCancelledBookingAsync(ev,buyer+"@test.invalid",default),"event detail remembers caller cancellation after rebooking");
Check(!await bookingService.HasCancelledBookingAsync(ev,watcher+"@test.invalid",default) && !await bookingService.HasCancelledBookingAsync(ev,null,default),"cancellation warning is private to the caller");
var activeCards = await store.ListByBookerEmailAsync(buyer+"@test.invalid",default);
Check(activeCards.Where(b=>b.EventId==ev).All(b=>b.TotalBookings==2 && b.MaxBookings==2),"cards include event-wide ticket counts, excluding cancelled tickets");
var sharedEvent = await Event(host,10); var sharedBuyer = await Parent(); var sharedOther = await Parent();
await Book(sharedEvent,sharedBuyer,2); await Book(sharedEvent,sharedOther,3);
Check((await store.ListByBookerEmailAsync(sharedBuyer+"@test.invalid",default)).Single(b=>b.EventId==sharedEvent).TotalBookings==5,"card counts all parents' tickets instead of bookings or the caller's tickets");
var input=new UpdateParentEventSqlInput(ev,host,current.EventCategory,true,current.Title,"Edited",null,Array.Empty<string>(),current.EventType,current.StartDate,current.EndDate,current.StartTime,current.EndTime,true,10,current.CancellationPolicy,null,4);
await Error(51465,()=>eventStore.UpdateByParentAsync(input,default),"booked events reject edits at SQL boundary");
await Error(51460,()=>Query("EXEC [Event].[CancelHostedEvent] @E,@P",("@E",ev),("@P",buyer)),"host cancellation ownership enforced");
await Query("EXEC [Event].[CancelHostedEvent] @E,@P",("@E",ev),("@P",host));
await Query("EXEC [Event].[CancelHostedEvent] @E,@P",("@E",ev),("@P",host));
Check((await store.GetAsync(rebook,default))!.Status=="EventCancelled","host cancellation reaches attendee booking");
Check((int)(await Query("SELECT COUNT(*) AS N FROM [Notification].[NotificationOutbox] WHERE RecipientId=@P AND NotificationType=N'EVENT_CANCELLED'",("@P",buyer)))[0]["N"]==1,"host cancellation notification is idempotent");
await Error(51461,()=>Book(ev,watcher),"cancelled events cannot be booked");
await Query("EXEC [Event].[RemoveHostedEvent] @E,@P",("@E",ev),("@P",host));
Check(!(await eventStore.ListByPetParentAsync(host,default)).Any(x=>x.EventId==ev),"remove hides only host list");
Check((await eventStore.GetAsync(ev,default))!.EventsCancelledCount==1,"cancelled event remains readable and counts for host");
var cancelledCards = (await store.ListByBookerEmailAsync(buyer+"@test.invalid",default)).Where(b=>b.EventId==ev).ToArray();
Check(cancelledCards.Length==2 && cancelledCards.Any(b=>b.Status=="EventCancelled") && cancelledCards.All(b=>b.TotalBookings==0),"host removal preserves attendee history and releases counts");
var parentEndpoints = Assembly.Load("Pawfront.PetParentApi");
var mapCard = parentEndpoints.GetType("Pawfront.PetParentApi.Endpoints.PetParentEndpoints")!.GetMethod("ToSummaryResponse",BindingFlags.Static|BindingFlags.NonPublic)!;
var hostCard = (Pawfront.Contracts.Events.EventBookingSummaryResponse)mapCard.Invoke(null,new object[]{cancelledCards.Single(b=>b.Status=="EventCancelled")})!;
var ownCard = (Pawfront.Contracts.Events.EventBookingSummaryResponse)mapCard.Invoke(null,new object[]{cancelledCards.Single(b=>b.Status=="CancelledByParent")})!;
Check(hostCard.Status=="host_cancelled" && hostCard.IsCancelledByHost && !hostCard.EventCancelledByMe,"host-cancelled API card distinguishes who cancelled");
Check(ownCard.EventCancelledByMe && !ownCard.IsCancelledByHost,"parent-cancelled API card marks caller's cancellation");
// Provider hosts use the same atomic lifecycle, with role-specific ownership.
var providerHost=Guid.NewGuid();
await Query("""
    DECLARE @Auth UNIQUEIDENTIFIER=NEWID();
    INSERT [Provider].[ProviderAuthIdentities](ProviderAuthIdentityId,FirebaseUserId,AuthProvider,Email)
    VALUES(@Auth,CONVERT(nvarchar(36),@Id),N'EmailPassword',CONCAT(@Id,N'@test.invalid'));
    INSERT [Provider].[Providers](ProviderId,ProviderAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth)
    VALUES(@Id,@Auth,N'Test',N'Provider',N'Other',N'+41',LEFT(CONVERT(nvarchar(36),@Id),16),'1990-01-01');
    """,("@Id",providerHost));
var providerEvent=await Event(host); var providerBuyer=await Parent();
await Query("UPDATE [Event].[Events] SET PetParentId=NULL,ProviderId=@P WHERE EventId=@E",("@P",providerHost),("@E",providerEvent));
var providerBooking=await Book(providerEvent,providerBuyer);
var lifecycle=(IEventLifecycleStore)Activator.CreateInstance(sqlAssembly.GetType("Pawfront.Infrastructure.Sql.Events.SqlEventLifecycleStore")!,connectionString,null)!;
await Error(51460,()=>lifecycle.CancelByProviderAsync(providerEvent,Guid.NewGuid(),default),"other provider cannot cancel host's event");
await Error(51460,()=>lifecycle.CancelAsync(providerEvent,providerHost,default),"parent cancellation cannot act as provider even with same id");
await Error(51460,()=>lifecycle.CancelByProviderAsync(sharedEvent,host,default),"provider cancellation cannot act as parent even with same id");
await lifecycle.CancelByProviderAsync(providerEvent,providerHost,default);
await lifecycle.CancelByProviderAsync(providerEvent,providerHost,default);
Check((await eventStore.GetAsync(providerEvent,default))!.EventStatus=="Cancelled","provider host cancellation persists event status");
var toEventResult=Assembly.Load("Pawfront.Application").GetType("Pawfront.Application.Events.EventService")!.GetMethod("ToResult",BindingFlags.Static|BindingFlags.NonPublic)!;
var providerEventResult=(EventResult)toEventResult.Invoke(null,new object?[]{await eventStore.GetAsync(providerEvent,default),null,null})!;
var toProviderResponse=Assembly.Load("Pawfront.Api").GetType("Pawfront.Api.Endpoints.EventEndpoints")!.GetMethod("ToResponse",BindingFlags.Static|BindingFlags.NonPublic)!;
var providerEventResponse=(Pawfront.Contracts.Events.EventResponse)toProviderResponse.Invoke(null,new object?[]{providerEventResult,true,null})!;
Check(providerEventResponse.EventStatus=="Cancelled" && providerEventResponse.IsCancelledByHost,"provider event response exposes Cancelled and host flag");
Check((await store.GetAsync(providerBooking,default))!.Status=="EventCancelled","provider host cancellation updates attendee booking");
Check((int)(await Query("SELECT COUNT(*) AS N FROM [Notification].[NotificationOutbox] WHERE RecipientId=@P AND NotificationType=N'EVENT_CANCELLED'",("@P",providerBuyer)))[0]["N"]==1,"provider cancellation enqueues attendee notification once");
var providerMapCard=Assembly.Load("Pawfront.Api").GetType("Pawfront.Api.Endpoints.EventBookingEndpoints")!.GetMethod("ToSummaryResponse",BindingFlags.Static|BindingFlags.NonPublic)!;
var providerCard=(Pawfront.Contracts.Events.EventBookingSummaryResponse)providerMapCard.Invoke(null,new object[]{(await store.ListByBookerEmailAsync(providerBuyer+"@test.invalid",default)).Single()})!;
Check(providerCard.Status=="host_cancelled" && providerCard.IsCancelledByHost,"provider booking API exposes host_cancelled and host flag");
Check(!(await lifecycle.GetReliabilityAsync(providerBuyer,default)).IsRedFlagged,"host cancellation does not penalize attendees");
var json = System.Text.Json.JsonSerializer.Serialize(hostCard,new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
using(var document = System.Text.Json.JsonDocument.Parse(json))
    Check(document.RootElement.GetProperty("bookings").GetProperty("totalBookings").GetInt32()==0 && document.RootElement.GetProperty("bookings").GetProperty("maxBookings").GetInt32()==2 && document.RootElement.TryGetProperty("bookingsCloseAtUtc",out _),"booking card serializes counts and UTC cutoff in client shape");
// Direct SQL rejects purchases inside the final hour and while live, for either account role.
var cutoffBuyer=await Parent(); var closingEvent=await Event(host,10,1);
await Query("""
    DECLARE @Start DATETIME2=CONVERT(DATETIME2,DATEADD(second,3598,SYSUTCDATETIME()) AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time');
    UPDATE [Event].[Events] SET StartDate=CAST(@Start AS DATE),EndDate=CAST(@Start AS DATE),StartTime=CAST(@Start AS TIME) WHERE EventId=@E;
    """,("@E",closingEvent));
await Error(51470,()=>Book(closingEvent,cutoffBuyer),"SQL rejects at or after the one-hour cutoff");
var liveEvent=await Event(host,10,0);
await Error(51470,()=>Book(liveEvent,cutoffBuyer),"SQL rejects purchases while event is live");
var openEvent=await Event(host,10,2); await Book(openEvent,cutoffBuyer);
Check(true,"SQL permits purchases before the one-hour cutoff");
try
{
    await store.CreateAsync(new CreateEventBookingSqlInput(closingEvent,"Test",cutoffBuyer+"@test.invalid",null,"Cash",10,10,new[]{"Attendee"},cutoffBuyer),default);
    throw new Exception("Expected BookingsClosed from SQL store");
}
catch(EventBookingsClosedException ex) { Check(ex.Message=="Ticket bookings for this event are closed.","SQL store translates cutoff into the exact BookingsClosed message"); }
var empty=await Event(host);
await Error(51466,()=>Query("EXEC [Event].[RemoveHostedEvent] @E,@P",("@E",empty),("@P",host)),"active event cannot be removed");
await Query("EXEC [Event].[CancelHostedEvent] @E,@P",("@E",empty),("@P",host));
Check((await eventStore.GetAsync(empty,default))!.EventsCancelledCount==1,"unbooked cancellation does not count");
// Two late cancellations, then account restrictions and expiry.
for(int i=0;i<2;i++) { var late=await Event(host,5,12); var b=await Book(late,buyer); await Cancel(b,buyer); }
Check((bool)(await Reliability(buyer))["IsRedFlagged"],"two late cancellations flag account");
var held=await Book(await Event(host),buyer);
await Error(51463,async ()=>{ await Book(await Event(host),buyer); },"red flag reduces booking limit to one");
await Error(51464,()=>Query("""
 EXEC [Event].[CreatePetParentEvent] @PetParentId=@P,@EventCategory=N'MeetupsAndSocials',@IsChildFriendly=1,@Title=N'T',@Description=N'D',@EventType=N'Online',@StartDate='2030-01-01',@EndDate='2030-01-01',@StartTime='10:00',@EndTime='11:00';
 """,("@P",buyer)),"red flag prevents event hosting");
await Query("UPDATE [Event].[Reliability] SET RedFlagExpiresAt=DATEADD(second,-1,SYSUTCDATETIME()) WHERE AccountId=@P",("@P",buyer));
Check(!(bool)(await Reliability(buyer))["IsRedFlagged"],"red flag expires without a scheduler mutation");
// Another late mark restarts an active flag immediately, for both late windows.
foreach(var policy in new[]{"FullRefundUpTo24Hours","FullRefundUpTo48Hours"})
{
    var flagged=await Parent(); var lateEvent=await Event(host,5,12);
    await Query("UPDATE [Event].[Events] SET CancellationPolicy=@Policy WHERE EventId=@E",("@Policy",policy),("@E",lateEvent));
    await Query("""
        INSERT [Event].[Reliability](AccountId,RedFlagExpiresAt,ConsecutivePaidEventsAttended)
        VALUES(@P,DATEADD(day,16,SYSUTCDATETIME()),1);
        """,("@P",flagged));
    var lateBooking=await Book(lateEvent,flagged);
    var before=(DateTime)(await Query("SELECT SYSUTCDATETIME() AS Now"))[0]["Now"];
    await Cancel(lateBooking,flagged);
    var after=(DateTime)(await Query("SELECT SYSUTCDATETIME() AS Now"))[0]["Now"];
    var refreshed=await lifecycle.GetReliabilityAsync(flagged,default);
    Check(refreshed.IsRedFlagged && refreshed.RedFlagExpiresAt>=new DateTimeOffset(before.AddDays(30),TimeSpan.Zero)
        && refreshed.RedFlagExpiresAt<=new DateTimeOffset(after.AddDays(30),TimeSpan.Zero),$"{policy}: reliability read immediately shows restarted 30-day flag");
    Check(refreshed.ConsecutivePaidEventsAttended==0,$"{policy}: late mark resets paid attendance recovery");
    await Query("EXEC [Event].[RecordReliabilityOutcome] @B,N'Negative'",("@B",lateBooking));
    Check((await lifecycle.GetReliabilityAsync(flagged,default)).RedFlagExpiresAt==refreshed.RedFlagExpiresAt,$"{policy}: duplicate outcome does not restart timer again");
}
// Concurrent requests cannot bypass maximum five bookings.
var concurrent=await Parent(); var events=new List<Guid>(); for(int i=0;i<6;i++) events.Add(await Event(host));
var outcomes=await Task.WhenAll(events.Select(async e=> {try {await Book(e,concurrent);return 0;}catch(SqlException ex){return ex.Number;}}));
Check(outcomes.Count(x=>x==0)==5 && outcomes.Count(x=>x==51462)==1,"concurrent sixth booking rejected with distinct code");
// Reporting persists a link, rejects other bookers, and remains reported after support closes it.
var report=await Query("EXEC [Support].[CreateTicket] @TicketType=N'EventIncident',@RaisedByType=N'PetParent',@ActorId=@P,@EventId=@E,@EventBookingId=@B,@Reason=N'Test'",("@P",buyer),("@E",ev),("@B",rebook));
Check((string)report[0]["Outcome"]=="Created" && (Guid)report[0]["EventBookingId"]==rebook,"support ticket links event and booking");
await Error(51343,()=>Query("EXEC [Support].[CreateTicket] @TicketType=N'EventIncident',@RaisedByType=N'PetParent',@ActorId=@P,@EventId=@E,@EventBookingId=@B,@Reason=N'Test'",("@P",watcher),("@E",ev),("@B",rebook)),"report requires booking ownership");
var supportStore=(ISupportTicketStore)Activator.CreateInstance(sqlAssembly.GetType("Pawfront.Infrastructure.Sql.Support.SqlSupportTicketStore")!,connectionString,null)!;
var reportId=(Guid)report[0]["TicketId"];
Check((await supportStore.GetAsync(reportId,"PetParent",buyer,default))!.EventBookingId==rebook,"support detail reads linked booking");
var page=await supportStore.ListAsync(new SupportTicketQuery("PetParent",buyer,null,SupportTicketSortBy.CreatedAt,Pawfront.Application.Earnings.EarningsSortDirection.Descending,0,20),default);
Check(page.TotalCount==1 && page.Items[0].EventBookingId==rebook,"support listing preserves count and booking link");
Check((await supportStore.AddPhotoAsync(reportId,"PetParent",buyer,"https://example.invalid/evidence.jpg",default)).EventBookingId==rebook,"support photo response preserves booking link");
await Query("UPDATE [Support].[Tickets] SET Status=N'CLOSED',ClosedAtUtc=SYSUTCDATETIME() WHERE EventBookingId=@B",("@B",rebook));
report=await Query("EXEC [Support].[CreateTicket] @TicketType=N'EventIncident',@RaisedByType=N'PetParent',@ActorId=@P,@EventId=@E,@EventBookingId=@B,@Reason=N'Test'",("@P",buyer),("@E",ev),("@B",rebook));
Check((string)report[0]["Outcome"]=="TicketAlreadyOpen" && (await store.GetAsync(rebook,default))!.HasReported,"once-per-booking survives ticket closure");
// Cash undo, partial cash statistics, midnight settlement and exactly-once negative outcomes.
var day=await Event(host); var dayBuyer=await Parent(); var dayBooking=await Book(day,dayBuyer,2);
await Error(51467,()=>Cash(dayBooking,host,1,"CashReceived"),"cash recording blocked before event day");
await Query("UPDATE [Event].[Events] SET StartDate=CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time' AS DATE),EndDate=CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time' AS DATE) WHERE EventId=@E",("@E",day));
await Error(51460,()=>Cash(dayBooking,watcher,1,"CashReceived"),"cash recording requires host ownership");
await Cash(dayBooking,host,1,"CashReceived"); await Cash(dayBooking,host,1,"Pending");
Check((await store.GetAsync(dayBooking,default))!.Tickets[0].PaymentStatus=="Pending","cash receipt can be undone");
await Cash(dayBooking,host,1,"CashReceived");
current=(await eventStore.GetAsync(day,default))!;
Check(current.AttendedCount==1 && current.EarnedAmount==10,"host stats count cash received per ticket");
await Query("UPDATE [Event].[Events] SET StartDate=DATEADD(day,-1,StartDate),EndDate=DATEADD(day,-1,EndDate) WHERE EventId=@E",("@E",day));
await Error(51467,()=>Cash(dayBooking,host,2,"CashReceived"),"cash recording closes at event-day midnight");
await Query("EXEC [Event].[SettleEventAttendance]"); await Query("EXEC [Event].[SettleEventAttendance]");
Check((await store.GetAsync(dayBooking,default))!.Status=="NoShow" && (int)(await Reliability(dayBuyer))["ConsecutiveNegativeMarks"]==1,"unpaid ticket settles as NoShow once");
// SQL timezone conversion includes both summer and winter Zurich offsets.
var tz=(await Query("SELECT [Event].[EventStartUtc]('2026-07-01','00:00') AS Summer,[Event].[EventStartUtc]('2026-01-01','00:00') AS Winter"))[0];
Check((DateTime)tz["Summer"]==new DateTime(2026,6,30,22,0,0) && (DateTime)tz["Winter"]==new DateTime(2025,12,31,23,0,0),"Zurich daylight-saving midnight conversion");

// Two consecutive fully paid attended events recover a flagged account.
var recovering=await Parent();
await Query("INSERT [Event].[Reliability](AccountId,RedFlagExpiresAt) VALUES(@P,DATEADD(day,30,SYSUTCDATETIME()))",("@P",recovering));
for(int i=1;i<=2;i++)
{
    var attendedEvent=await Event(host); var attendedBooking=await Book(attendedEvent,recovering);
    await Query("UPDATE [Event].[Events] SET StartDate=CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time' AS DATE),EndDate=CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time' AS DATE) WHERE EventId=@E",("@E",attendedEvent));
    await Cash(attendedBooking,host,1,"CashReceived");
    Check((await store.GetAsync(attendedBooking,default))!.Status=="Confirmed","cash receipt keeps booking Attending until midnight");
    await Query("UPDATE [Event].[Events] SET StartDate=DATEADD(day,-1,StartDate),EndDate=DATEADD(day,-1,EndDate) WHERE EventId=@E",("@E",attendedEvent));
    await Query("EXEC [Event].[SettleEventAttendance]");
    var state=await Reliability(recovering);
    Check((int)state["ConsecutivePaidEventsAttended"]==i && (bool)state["IsRedFlagged"]==(i<2),$"paid attendance recovery progress {i}");
}
var expiredMark=await Parent();
await Query("INSERT [Event].[Reliability](AccountId,LastNegativeAtUtc,ConsecutiveNegativeMarks) VALUES(@P,DATEADD(day,-31,SYSUTCDATETIME()),1)",("@P",expiredMark));
var newLate=await Book(await Event(host,5,12),expiredMark); await Cancel(newLate,expiredMark);
var expiredState=await Reliability(expiredMark);
Check(!(bool)expiredState["IsRedFlagged"] && (int)expiredState["ConsecutiveNegativeMarks"]==1,"marks older than 30 days do not trigger a flag");
// Before the first booking, capacity updates preserve the event and round-trip SQL columns.
var editable=await Event(host); var editableRow=(await eventStore.GetAsync(editable,default))!;
var saved=await eventStore.UpdateByParentAsync(new UpdateParentEventSqlInput(editable,host,editableRow.EventCategory,true,editableRow.Title,"New description",null,Array.Empty<string>(),editableRow.EventType,editableRow.StartDate,editableRow.EndDate,editableRow.StartTime,editableRow.EndTime,true,10,editableRow.CancellationPolicy,null,7),default);
Check(saved.MaximumCapacity==7 && saved.Description=="New description","unbooked event capacity and description update");


var patch=System.Text.Json.JsonSerializer.Deserialize<Pawfront.Contracts.Events.PatchEventRequest>("{\"maximumCapacity\":12}",new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
Check(patch.MaximumCapacity.IsSet && patch.MaximumCapacity.Value==12 && !patch.Physical.IsSet,"top-level capacity binds without a physical replacement");
var merge=Assembly.Load("Pawfront.PetParentApi").GetType("Pawfront.PetParentApi.Endpoints.EventEndpoints")!.GetMethod("MergePhysical",BindingFlags.Static|BindingFlags.NonPublic)!;
var venue=new EventLocationResult("1","Street","Zurich","8000","Switzerland",47,8);
var merged=(PhysicalEventInput)merge.Invoke(null,new object?[]{patch.Physical,new PhysicalEventResult(5,venue),patch.MaximumCapacity})!;
Check(merged.MaximumCapacity==12 && merged.Location!.Street==venue.Street && merged.Location.Latitude==venue.Latitude,"capacity-only PATCH preserves venue");


// Paid-only restrictions: free holds never consume the flagged paid slot.
var paidOnlyParent=await Parent();
await Query("INSERT [Event].[Reliability](AccountId,RedFlagExpiresAt) VALUES(@P,DATEADD(day,30,SYSUTCDATETIME()))",("@P",paidOnlyParent));
async Task<Guid> FreeEvent(Guid owner)
{
    var id=await Event(owner);
    await Query("UPDATE [Event].[Events] SET IsPaid=0,Price=NULL WHERE EventId=@E",("@E",id));
    return id;
}
await Book(await FreeEvent(host),paidOnlyParent);
var paidOnlyBooking=await Book(await Event(host),paidOnlyParent);
await Book(await FreeEvent(host),paidOnlyParent);
await Error(51463,async()=>{await Book(await Event(host),paidOnlyParent);},"flagged parent with free bookings can hold one paid booking but not two");
Check((await lifecycle.GetReliabilityAsync(paidOnlyParent,default)).ActivePaidBookingCount==1,"reliability reports upcoming paid bookings separately");
await Cancel(paidOnlyBooking,paidOnlyParent);
Check((await lifecycle.GetReliabilityAsync(paidOnlyParent,default)).ActivePaidBookingCount==0,"cancelled paid bookings release flagged slot");

var reliabilityReader=(Pawfront.Application.Reliability.IReliabilityReader)Activator.CreateInstance(sqlAssembly.GetType("Pawfront.Infrastructure.Sql.Reliability.SqlReliabilityReader")!,connectionString,null)!;
var flaggedCommunity=(await reliabilityReader.GetParentAsync(paidOnlyParent,default)).Community;
Check(flaggedCommunity.ActivePaidBookingCount==0 && flaggedCommunity.AvailableBookingSlots==1,"account reliability gives flagged parent a paid slot despite free bookings");

// Both remaining and newly registered waiters receive one reminder each.
var reminderEvent=await Event(host,1); var reminderBuyer=await Parent(); var reminderA=await Parent(); var reminderB=await Parent();
var reminderBooking=await Book(reminderEvent,reminderBuyer);
foreach(var parent in new[]{reminderA,reminderB})
    await Query("EXEC [Event].[SetWaitlist] @E,@P,1",("@E",reminderEvent),("@P",parent));
Check((await lifecycle.GetWaitlistAsync(reminderA,default)).Contains(reminderEvent),"waitlist membership persists when read again");
await Cancel(reminderBooking,reminderBuyer);
await Task.WhenAll(Enumerable.Range(0,4).Select(_=>Query("EXEC [Event].[NotifyEventWaitlist] @E",("@E",reminderEvent))));
Check((int)(await Query("SELECT COUNT(*) N FROM [Notification].[NotificationOutbox] WHERE EntityId=@E AND NotificationType=N'EVENT_SEATS_AVAILABLE'",("@E",reminderEvent)))[0]["N"]==2,"repeated concurrent releases notify all waiters exactly once");
await Book(reminderEvent,reminderA);
Check(!(await lifecycle.GetWaitlistAsync(reminderA,default)).Contains(reminderEvent) && (await lifecycle.GetWaitlistAsync(reminderB,default)).Contains(reminderEvent),"booking clears only the winning parent's reminder");
await Error(51091,()=>Book(reminderEvent,reminderB),"other waiters see sold out after first booking");
await Query("UPDATE [Event].[Events] SET StartDate=CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time' AS DATE),EndDate=DATEADD(day,1,CAST(SYSUTCDATETIME() AS DATE)),StartTime='00:00' WHERE EventId=@E",("@E",reminderEvent));
Check(!(await lifecycle.GetWaitlistAsync(reminderB,default)).Contains(reminderEvent),"started event stops showing waitlist tick immediately");
await Query("EXEC [Event].[SettleEventAttendance]");
Check((int)(await Query("SELECT COUNT(*) N FROM [Event].[Waitlist] WHERE EventId=@E",("@E",reminderEvent)))[0]["N"]==0,"sweep deletes waitlist rows when event starts");

// One view per authenticated account, including concurrent opens; never count the host.
var viewEvent=await Event(host);
Task View(Guid viewer)=>Query("EXEC [Event].[IncrementEventCounter] @E,N'View',@P,N'PetParent'",("@E",viewEvent),("@P",viewer));
await View(host);
await Task.WhenAll(Enumerable.Range(0,8).Select(_=>View(reminderA)));
await View(reminderB);
Check((int)(await Query("SELECT ViewCount FROM [Event].[Events] WHERE EventId=@E",("@E",viewEvent)))[0]["ViewCount"]==2,"unique account views count once under concurrency and exclude the host");
await Error(51097,()=>Query("EXEC [Event].[IncrementEventCounter] @E,N'View'",("@E",viewEvent)),"identity-free calls cannot increment views");
await eventStore.IncrementCounterAsync(viewEvent,"View",default,new Pawfront.Application.Blocks.BlockParty(Pawfront.Application.Blocks.BlockPartyType.PetParent,reminderB));
Check((int)(await Query("SELECT COUNT(*) N FROM [Event].[EventViews] WHERE EventId=@E",("@E",viewEvent)))[0]["N"]==2,"SQL adapter forwards caller identity");

// Receipt and undo notifications reflect real transitions and preserve paid state on reopen.
var receiptParent=await Parent(); var receiptEvent=await Event(host); var receiptBooking=await Book(receiptEvent,receiptParent,2);
await Query("UPDATE [Event].[Events] SET StartDate=CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time' AS DATE),EndDate=CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time' AS DATE) WHERE EventId=@E",("@E",receiptEvent));
await Cash(receiptBooking,host,1,"CashReceived"); await Cash(receiptBooking,host,1,"CashReceived");
Check((await store.GetAsync(receiptBooking,default))!.Status=="Confirmed","partial receipt leaves booking confirmed");
await Cash(receiptBooking,host,2,"CashReceived");
Check((await store.GetAsync(receiptBooking,default))!.Status=="Confirmed" && (await store.ListByBookerEmailAsync(receiptParent+"@test.invalid",default)).Single().Status=="Confirmed","full receipt remains Attending until midnight in list and detail");
await Cash(receiptBooking,host,2,"Pending"); await Cash(receiptBooking,host,2,"Pending");
Check((await store.GetAsync(receiptBooking,default))!.Status=="Confirmed" && (await store.ListByBookerEmailAsync(receiptParent+"@test.invalid",default)).Single().Status=="Confirmed","undo returns booking list and detail to Confirmed");
await Cash(receiptBooking,host,2,"CashReceived");
var receipts=await Query("SELECT NotificationType,EntityId,DataJson FROM [Notification].[NotificationOutbox] WHERE RecipientId=@P AND EntityId=@B ORDER BY CreatedAtUtc",("@P",receiptParent),("@B",receiptBooking));
Check(receipts.Count==3 && receipts.Count(r=>(string)r["NotificationType"]=="EVENT_BOOKING_CASH_RECEIVED")==2 && receipts.Count(r=>(string)r["NotificationType"]=="EVENT_BOOKING_PAYMENT_PENDING")==1,"whole-booking cash transitions notify once and a later receipt can notify again");
foreach(var receipt in receipts)
{
    var data=System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>((string)receipt["DataJson"])!;
    var type=(string)receipt["NotificationType"];
    Check(data["bookingId"]==receiptBooking.ToString() && data["entityId"]==receiptBooking.ToString() && data["eventId"]==receiptEvent.ToString() && data["amount"]=="20" && data["category"]=="EVENT" && data["hostName"]=="Test Parent","cash outbox has attendee envelope and whole-booking amount");
    var rendered=Pawfront.Application.Notifications.NotificationRenderer.Render(type,Pawfront.Application.Notifications.NotificationAudience.PetParent,data)!;
    Check(rendered.Title==(type=="EVENT_BOOKING_CASH_RECEIVED"?"Thank You for Your Payment":"Event Payment Pending") && rendered.Body==(type=="EVENT_BOOKING_CASH_RECEIVED"?"Test Parent has confirmed receiving CHF 20 for Test Event.":"Test Parent marked your CHF 20 payment for Test Event as pending. Please check with the host."),"cash notification uses requested copy");
}

// Deleting an account cancels hosting and attendee holds atomically, without reliability marks.
var deleting=await Parent(); var deletionHost=await Parent(); var deletionWaiter=await Parent(); var hostedAttendee=await Parent();
var releasedEvent=await Event(deletionHost,1,12); var releasedBooking=await Book(releasedEvent,deleting);
await Query("EXEC [Event].[SetWaitlist] @E,@P,1",("@E",releasedEvent),("@P",deletionWaiter));
var deletedHosted=await Event(deleting); var deletedHostedBooking=await Book(deletedHosted,hostedAttendee);
var deletingWaitEvent=await Event(host,1); await Book(deletingWaitEvent,await Parent());
await Query("EXEC [Event].[SetWaitlist] @E,@P,1",("@E",deletingWaitEvent),("@P",deleting));
await Query("EXEC [Parent].[DeletePetParent] @P",("@P",deleting));
await Query("EXEC [Parent].[DeletePetParent] @P",("@P",deleting));
Check((await store.GetAsync(releasedBooking,default))!.Status=="AccountDeleted","account deletion releases upcoming confirmed attendee booking");
Check((int)(await Reliability(deleting))["ConsecutiveNegativeMarks"]==0,"deletion inside late cancellation window has no reliability penalty");
Check((await eventStore.GetAsync(releasedEvent,default))!.TotalBookings==0,"deleted attendee no longer consumes seats");
Check((await eventStore.GetAsync(deletedHosted,default))!.EventStatus=="Cancelled" && (await store.GetAsync(deletedHostedBooking,default))!.Status=="EventCancelled","deleted host's upcoming event is cancelled for attendees");
Check((int)(await Query("SELECT COUNT(*) N FROM [Event].[Waitlist] WHERE PetParentId=@P",("@P",deleting)))[0]["N"]==0,"deletion clears parent's waitlist rows");
Check((int)(await Query("SELECT COUNT(*) N FROM [Notification].[NotificationOutbox] WHERE NotificationType=N'EVENT_EDITABLE_AGAIN' AND EntityId=@B",("@B",releasedBooking)))[0]["N"]==1,"deletion notifies affected host once");
Check((int)(await Query("SELECT COUNT(*) N FROM [Notification].[NotificationOutbox] WHERE NotificationType=N'EVENT_SEATS_AVAILABLE' AND RecipientId=@P AND EntityId=@E",("@P",deletionWaiter),("@E",releasedEvent)))[0]["N"]==1,"deletion notifies waiters about freed seats once");
Check((int)(await Query("SELECT COUNT(*) N FROM [Notification].[NotificationOutbox] WHERE NotificationType=N'EVENT_CANCELLED' AND RecipientId=@P",("@P",hostedAttendee)))[0]["N"]==1,"deleting host notifies attendees once");
await Book(releasedEvent,deletionWaiter);
await Error(51468,()=>Book(releasedEvent,deleting),"deleted account cannot reacquire seats");

// Events V3: role routing, repeated transitions, timing and paid/free outcomes.
async Task<int> Notices(Guid eventId, string type) => (int)(await Query("SELECT COUNT(*) N FROM [Notification].[NotificationOutbox] WHERE JSON_VALUE(DataJson,'$.eventId')=CONVERT(nvarchar(36),@E) AND NotificationType=@T",("@E",eventId),("@T",type)))[0]["N"];
async Task<Dictionary<string,object>> Notice(Guid eventId, string type) => (await Query("SELECT TOP(1) * FROM [Notification].[NotificationOutbox] WHERE JSON_VALUE(DataJson,'$.eventId')=CONVERT(nvarchar(36),@E) AND NotificationType=@T ORDER BY CreatedAtUtc DESC",("@E",eventId),("@T",type)))[0];
Check(await Notices(day,"EVENT_MISSED")==1 && await Notices(day,"EVENT_PROFILE_FLAGGED")==0,"first paid system no-show sends missed-event notice only");
Check((int)(await Query("SELECT COUNT(*) N FROM [Notification].[NotificationOutbox] WHERE RecipientId=@P AND NotificationType=N'EVENT_PROFILE_FLAGGED'",("@P",buyer)))[0]["N"]==0,"own late cancellations never send flag self-notifications");
var v3host=await Parent(); var v3a=await Parent(); var v3b=await Parent();
var v3event=await Event(v3host,2); var v3booking=await Book(v3event,v3a);
Check(await Notices(v3event,"EVENT_FIRST_BOOKING")==1 && await Notices(v3event,"EVENT_TICKET_SOLD")==0,"V3 first booking replaces routine ticket sale notice");
var v3second=await Book(v3event,v3b);
Check(await Notices(v3event,"EVENT_FIRST_BOOKING")==1 && await Notices(v3event,"EVENT_SOLD_OUT")==1,"only first booking and sold-out transitions notify host");
var v3firstNotice=await Notice(v3event,"EVENT_FIRST_BOOKING");
Check((Guid)v3firstNotice["RecipientId"]==v3host && (string)v3firstNotice["Audience"]=="PetParent","host role uses Parent app inbox");
await Cancel(v3second,v3b);
Check(await Notices(v3event,"EVENT_EDITABLE_AGAIN")==0 && await Notices(v3event,"EVENT_TICKET_CANCELLED")==0,"ordinary cancellation does not notify host");
await Cancel(v3booking,v3a);
Check(await Notices(v3event,"EVENT_EDITABLE_AGAIN")==1,"last cancellation notifies event unlock");
var unlockedSnapshot=(await eventStore.GetAsync(v3event,default))!;
var unlockedInput=new UpdateParentEventSqlInput(v3event,v3host,unlockedSnapshot.EventCategory,unlockedSnapshot.IsChildFriendly,unlockedSnapshot.Title,"Edited after all attendees cancelled",unlockedSnapshot.BannerImageUrl,unlockedSnapshot.Amenities,unlockedSnapshot.EventType,unlockedSnapshot.StartDate,unlockedSnapshot.EndDate,unlockedSnapshot.StartTime,unlockedSnapshot.EndTime,unlockedSnapshot.IsPaid,unlockedSnapshot.Price,unlockedSnapshot.CancellationPolicy,unlockedSnapshot.EventLink,2);
await eventStore.UpdateByParentAsync(unlockedInput,default);
Check((await eventStore.GetAsync(v3event,default))!.Description=="Edited after all attendees cancelled","unlock notification corresponds to an editable event");

var v3again=await Book(v3event,v3a,2);
Check(await Notices(v3event,"EVENT_FIRST_BOOKING")==2 && await Notices(v3event,"EVENT_SOLD_OUT")==2,"first-booking and sold-out notices repeat after unlocking");

// Cancellation carries host and date, and only reaches active attendees.
await Query("EXEC [Event].[CancelHostedEvent] @E,@P",("@E",v3event),("@P",v3host));
var v3cancel=await Notice(v3event,"EVENT_CANCELLED");
var v3cancelData=System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>((string)v3cancel["DataJson"])!;
Check(await Notices(v3event,"EVENT_CANCELLED")==1 && (Guid)v3cancel["RecipientId"]==v3a && v3cancelData["hostName"]=="Test Parent" && v3cancelData.ContainsKey("serviceStartUtc"),"host cancellation sends attendee the host and scheduled date");

var v3free=await Event(v3host); var v3freeParent=await Parent();
await Query("UPDATE [Event].[Events] SET IsPaid=0,Price=NULL,CancellationPolicy=NULL WHERE EventId=@E",("@E",v3free));
var v3freeBooking=await Book(v3free,v3freeParent);
await Query("UPDATE [Event].[Events] SET StartDate=CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time' AS DATE),EndDate=CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time' AS DATE),StartTime='00:00',EndTime='23:59:59' WHERE EventId=@E",("@E",v3free));
await Error(51468,()=>Cash(v3freeBooking,v3host,1,"CashReceived"),"free events cannot record attendance or cash");
await Query("EXEC [Event].[SendEventNotifications]");
Check(await Notices(v3free,"EVENT_LIVE_FREE")==1 && await Notices(v3free,"EVENT_LIVE")==0 && await Notices(v3free,"EVENT_RECORD_REMAINING")==0,"free event gets live copy without cash or recording prompts");
await Query("UPDATE [Event].[Events] SET StartDate=DATEADD(day,-1,StartDate),EndDate=DATEADD(day,-1,EndDate) WHERE EventId=@E",("@E",v3free));
await Query("EXEC [Event].[SettleEventAttendance]; EXEC [Event].[SendEventNotifications]; EXEC [Event].[SettleEventAttendance]; EXEC [Event].[SendEventNotifications]");
Check(await Notices(v3free,"EVENT_ATTENDED")==1 && await Notices(v3free,"EVENT_HOSTED_FREE")==1 && await Notices(v3free,"EVENT_MISSED")==0 && await Notices(v3free,"EVENT_PROFILE_FLAGGED")==0,"free event completion notifies once without no-show or red-flag notices");
var v3thanks=await Notice(v3free,"EVENT_ATTENDED"); var v3hostThanks=await Notice(v3free,"EVENT_HOSTED_FREE");
var v3times=(await Query("SELECT DATEADD(hour,24,[Event].[EventStartUtc](EndDate,EndTime)) AS ThanksAt,[Event].[EventStartUtc](DATEADD(day,1,EndDate),'09:00') AS Morning FROM [Event].[Events] WHERE EventId=@E",("@E",v3free)))[0];
Check((DateTime)v3thanks["NextAttemptAtUtc"]==(DateTime)v3times["ThanksAt"] && (DateTime)v3hostThanks["NextAttemptAtUtc"]==(DateTime)v3times["Morning"],"attendee thanks waits 24 hours after end; host summary waits until 09:00 Zurich");

// A second system no-show replaces A-S6 with A-S7, never both.
var v3missedParent=await Parent(); var v3missed=await Event(v3host); var v3missedBooking=await Book(v3missed,v3missedParent);
await Query("INSERT [Event].[Reliability](AccountId,LastNegativeAtUtc,ConsecutiveNegativeMarks) VALUES(@P,SYSUTCDATETIME(),1); UPDATE [Event].[Events] SET StartDate=DATEADD(day,-1,CAST(SYSUTCDATETIME() AS DATE)),EndDate=DATEADD(day,-1,CAST(SYSUTCDATETIME() AS DATE)) WHERE EventId=@E",("@P",v3missedParent),("@E",v3missed));
await Query("EXEC [Event].[SettleEventAttendance]");
Check(await Notices(v3missed,"EVENT_PROFILE_FLAGGED")==1 && await Notices(v3missed,"EVENT_MISSED")==0,"second system strike sends only flagged notice");
Check(await Notices(v3missed,"EVENT_HOSTED")==0,"paid event without recorded attendees has no host thank-you");
var v3flag=await Notice(v3missed,"EVENT_PROFILE_FLAGGED");
Check((int)(await Query("SELECT DATEPART(hour,CAST(@At AS datetime2) AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time') AS H",("@At",v3flag["NextAttemptAtUtc"])))[0]["H"]==9,"red-flag notification is held to 09:00");
await Query("UPDATE [Event].[Reliability] SET RedFlagExpiresAt=DATEADD(second,-1,SYSUTCDATETIME()) WHERE AccountId=@P; EXEC [Event].[SendEventNotifications]; EXEC [Event].[SendEventNotifications]",("@P",v3missedParent));
Check(await Notices(v3missed,"EVENT_PROFILE_FLAG_REMOVED")==1,"expired flag emits one removal notification");

var v3paid=await Event(v3host); var v3paidParent=await Parent(); var v3paidBooking=await Book(v3paid,v3paidParent,2);
await Query("UPDATE [Event].[Events] SET StartDate=CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time' AS DATE),EndDate=CAST(SYSUTCDATETIME() AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time' AS DATE),StartTime='00:00' WHERE EventId=@E",("@E",v3paid));
await Cash(v3paidBooking,v3host,1,"CashReceived"); await Cash(v3paidBooking,v3host,2,"CashReceived");
await Query("UPDATE [Event].[Events] SET StartDate=DATEADD(day,-1,StartDate),EndDate=DATEADD(day,-1,EndDate) WHERE EventId=@E; EXEC [Event].[SettleEventAttendance]; EXEC [Event].[SendEventNotifications]",("@E",v3paid));
var v3summary=await Notice(v3paid,"EVENT_HOSTED");
var v3summaryData=System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>((string)v3summary["DataJson"])!;
Check(v3summaryData["attendeeCount"]=="2" && v3summaryData["totalAmount"]=="20" && await Notices(v3paid,"EVENT_HOSTED_FREE")==0,"paid host summary carries recorded attendee count and total cash");
Check(await Notices(v3paid,"EVENT_ATTENDED")==1 && await Notices(v3paid,"EVENT_MISSED")==0,"recorded paid booking receives only attendee thank-you");

// Closing a support ticket notifies the reporter with a ticket detail link once.
var v3report=await Query("EXEC [Support].[CreateTicket] @TicketType=N'EventIncident',@RaisedByType=N'PetParent',@ActorId=@P,@EventId=@E,@EventBookingId=@B,@Reason=N'Test'",("@P",v3paidParent),("@E",v3paid),("@B",v3paidBooking));
var v3ticket=(Guid)v3report[0]["TicketId"];
Check(await Notices(v3paid,"EVENT_ISSUE_RESOLVED")==0,"report submission has no self-notification");
await Query("EXEC [Support].[CloseTicket] @T; EXEC [Support].[CloseTicket] @T",("@T",v3ticket));
var v3resolved=await Notice(v3paid,"EVENT_ISSUE_RESOLVED");
Check(await Notices(v3paid,"EVENT_ISSUE_RESOLVED")==1 && (Guid)v3resolved["RecipientId"]==v3paidParent && ((string)v3resolved["DataJson"]).Contains(v3ticket.ToString(),StringComparison.OrdinalIgnoreCase),"ticket resolution notifies only reporter once with ticket identity");
Check(((DateTime)v3resolved["NextAttemptAtUtc"]-(DateTime)v3resolved["CreatedAtUtc"]).TotalSeconds is >59 and <=60,"issue resolution uses configured 60-second delay");

// Stage exact reminders without relying on the machine's current hour.
var v3reminder=await Event(v3host); var v3reminderParent=await Parent(); var v3reminderBooking=await Book(v3reminder,v3reminderParent);
await Query("DECLARE @At datetime2=(SELECT DATEADD(hour,-24,[Event].[EventStartUtc](StartDate,StartTime)) FROM [Event].[Events] WHERE EventId=@E); EXEC [Event].[SendEventNotifications] @At; EXEC [Event].[SendEventNotifications] @At",("@E",v3reminder));
Check(await Notices(v3reminder,"EVENT_REMINDER_DAY_BEFORE")==1 && await Notices(v3reminder,"EVENT_HOST_REMINDER_DAY_BEFORE")==1,"day-before reminders reach both roles once");
var v3dayReminder=await Notice(v3reminder,"EVENT_REMINDER_DAY_BEFORE");
var v3dayDue=(DateTime)(await Query("SELECT DATEADD(second,60,DATEADD(hour,-24,[Event].[EventStartUtc](StartDate,StartTime))) AS Due FROM [Event].[Events] WHERE EventId=@E",("@E",v3reminder)))[0]["Due"];
Check((DateTime)v3dayReminder["NextAttemptAtUtc"]==v3dayDue,"attendee day-before reminder waits configured 60 seconds");
var v3late=await Event(v3host,5,12); await Book(v3late,await Parent());
await Query("EXEC [Event].[SendEventNotifications]");
Check(await Notices(v3late,"EVENT_REMINDER_DAY_BEFORE")==0 && await Notices(v3late,"EVENT_HOST_REMINDER_DAY_BEFORE")==0,"booking made inside 24 hours receives no day-before reminder for either role");
await Query("DECLARE @At datetime2=(SELECT DATEADD(hour,-1,[Event].[EventStartUtc](StartDate,StartTime)) FROM [Event].[Events] WHERE EventId=@E); EXEC [Event].[SendEventNotifications] @At",("@E",v3reminder));
Check(await Notices(v3reminder,"EVENT_REMINDER_STARTING_SOON")==1 && await Notices(v3reminder,"EVENT_HOST_REMINDER_STARTING_SOON")==1,"one-hour reminder reaches both roles when bookings close");

// Deferred first-booking push is suppressed after a quick cancellation.
var v3delay=(int)(await Query("SELECT DelaySeconds FROM [Notification].[EventNotificationDelivery] WHERE Code=N'H-U1'"))[0]["DelaySeconds"];
await Query("UPDATE [Notification].[EventNotificationDelivery] SET DelaySeconds=15 WHERE Code=N'H-U1'");
try
{
    var v3quick=await Event(v3host); var v3quickParent=await Parent(); var v3quickBooking=await Book(v3quick,v3quickParent);
    var v3delayed=await Notice(v3quick,"EVENT_FIRST_BOOKING");
    Check(((DateTime)v3delayed["NextAttemptAtUtc"]-(DateTime)v3delayed["CreatedAtUtc"]).TotalSeconds is >14 and <=15,"first-booking delay is configurable without an app change");
    await Cancel(v3quickBooking,v3quickParent);
    await Query("EXEC [Notification].[ClaimPendingNotifications] @BatchSize=1");
    Check((string)(await Notice(v3quick,"EVENT_FIRST_BOOKING"))["Status"]=="Failed","cancelled first booking is retired before delivery");
    await Query("BEGIN TRANSACTION; EXEC [Notification].[EnqueueNotification] @Audience=N'PetParent',@RecipientId=@P,@NotificationType=N'BOOKING_REQUESTED',@DataJson=N'{',@SuppressResultSet=1; EXEC [Notification].[ClaimPendingNotifications] @BatchSize=1; ROLLBACK TRANSACTION",("@P",v3quickParent));
    Check(true,"event relevance checks tolerate malformed unrelated notification data");
}
finally
{
    await Query("UPDATE [Notification].[EventNotificationDelivery] SET DelaySeconds=@D WHERE Code=N'H-U1'",("@D",v3delay));
}

Console.WriteLine($"{checks} event lifecycle integration checks passed.");
