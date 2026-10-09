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
var booking = new NightStayBookingResult(Guid.NewGuid(), providerId, Guid.NewGuid(), serviceId,
    "PetSitter", "PetHotel", monday, monday.AddDays(2), new(9, 0), new(10, 0), "ACCEPTED",
    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null);
var hourlyBooking = new BookingResult(Guid.NewGuid(), providerId, booking.PetParentId, serviceId,
    "PetSitter", "PetHotel", monday, new(9, 0), new(10, 0), "ACCEPTED", default, default,
    null, null, "App", null, null, null, null, null, null, null, null, null);

var assembly = typeof(BookingLeadTime).Assembly;
var currentTerms = new BookingAcknowledgedTerms(20m, 48, new(11, 0), new(12, 0), "New address", "City", "1234", null, null);
var reads = 0;
var writes = 0;
BookingAcknowledgedTerms? staged = null;
var drifted = true;
var terms = Fake<IBookingTermsChangeService>((m, a) => {
    reads++;
    return Task.FromResult(new BookingTermsChangeResult((Guid)a[0]!, drifted, [], currentTerms));
});
var resolver = Fake<IProviderOfferingResolver>((m, a) => Task.FromResult<OfferingResolution>(
    new OfferingResolution.Resolved(serviceId, providerId, "PetSitter", "PetHotel", "DayCare", 2, 1, false, 20)));
var hourlyStore = Fake<IBookingSqlStore>((m, a) => {
    if (m.Name == "GetAsync") return Task.FromResult<BookingResult?>(hourlyBooking);
    if (m.Name != "RequestModificationAsync") throw new Exception(m.Name);
    writes++; staged = (BookingAcknowledgedTerms?)a[7]; return Task.FromResult(hourlyBooking);
});
var nightStore = Fake<INightStayBookingSqlStore>((m, a) => {
    if (m.Name == "GetAsync") return Task.FromResult<NightStayBookingResult?>(booking);
    if (m.Name != "RequestModificationAsync") throw new Exception(m.Name);
    writes++; staged = (BookingAcknowledgedTerms?)a[6]; return Task.FromResult(booking);
});
var dependencies = new Dictionary<Type, object> {
    [typeof(IBookingSqlStore)] = hourlyStore, [typeof(INightStayBookingSqlStore)] = nightStore,
    [typeof(IProviderOfferingResolver)] = resolver, [typeof(IProviderAvailabilityService)] = weekly,
    [typeof(IProviderClosureReader)] = timeOff, [typeof(IBookingTermsChangeService)] = terms
};
var hourlyService = (IBookingService)Make("Pawfront.Application.Bookings.BookingService", dependencies);
var nightService = (INightStayBookingService)Make("Pawfront.Application.Bookings.NightStayBookingService", dependencies);
foreach (var night in new[] { false, true })
{
    Task Request(BookingStatusActor actor, bool acknowledge) => night
        ? nightService.RequestModificationAsync(new(booking.NightStayBookingId, actor, providerId, monday, monday.AddDays(2), null, acknowledge), default)
        : hourlyService.RequestModificationAsync(new(hourlyBooking.BookingId, actor, providerId, monday, new(9, 0), new(10, 0), null, acknowledge), default);
    foreach (var acknowledge in new[] { false, true })
    {
        var before = reads;
        await Request(BookingStatusActor.Provider, acknowledge);
        if (reads != before || staged != null) throw new Exception("Provider proposal looked up or staged live terms");
    }
    var beforeWrites = writes;
    try { await Request(BookingStatusActor.Parent, false); throw new Exception("Parent drift was not rejected"); }
    catch (BookingTermsChangedException) { }
    if (writes != beforeWrites) throw new Exception("Rejected parent proposal was written");
    await Request(BookingStatusActor.Parent, true);
    if (staged != currentTerms) throw new Exception("Parent acknowledged terms were not staged");
    drifted = false;
    await Request(BookingStatusActor.Parent, false);
    if (staged != null) throw new Exception("Unchanged parent terms were staged");
    drifted = true;
}
Console.WriteLine("Passed provider and parent modification checks for single-day and night-stay bookings.");
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
