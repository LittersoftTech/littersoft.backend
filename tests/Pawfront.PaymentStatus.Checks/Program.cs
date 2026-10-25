using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Pawfront.Application.Bookings;
using Pawfront.Application.Earnings;
using Pawfront.Application.Notifications;
using Pawfront.Application.Support;
using Pawfront.Contracts.Bookings;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}
async Task Reject<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { checks++; return; }
    throw new Exception(name);
}

Check(BookingStatuses.Normalize(" PAYMENT_NOT_RECEIVED ") == BookingStatuses.PaymentNotReceived, "Canonical status");
Check(BookingStatusFilter.Expand(["Completed"]).Contains(BookingStatuses.PaymentNotReceived), "Completed filter retains unpaid jobs");
Check(!BookingStatusFilter.Expand(["Upcoming", "Cancelled"]).Contains(BookingStatuses.PaymentNotReceived), "Finished job is neither upcoming nor cancelled");
Check(BookingStatusFilter.Expand(["PAYMENT_NOT_RECEIVED"]).SequenceEqual([BookingStatuses.PaymentNotReceived]), "Exact status filter");
Check(ProviderStatement.Group(BookingStatuses.PaymentNotReceived) == "Unpaid", "Statement unpaid grouping");
Check(NotificationTemplateCatalog.Find(NotificationTypes.BookingCashNotReceived, NotificationAudience.PetParent) is not null, "Parent notification is renderable");

