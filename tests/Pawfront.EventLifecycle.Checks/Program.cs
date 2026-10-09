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
    Check((await store.GetAsync(attendedBooking,default))!.Status=="Attended","fully collected booking shows Attended");
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

Console.WriteLine($"{checks} event lifecycle integration checks passed.");
