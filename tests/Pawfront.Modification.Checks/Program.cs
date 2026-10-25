using System.Reflection;
using Pawfront.Application.Availability;
using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.ProviderServices;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Support;
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
var today = DateOnly.FromDateTime(DateTime.UtcNow);
await nightService.RequestModificationAsync(new(booking.NightStayBookingId,
    BookingStatusActor.Provider, providerId, today, today.AddDays(1), null), default);
try
{
    await nightService.RequestModificationAsync(new(booking.NightStayBookingId,
        BookingStatusActor.Provider, providerId, today.AddDays(-1), today, null), default);
    throw new Exception("Past night-stay check-in was accepted");
}
catch (InvalidNightStayDatesException) { }
var dateDeadline = BookingAcceptanceDeadline.ComputeNightStay(DateTimeOffset.UtcNow, today);
if (dateDeadline != new DateTimeOffset(today.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
    throw new Exception("Night-stay acceptance deadline does not last through check-in day");
foreach (var fixedTimes in new[] { true, false })
{
    TimeOnly? savedDropOff = fixedTimes ? TimeOnly.MinValue : null;
    TimeOnly? savedPickUp = fixedTimes ? new TimeOnly(10, 0) : null;
    var offering = new PetSitterOfferingResult(null, new(20m, [], 1, 0m,
        savedDropOff, savedPickUp, fixedTimes), [], 2, [], "ProviderLocation", false);
    dependencies[typeof(IPetSitterServiceRegistry)] = Fake<IPetSitterServiceRegistry>((_, _) =>
        Task.FromResult<PetSitterServiceResult?>(new(providerId, "PetHotel", "Address", "1234", "City", null,
            new("Hotel", "+41", "123", "test@example.invalid", "Hotel", null, null, offering), null, default, default)));
    dependencies[typeof(IProviderServiceCatalog)] = Fake<IProviderServiceCatalog>((_, _) =>
        Task.FromResult<ProviderService?>(new(serviceId, providerId, "PetSitter", "PetHotel", "NightStay", true, default, default)));
    var creationCaptured = false;
    dependencies[typeof(INightStayBookingSqlStore)] = Fake<INightStayBookingSqlStore>((m, args) => m.Name switch
    {
        "CreateAsync" => CaptureCreation(args),
        "GetAsync" => Task.FromResult<NightStayBookingResult?>(booking),
        "GetPendingModificationAsync" => Task.FromResult<NightStayBookingModificationResult?>(new(
            Guid.NewGuid(), booking.NightStayBookingId, "Provider", providerId, today, today.AddDays(1), null, DateTimeOffset.UtcNow)),
        "RespondModificationAsync" => Task.FromResult(booking),
        _ => throw new Exception(m.Name)
    });
    Task<NightStayBookingResult> CaptureCreation(object?[] args)
    {
        creationCaptured = true;
        if ((TimeOnly?)args[8] != savedDropOff || (TimeOnly?)args[9] != savedPickUp)
            throw new Exception("Creation failed to preserve provider times or nullable handover times");
        return Task.FromResult(booking);
    }
    var sameDayService = (INightStayBookingService)Make("Pawfront.Application.Bookings.NightStayBookingService", dependencies);
    await sameDayService.CreateAsync(new(providerId, booking.PetParentId, serviceId, today,
        today.AddDays(1)), default);
    if (!creationCaptured) throw new Exception("Date-only stay was not created");
    if (fixedTimes)
        await sameDayService.CreateAsync(new(providerId, booking.PetParentId, serviceId, today,
            today.AddDays(1), DropOffTime: new(14, 0), PickUpTime: new(15, 0)), default);
    await sameDayService.RespondModificationAsync(new(booking.NightStayBookingId,
        BookingStatusActor.Parent, booking.PetParentId, true, null), default);
}
// Chat reason choices apply to chat alone; booking comments stay required.
var normalise = assembly.GetType("Pawfront.Application.Support.SupportTicketService")!
    .GetMethod("Normalise", BindingFlags.NonPublic | BindingFlags.Static)!;
CreateSupportTicketCommand ticket = new(SupportTicketTypes.BookingIncident,
    SupportRaisedByTypes.PetParent, booking.PetParentId, "NightStay", booking.NightStayBookingId,
    null, "My booking problem", "Payment issue", "My own description");
CreateSupportTicketCommand Normalise(CreateSupportTicketCommand value) =>
    (CreateSupportTicketCommand)normalise.Invoke(null, [value])!;
void InvalidTicket(CreateSupportTicketCommand value)
{
    try { Normalise(value); throw new Exception("Invalid ticket accepted"); }
    catch (TargetInvocationException e) when (e.InnerException is ArgumentException) { }
}
Normalise(ticket);
Normalise(ticket with { RaisedByType = SupportRaisedByTypes.Provider });
Normalise(ticket with { Category = null, Reason = null });
InvalidTicket(ticket with { Comment = null });
var chat = ticket with { TicketType = SupportTicketTypes.ChatIncident, ConversationId = Guid.NewGuid() };
InvalidTicket(chat);
InvalidTicket(chat with { Reason = ReportUserReasons.SomethingElse, Comment = null });
Normalise(chat with { Reason = ReportUserReasons.SomethingElse });
Normalise(chat with { Reason = ReportUserReasons.All[0], Comment = null });
foreach (var night in new[] { false, true })
{
    var cancelled = false;
    if (night)
        dependencies[typeof(INightStayBookingSqlStore)] = Fake<INightStayBookingSqlStore>((m, a) =>
        {
            if (m.Name != "RespondModificationAsync") throw new Exception(m.Name);
            cancelled = (bool)a[^1]!;
            return Task.FromResult(booking);
        });
    else
        dependencies[typeof(IBookingSqlStore)] = Fake<IBookingSqlStore>((m, a) =>
        {
            if (m.Name != "RespondModificationAsync") throw new Exception(m.Name);
            cancelled = (bool)a[^1]!;
            return Task.FromResult(hourlyBooking);
        });
    var service = Make(night ? "Pawfront.Application.Bookings.NightStayBookingService" : "Pawfront.Application.Bookings.BookingService", dependencies);
    Task Respond(BookingStatusActor actor, bool accept) => night
        ? ((INightStayBookingService)service).RespondModificationAsync(new(booking.NightStayBookingId, actor, booking.PetParentId, accept, null, true), default)
        : ((IBookingService)service).RespondModificationAsync(new(hourlyBooking.BookingId, actor, booking.PetParentId, accept, null, true), default);
    await Respond(BookingStatusActor.Parent, false);
    if (!cancelled) throw new Exception("Decline-and-cancel was not forwarded atomically to SQL");
    foreach (var (actor, accept) in new[] { (BookingStatusActor.Parent, true), (BookingStatusActor.Provider, false) })
    {
        try { await Respond(actor, accept); throw new Exception("Invalid decline-and-cancel accepted"); }
        catch (ArgumentException) { }
    }
}
Console.WriteLine("Passed provider and parent modification checks and date-only night-stay regressions.");
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