var infrastructure = Assembly.Load("Pawfront.Infrastructure.Sql");
var providerId = Guid.NewGuid();
var parentId = Guid.NewGuid();
var date = new DateOnly(2099, 1, 1);
var location = new CapturedLocation(47m, 8m, 5m, null);
foreach (var night in new[] { false, true })
{
    var nightStore = (INightStayBookingSqlStore)Activator.CreateInstance(
        infrastructure.GetType("Pawfront.Infrastructure.Sql.Bookings.InMemoryNightStayBookingStore")!)!;
    var dayStore = (IBookingSqlStore)Activator.CreateInstance(
        infrastructure.GetType("Pawfront.Infrastructure.Sql.Bookings.InMemoryBookingStore")!, [nightStore])!;
    var dayRow = await dayStore.CreateAsync(providerId, parentId, null, Guid.NewGuid(), "PetSitter", "PetHotel",
        null, date, new(9, 0), new(10, 0), null, null, 20m, null, 1, default);
    var nightRow = await nightStore.CreateAsync(providerId, parentId, null, Guid.NewGuid(), "PetSitter", "PetHotel",
        date, date.AddDays(2), new(9, 0), new(10, 0), null, null, 20m, null, 1, default);
    var bookingId = night ? nightRow.NightStayBookingId : dayRow.BookingId;
    var store = night ? (object)nightStore : dayStore;
    // Seed the persisted from-state: starting/completing with OTP is SQL-only in
    // these fallbacks and is outside the payment transition under test.
    var rows = store.GetType().GetField("bookings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
    var savedRow = rows.GetType().GetProperty("Item")!.GetValue(rows, [bookingId])!;
    var statusProperty = savedRow.GetType().GetProperty("Status")!;
    Task Change(BookingStatusActor actor, Guid actorId, string target = BookingStatuses.PaymentNotReceived) => night
        ? nightStore.UpdateStatusAsync(bookingId, target, actor, actorId, "Payment not received", location, default)
        : dayStore.UpdateStatusAsync(bookingId, target, actor, actorId, "Payment not received", location, default);

    foreach (var from in BookingStatuses.All.Where(s => s != BookingStatuses.Completed && s != BookingStatuses.PaymentNotReceived))
    {
        statusProperty.SetValue(savedRow, from);
        if (BookingStatuses.Terminal.Contains(from))
            await Reject<BookingStatusTerminalException>(() => Change(BookingStatusActor.Provider, providerId), $"{night}: allowed from {from}");
        else
            await Reject<BookingNotStartableException>(() => Change(BookingStatusActor.Provider, providerId), $"{night}: allowed from {from}");
        Check((string)statusProperty.GetValue(savedRow)! == from, $"{night}: rejected transition preserved {from}");
    }
    statusProperty.SetValue(savedRow, BookingStatuses.Completed);
    await Reject<BookingStatusNotAllowedException>(() => Change(BookingStatusActor.Parent, parentId), "Parent cannot set unpaid status");
    await Reject<BookingStatusForbiddenException>(() => Change(BookingStatusActor.Provider, Guid.NewGuid()), "Other provider cannot change job");
    await Change(BookingStatusActor.Provider, providerId);
    Check((string)statusProperty.GetValue(savedRow)! == BookingStatuses.PaymentNotReceived, "Completed job transitions to unpaid");
    var history = night ? await nightStore.ListStatusHistoryAsync(bookingId, default) : await dayStore.ListStatusHistoryAsync(bookingId, default);
    Check(history.Last() is { FromStatus: BookingStatuses.Completed, ToStatus: BookingStatuses.PaymentNotReceived,
        ChangedByActor: "Provider", Note: "Payment not received" }, "Status history records provider transition");
    Check(history.Last().ChangedByActorId == providerId, "History records actor identity");
    var cashReport = savedRow.GetType().GetProperty("CashNotReceivedAtUtc")!;
    var firstReport = (DateTimeOffset?)cashReport.GetValue(savedRow);
    Check(firstReport is not null, "Cash report time is persisted");
    await Change(BookingStatusActor.Provider, providerId);
    Check((DateTimeOffset?)cashReport.GetValue(savedRow) >= firstReport, "Repeated cash report keeps the latest time");
    await Reject<BookingStatusTerminalException>(() => Change(BookingStatusActor.Provider, providerId, BookingStatuses.ProviderCancelled), "Unpaid job cannot be cancelled");

    // Exercise the actual application and endpoint paths with a recording SQL
    // boundary, so location validation and the wire response are verified.
    var writes = 0;
    object OnStoreCall(MethodInfo method, object?[] args)
    {
        if (method.Name != "UpdateStatusAsync") throw new Exception(method.Name);
        writes++;
        Check((string)args[1]! == BookingStatuses.PaymentNotReceived && (BookingStatusActor)args[2]! == BookingStatusActor.Provider,
            "Endpoint pins status and actor");
        Check((Guid)args[3]! == providerId && (CapturedLocation)args[5]! == location, "Location reaches transition SQL");
        return night ? Task.FromResult(nightRow with { Status = BookingStatuses.PaymentNotReceived })
            : Task.FromResult(dayRow with { Status = BookingStatuses.PaymentNotReceived });
    }
    var sql = night ? (object)Fake<INightStayBookingSqlStore>(OnStoreCall) : Fake<IBookingSqlStore>(OnStoreCall);
    var serviceType = typeof(IBookingService).Assembly.GetType(night
        ? "Pawfront.Application.Bookings.NightStayBookingService" : "Pawfront.Application.Bookings.BookingService")!;
    var constructor = serviceType.GetConstructors().Single();
    var constructorArgs = new object?[constructor.GetParameters().Length];
    constructorArgs[0] = sql;
    constructorArgs[^1] = Fake<IMySupportTicketLookup>((_, args) =>
    {
        Check((string)args[0]! == SupportRaisedByTypes.Provider && (Guid)args[1]! == providerId,
            "Mutation dispute lookup is scoped to the booking participant");
        return Task.FromResult(new MySupportTicketSubjects([], [new(
            night ? BookingTypes.NightStay : BookingTypes.SingleDay, bookingId)]));
    });
    var service = constructor.Invoke(constructorArgs);
    var endpointType = Assembly.Load("Pawfront.Api").GetType(night
        ? "Pawfront.Api.Endpoints.NightStayBookingEndpoints" : "Pawfront.Api.Endpoints.BookingEndpoints")!;
    var endpoint = endpointType.GetMethod("RecordCashNotReceived", BindingFlags.Static | BindingFlags.NonPublic)!;
    async Task<IResult> CallEndpoint(BookingLocationOnlyRequest? request) =>
        await (Task<IResult>)endpoint.Invoke(null, [providerId, bookingId, request, service, CancellationToken.None])!;
    Check(((IStatusCodeHttpResult)await CallEndpoint(null)).StatusCode == 400 && writes == 0, "Missing position rejects before write");
    Check(((IStatusCodeHttpResult)await CallEndpoint(new(new(91m, 8m)))).StatusCode == 400 && writes == 0, "Invalid position rejects before write");
    var result = await CallEndpoint(new(new(47m, 8m, 5m)));
    Check(((IStatusCodeHttpResult)result).StatusCode == 200 && writes == 1, "Cash-not-received endpoint succeeds");
    var response = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value);
    Check(response.GetProperty("Data").GetProperty("Status").GetString() == BookingStatuses.PaymentNotReceived, "Response contains updated booking status");
    Check(response.GetProperty("Data").GetProperty("IsDisputeRaised").GetBoolean(), "Status responses preserve disputes from either party");
    foreach (var host in new[] { "Pawfront.Api", "Pawfront.PetParentApi" })
    {
        var mapping = Assembly.Load(host).GetType(host + ".Endpoints.ReviewResponseMapping")!;
        // Review eligibility is shared with the parent's card enrichment.
        var canReview = mapping.GetMethod("IsReviewable", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((bool)canReview.Invoke(null, [BookingStatuses.PaymentNotReceived, "App"])!, "Completed unpaid job remains reviewable");
    }
}
// Every booking host must expose the same durable-limit conflict contract.
foreach (var (host, endpoint) in new[] {
    ("Pawfront.Api", "BookingEndpoints"), ("Pawfront.Api", "NightStayBookingEndpoints"),
    ("Pawfront.PetParentApi", "PetParentEndpoints"), ("Pawfront.PetParentApi", "NightStayBookingEndpoints") })
{
    var type = Assembly.Load(host).GetType(host + ".Endpoints." + endpoint)!;
    var error = new BookingModificationLimitReachedException(Guid.NewGuid());
    Check((bool)type.GetMethod("IsBookingError", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [error])!, "Modification limit is a handled booking error");
    var mapped = (IResult)type.GetMethod("MapBookingError", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [error])!;
    Check(((IStatusCodeHttpResult)mapped).StatusCode == 409, "Modification limit returns conflict");
    var json = JsonSerializer.Serialize(((IValueHttpResult)mapped).Value);
    Check(json.Contains("ModificationLimitReached"), "Modification limit returns stable mobile error code");
}
Check(BookingStatuses.Terminal.Contains(BookingStatuses.JobIncomplete), "Incomplete jobs are terminal");
if (!args.Contains("--payment-only")) checks += await BookingDisputeChecks.RunAsync();
if (args.Contains("--sql")) checks += await SqlBookingReadChecks.RunAsync();
Console.WriteLine($"Passed {checks} payment-status lifecycle, history, filter and endpoint checks.");

static T Fake<T>(Func<MethodInfo, object?[], object> callback) where T : class
{
    var proxy = DispatchProxy.Create<T, RecordingProxy>();
    ((RecordingProxy)(object)proxy).Callback = callback;
    return proxy;
}
public class RecordingProxy : DispatchProxy
{
    public Func<MethodInfo, object?[], object> Callback { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Callback(method!, args!);
}
