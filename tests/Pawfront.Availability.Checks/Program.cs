using Pawfront.Application.Services.PetSitter;
using Microsoft.AspNetCore.Http;
using Pawfront.Contracts.Common;
using Pawfront.Application.Providers;
using Pawfront.Application.ProviderServices;
using System.Reflection;
using Pawfront.Application.Availability;
using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Offerings;

var providerId = Guid.NewGuid();
var serviceId = Guid.NewGuid();
var monday = new DateOnly(2099, 1, 5);
while (monday.DayOfWeek != DayOfWeek.Monday) monday = monday.AddDays(1);
var days = Enumerable.Range(0, 7).Select(d => new DayAvailabilityResult(d, true,
    new TimeOnly(8, 0), new TimeOnly(18, 0), null, null)).ToList();
var closures = new Dictionary<(Guid, DateOnly), IReadOnlyList<ActiveClosure>>();
var weekly = Fake<IProviderAvailabilityService>((m, a) => Task.FromResult(new ProviderWeeklyAvailabilityResult(providerId, days)));
var timeOff = Fake<IProviderClosureReader>((m, a) => Task.FromResult(closures.GetValueOrDefault(((Guid)a[0]!, (DateOnly)a[1]!), [])));
var checks = 0;
var assembly = typeof(BookingLeadTime).Assembly;
var method = assembly.GetType("Pawfront.Application.Availability.ProviderDateAvailability")!.GetMethod("EnsureOpenAsync")!;
Task Open(DateOnly first, DateOnly last) => (Task)method.Invoke(null, [weekly, timeOff, providerId, serviceId, first, last, CancellationToken.None])!;
async Task Reject<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { checks++; return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
await Open(monday, monday.AddDays(6)); checks++;
days[6] = days[6] with { IsOpen = false };
await Reject<ProviderClosedOnDateException>(() => Open(monday, monday.AddDays(5))); // pickup Saturday
await Reject<ProviderClosedOnDateException>(() => Open(monday, monday.AddDays(6))); // intermediate Saturday
days.RemoveAt(6);
await Reject<ProviderClosedOnDateException>(() => Open(monday.AddDays(5), monday.AddDays(5)));
days.Add(new(6, true, new(8, 0), new(18, 0), null, null));
closures[(serviceId, monday.AddDays(2))] = [new(Guid.NewGuid(), null, null, "Holiday")];
await Reject<ProviderClosedOnDateException>(() => Open(monday, monday.AddDays(4)));
closures.Clear();
closures[(serviceId, monday.AddDays(4))] = [new(Guid.NewGuid(), new(9, 0), new(10, 0), "Time off")];
await Reject<ProviderClosedOnDateException>(() => Open(monday, monday.AddDays(4)));
closures.Clear();
closures[(Guid.NewGuid(), monday)] = [new(Guid.NewGuid(), null, null, "Other service")];
await Open(monday, monday); checks++;

var booking = new NightStayBookingResult(Guid.NewGuid(), providerId, Guid.NewGuid(), serviceId,
    "PetSitter", "PetHotel", monday, monday.AddDays(2), new(9, 0), new(10, 0), "ACCEPTED",
    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null);
var proposedDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
var store = Fake<INightStayBookingSqlStore>((m, a) => m.Name switch
{
    "GetAsync" => Task.FromResult<NightStayBookingResult?>(booking),
    "GetPendingModificationAsync" => Task.FromResult<NightStayBookingModificationResult?>(new(
        Guid.NewGuid(), booking.NightStayBookingId, "PetParent", booking.PetParentId,
        proposedDate, proposedDate.AddDays(2), null, DateTimeOffset.UtcNow)),
    _ => throw new Exception("Invalid modification reached SQL write: " + m.Name)
});
var nightService = (INightStayBookingService)Make("Pawfront.Application.Bookings.NightStayBookingService",
    new Dictionary<Type, object> { [typeof(INightStayBookingSqlStore)] = store,
        [typeof(IProviderAvailabilityService)] = weekly, [typeof(IProviderClosureReader)] = timeOff });
await Reject<BookingLeadTimeTooShortException>(() => nightService.RequestModificationAsync(
    new(booking.NightStayBookingId, default, booking.PetParentId, proposedDate, proposedDate.AddDays(2), null), default));
await Reject<BookingLeadTimeTooShortException>(() => nightService.RespondModificationAsync(
    new(booking.NightStayBookingId, default, providerId, true, null), default));
proposedDate = monday;
days[1] = days[1] with { IsOpen = false };
await Reject<ProviderClosedOnDateException>(() => nightService.RequestModificationAsync(
    new(booking.NightStayBookingId, default, booking.PetParentId, proposedDate, proposedDate.AddDays(2), null), default));
await Reject<ProviderClosedOnDateException>(() => nightService.RespondModificationAsync(
    new(booking.NightStayBookingId, default, providerId, true, null), default));

var offering = new OfferingResolution.Resolved(serviceId, providerId, "PetSitter", "PetHotel", "NightStay", 2, 0, false, 10);
var resolver = Fake<IProviderOfferingResolver>((m, a) => Task.FromResult<OfferingResolution>(offering));
var occupancy = Fake<INightStayOccupancyReader>((m, a) => Task.FromResult<IReadOnlyDictionary<DateOnly, int>>(new Dictionary<DateOnly, int>()));
var slots = (IProviderAvailabilitySlotService)Make("Pawfront.Application.Availability.ProviderAvailabilitySlotService",
    new Dictionary<Type, object> { [typeof(IProviderOfferingResolver)] = resolver,
        [typeof(IProviderAvailabilityService)] = weekly, [typeof(IProviderClosureReader)] = timeOff,
        [typeof(INightStayOccupancyReader)] = occupancy });
var result = await slots.GetAvailableSlotsAsync(providerId, serviceId, monday, 0, 15, null, default);
if (result.Nights!.Single().IsAvailable) throw new Exception("Closed weekday was available"); checks++;
days[1] = days[1] with { IsOpen = true };
result = await slots.GetAvailableSlotsAsync(providerId, serviceId, monday, 0, 15, null, default);
if (!result.Nights!.Single().IsAvailable) throw new Exception("Open weekday was unavailable"); checks++;
var discovery = Fake<IProviderDiscoveryService>((m, a) => Task.FromResult<IReadOnlyList<ProviderSummary>>(
    [new(providerId, "PetSitter", "PetHotel", "Hotel", null, "Zurich", null, [])]));
var catalog = Fake<IProviderServiceCatalog>((m, a) => Task.FromResult<IReadOnlyList<ProviderService>>(
    [new(serviceId, providerId, "PetSitter", "PetHotel", "NightStay", true, default, default)]));
var search = (IProviderSearchService)Make("Pawfront.Application.Providers.ProviderSearchService",
    new Dictionary<Type, object> { [typeof(IProviderDiscoveryService)] = discovery,
        [typeof(IProviderServiceCatalog)] = catalog, [typeof(IProviderOfferingResolver)] = resolver,
        [typeof(IProviderAvailabilitySlotService)] = slots,
        [typeof(IProviderAvailabilityService)] = weekly, [typeof(IProviderClosureReader)] = timeOff });
days[6] = days[6] with { IsOpen = false };
var matches = await search.SearchNightStayAsync(new(null, null, null, monday, monday.AddDays(5), 0, 20), default);
if (matches.Count != 0) throw new Exception("Search included closed pickup date"); checks++;
days[6] = days[6] with { IsOpen = true };
closures[(serviceId, monday.AddDays(5))] = [new(Guid.NewGuid(), null, null, "Pickup holiday")];
matches = await search.SearchNightStayAsync(new(null, null, null, monday, monday.AddDays(5), 0, 20), default);
if (matches.Count != 0) throw new Exception("Search included pickup time off"); checks++;

var hourlyBooking = new BookingResult(Guid.NewGuid(), providerId, booking.PetParentId, serviceId,
    "PetSitter", "PetHotel", monday, new(9, 0), new(10, 0), "ACCEPTED", default, default,
    null, null, "App", null, null, null, null, null, null, null, null, null);
var hourlyStore = Fake<IBookingSqlStore>((m, a) => m.Name switch
{
    "GetAsync" => Task.FromResult<BookingResult?>(hourlyBooking),
    "GetPendingModificationAsync" => Task.FromResult<BookingModificationResult?>(new(
        Guid.NewGuid(), hourlyBooking.BookingId, "PetParent", booking.PetParentId,
        proposedDate, new(9, 0), new(10, 0), null, DateTimeOffset.UtcNow)),
    _ => throw new Exception("Invalid hourly modification reached SQL write: " + m.Name)
});
offering = offering with { ServiceType = "DayCare", DurationHours = 1 };
var hourlyService = (IBookingService)Make("Pawfront.Application.Bookings.BookingService",
    new Dictionary<Type, object> { [typeof(IBookingSqlStore)] = hourlyStore,
        [typeof(IProviderOfferingResolver)] = resolver,
        [typeof(IProviderAvailabilityService)] = weekly, [typeof(IProviderClosureReader)] = timeOff });
proposedDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
await Reject<BookingLeadTimeTooShortException>(() => hourlyService.RequestModificationAsync(
    new(hourlyBooking.BookingId, default, booking.PetParentId, proposedDate, new(9, 0), new(10, 0), null), default));
await Reject<BookingLeadTimeTooShortException>(() => hourlyService.RespondModificationAsync(
    new(hourlyBooking.BookingId, default, providerId, true, null), default));
proposedDate = monday;
days[1] = days[1] with { IsOpen = false };
await Reject<InvalidBookingTimeException>(() => hourlyService.RespondModificationAsync(
    new(hourlyBooking.BookingId, default, providerId, true, null), default));
days[1] = days[1] with { IsOpen = true };
closures[(serviceId, monday)] = [new(Guid.NewGuid(), new(15, 0), new(16, 0), "Afternoon time off")];
await Reject<ProviderClosedOnDateException>(() => hourlyService.RespondModificationAsync(
    new(hourlyBooking.BookingId, default, providerId, true, null), default));
result = await slots.GetAvailableSlotsAsync(providerId, serviceId, monday, 1, 15, null, default);
if (result.Slots.Count != 0) throw new Exception("Search offered slots on time-off date"); checks++;
// Creation must reject the same dates as modification, before a write is attempted.
closures.Clear();
await Reject<BookingLeadTimeTooShortException>(() => hourlyService.CreateAsync(
    new(providerId, booking.PetParentId, serviceId, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), new(9, 0), new(10, 0), null), default));
days[1] = days[1] with { IsOpen = false };
await Reject<InvalidBookingTimeException>(() => hourlyService.CreateAsync(
    new(providerId, booking.PetParentId, serviceId, monday, new(9, 0), new(10, 0), null), default));
await Reject<InvalidBookingTimeException>(() => hourlyService.RequestModificationAsync(
    new(hourlyBooking.BookingId, default, booking.PetParentId, monday, new(9, 0), new(10, 0), null), default));
days[1] = days[1] with { IsOpen = true };
closures[(serviceId, monday)] = [new(Guid.NewGuid(), new(15, 0), new(16, 0), "Time off")];
await Reject<ProviderClosedOnDateException>(() => hourlyService.CreateAsync(
    new(providerId, booking.PetParentId, serviceId, monday, new(9, 0), new(10, 0), null), default));
await Reject<ProviderClosedOnDateException>(() => hourlyService.RequestModificationAsync(
    new(hourlyBooking.BookingId, default, booking.PetParentId, monday, new(9, 0), new(10, 0), null), default));
closures.Clear();
var nightCatalog = Fake<IProviderServiceCatalog>((m, a) => Task.FromResult<ProviderService?>(
    new(serviceId, providerId, "PetSitter", "PetHotel", "NightStay", true, default, default)));
var registry = Fake<IPetSitterServiceRegistry>((m, a) => Task.FromResult<PetSitterServiceResult?>(
    new(providerId, "PetHotel", "", "", "", null,
        new("", "", "", "", "", null, null, new(null, new(10, [], 1, 0, new(9, 0), new(10, 0)), [], 10, [], "", false)),
        null, default, default)));
nightService = (INightStayBookingService)Make("Pawfront.Application.Bookings.NightStayBookingService",
    new() { [typeof(INightStayBookingSqlStore)] = store, [typeof(IProviderServiceCatalog)] = nightCatalog,
        [typeof(IPetSitterServiceRegistry)] = registry,
        [typeof(IProviderAvailabilityService)] = weekly, [typeof(IProviderClosureReader)] = timeOff });
await Reject<BookingLeadTimeTooShortException>(() => nightService.CreateAsync(
    new(providerId, booking.PetParentId, serviceId, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1)), default));

foreach (var offset in new[] { 0, 1, 2 })
{
    days[(int)monday.AddDays(offset).DayOfWeek] = days[(int)monday.AddDays(offset).DayOfWeek] with { IsOpen = false };
    await Reject<ProviderClosedOnDateException>(() => nightService.CreateAsync(
        new(providerId, booking.PetParentId, serviceId, monday, monday.AddDays(2)), default));
    await Reject<ProviderClosedOnDateException>(() => nightService.RequestModificationAsync(
        new(booking.NightStayBookingId, default, booking.PetParentId, monday, monday.AddDays(2), null), default));
    await Reject<ProviderClosedOnDateException>(() => nightService.RespondModificationAsync(
        new(booking.NightStayBookingId, default, providerId, true, null), default));
    days[(int)monday.AddDays(offset).DayOfWeek] = days[(int)monday.AddDays(offset).DayOfWeek] with { IsOpen = true };
    closures[(serviceId, monday.AddDays(offset))] = [new(Guid.NewGuid(), null, null, "Holiday")];
    await Reject<ProviderClosedOnDateException>(() => nightService.CreateAsync(
        new(providerId, booking.PetParentId, serviceId, monday, monday.AddDays(2)), default));
    await Reject<ProviderClosedOnDateException>(() => nightService.RespondModificationAsync(
        new(booking.NightStayBookingId, default, providerId, true, null), default));
    closures.Clear();
}

// Persist and read the original wall-clock values; test both UTC date portions.
var infra = Assembly.Load("Pawfront.Infrastructure.Sql");
var memoryAvailability = (IProviderAvailabilityService)Activator.CreateInstance(
    infra.GetType("Pawfront.Infrastructure.Sql.Availability.InMemoryProviderAvailabilityService")!)!;
var localDays = Enumerable.Range(0, 7).Select(d => new DayAvailabilityInput(d, d == 1,
    d == 1 ? new TimeOnly(0, 0) : null, d == 1 ? new TimeOnly(8, 0) : null, null, null)).ToArray();
await memoryAvailability.SaveAsync(new(providerId, localDays, "Asia/Kolkata"), default);
var saved = await memoryAvailability.GetAsync(providerId, default);
Assert(saved.TimeZone == "Asia/Kolkata" && saved.Days[1].StartTime == TimeOnly.MinValue, "Local hours round trip");
Assert(ProviderWorkingHours.Windows(saved, monday.AddDays(-1)).Single() == (new TimeOnly(18, 30), TimeOnly.MaxValue), "India previous UTC weekday");
Assert(ProviderWorkingHours.Windows(saved, monday).Single() == (TimeOnly.MinValue, new TimeOnly(2, 30)), "India next UTC portion");
await Reject<ArgumentException>(() => memoryAvailability.SaveAsync(new(providerId, localDays, "Invalid/Zone"), default));
var allDay = localDays.Select(d => d with { IsOpen = true, StartTime = new(0, 0), EndTime = new(23, 59) }).ToArray();
await memoryAvailability.SaveAsync(new(providerId, allDay, "Europe/Zurich"), default);
saved = await memoryAvailability.GetAsync(providerId, default);
Assert(saved.Days.All(d => d.StartTime == TimeOnly.MinValue && d.EndTime == new TimeOnly(23, 59)), "All-day wall-clock round trip");
var swiss = new ProviderWeeklyAvailabilityResult(providerId,
    Enumerable.Range(0, 7).Select(d => new DayAvailabilityResult(d, true, new(0, 0), new(8, 0), null, null)).ToArray(), "Europe/Zurich");
Assert(ProviderWorkingHours.Windows(swiss, new(2099, 1, 5))[0].End == new TimeOnly(7, 0), "Zurich winter offset");
Assert(ProviderWorkingHours.Windows(swiss, new(2099, 7, 5))[0].End == new TimeOnly(6, 0), "Zurich summer offset");
var utcSchedule = swiss with { TimeZone = "UTC" };
Assert(ProviderWorkingHours.Windows(utcSchedule, monday).Single() == (TimeOnly.MinValue, new TimeOnly(8, 0)), "Legacy UTC behavior");

// Slots use the converted windows too, not just the persistence response.
await memoryAvailability.SaveAsync(new(providerId, localDays, "Asia/Kolkata"), default);
var emptyBookings = Fake<IDailyBookingReader>((m, a) => Task.FromResult<IReadOnlyList<BookingWindow>>([]));
var zonedSlots = (IProviderAvailabilitySlotService)Make("Pawfront.Application.Availability.ProviderAvailabilitySlotService",
    new() { [typeof(IProviderOfferingResolver)] = resolver, [typeof(IProviderAvailabilityService)] = memoryAvailability,
        [typeof(IProviderClosureReader)] = timeOff, [typeof(IDailyBookingReader)] = emptyBookings });
var sundaySlots = await zonedSlots.GetAvailableSlotsAsync(providerId, serviceId, monday.AddDays(-1), 1, 30, null, default);
Assert(sundaySlots.Slots.First().StartTime == new TimeOnly(18, 30), "Slots include Monday midnight on Sunday UTC");
var mondaySlots = await zonedSlots.GetAvailableSlotsAsync(providerId, serviceId, monday, 1, 30, null, default);
Assert(mondaySlots.Slots.First().StartTime == TimeOnly.MinValue && mondaySlots.Slots.Last().EndTime == new TimeOnly(2, 30), "Slots include remaining Monday hours");
var spring = swiss with { Days = swiss.Days.Select(d => d with { StartTime = new(2, 30), EndTime = new(4, 0) }).ToArray() };
Assert(ProviderWorkingHours.Windows(spring, new(2030, 3, 31)).Single() == (new TimeOnly(1, 30), new TimeOnly(2, 0)), "DST spring gap");
Assert(ProviderWorkingHours.Windows(spring, new(2030, 10, 27)).Single() == (new TimeOnly(0, 30), new TimeOnly(3, 0)), "DST fall overlap");
var breaks = saved with { TimeZone = "Asia/Kolkata", Days = localDays.Select(d => new DayAvailabilityResult(
    d.DayOfWeek, d.IsOpen, d.StartTime, d.EndTime, d.IsOpen ? new TimeOnly(5, 0) : null, d.IsOpen ? new TimeOnly(6, 0) : null)).ToArray() };
Assert(ProviderWorkingHours.Windows(breaks, monday.AddDays(-1)).Single().End == new TimeOnly(23, 30), "Break before UTC midnight");
Assert(ProviderWorkingHours.Windows(breaks, monday).Single().Start == new TimeOnly(0, 30), "Break after UTC midnight");

// The existing status route calls this store without a payment/invoice flow.
var nightMemory = Activator.CreateInstance(infra.GetType("Pawfront.Infrastructure.Sql.Bookings.InMemoryNightStayBookingStore")!);
var customStore = (IBookingSqlStore)Activator.CreateInstance(infra.GetType("Pawfront.Infrastructure.Sql.Bookings.InMemoryBookingStore")!, nightMemory)!;
async Task<BookingResult> Custom() => await customStore.CreateCustomAsync(providerId, serviceId, "PetSitter", "PetHotel",
    "Test", "+91", "1234567890", "Dog", "Pet", monday, new(9, 0), new(10, 0), "MyLocation", null, 10, null, 100, default);
foreach (var status in new[] { BookingStatuses.Completed, BookingStatuses.ProviderCancelled })
{
    var custom = await Custom();
    await Reject<BookingStatusForbiddenException>(() => customStore.UpdateStatusAsync(custom.BookingId, status, BookingStatusActor.Provider, Guid.NewGuid(), null, null, default));
    await Reject<BookingStatusForbiddenException>(() => customStore.UpdateStatusAsync(custom.BookingId, status, BookingStatusActor.Parent, providerId, null, null, default));
    var changed = await customStore.UpdateStatusAsync(custom.BookingId, status, BookingStatusActor.Provider, providerId, "Done", null, default);
    Assert(changed.Status == status, "Custom terminal status");
    Assert((await customStore.ListStatusHistoryAsync(custom.BookingId, default)).Last().ToStatus == status, "Custom audit history");
    await Reject<BookingStatusTerminalException>(() => customStore.UpdateStatusAsync(custom.BookingId, status, BookingStatusActor.Provider, providerId, null, null, default));
}
var unsupported = await Custom();
await Reject<BookingStatusNotAllowedException>(() => customStore.UpdateStatusAsync(unsupported.BookingId, BookingStatuses.Confirmed, BookingStatusActor.Provider, providerId, null, null, default));

// Both hosts map these domain failures to 400 with stable, nonempty error codes.
foreach (var host in new[] { "Pawfront.Api", "Pawfront.PetParentApi" })
foreach (var endpoint in new[] { host == "Pawfront.Api" ? "BookingEndpoints" : "PetParentEndpoints", "NightStayBookingEndpoints" })
{
    var type = Assembly.Load(host).GetType(host + ".Endpoints." + endpoint)!;
    var mapper = type.GetMethod("MapBookingError", BindingFlags.NonPublic | BindingFlags.Static)!;
    foreach (var error in new Exception[] { new ProviderClosedOnDateException(serviceId, monday, "Holiday"), new BookingLeadTimeTooShortException(DateTimeOffset.UtcNow) })
    {
        var response = mapper.Invoke(null, [error]);
        Assert(((IStatusCodeHttpResult)response!).StatusCode == 400, "HTTP 400");
        var envelope = (ApiResponse<object>)((IValueHttpResult)response!).Value!;
        Assert(envelope.Error?.Code == (error is ProviderClosedOnDateException ? "ServiceClosed" : "BookingLeadTimeTooShort"), "Stable error code");
    }
}
checks += await NightStaySearchChecks.RunAsync();
Console.WriteLine($"Passed {checks} availability, timezone, lifecycle and HTTP mapping checks.");
void Assert(bool condition, string description) { if (!condition) throw new Exception(description); checks++; }


object Make(string name, Dictionary<Type, object> dependencies)
{
    var constructor = assembly.GetType(name)!.GetConstructors().Single();
    return constructor.Invoke(constructor.GetParameters().Select(p => dependencies.GetValueOrDefault(p.ParameterType)).ToArray());
}
static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
{
    var proxy = DispatchProxy.Create<T, Stub>(); ((Stub)(object)proxy).Handler = handler; return proxy;
}
public class Stub : DispatchProxy
{
    public Func<MethodInfo, object?[], object?> Handler = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args!);
}
