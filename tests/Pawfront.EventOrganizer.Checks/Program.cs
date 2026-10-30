using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Pawfront.Application.Blocks;
using Pawfront.Application.Events;
using Pawfront.Application.Providers;
using Pawfront.Application.Services.ProviderServiceLocations;

var providerId = Guid.NewGuid();
var date = new DateOnly(2099, 1, 1);
var providerEvent = new EventSqlSnapshot(Guid.NewGuid(), providerId, null, "MeetupsAndSocials", false,
    "Event", "Description", null, [], "Online", date, date, new(10,0), new(11,0), false, null, null,
    "https://example.test/meeting", default, default, new(0,0,0), OrganizerName: "Provider");
var parentEvent = providerEvent with { EventId = Guid.NewGuid(), ProviderId = null, PetParentId = Guid.NewGuid(),
    OrganizerImageUrl = "https://example.test/parent.jpg" };
IReadOnlyList<EventSqlSnapshot> rows = [providerEvent, providerEvent with { EventId = Guid.NewGuid() }, parentEvent];
var photo = "https://example.test/provider-v1.jpg";
var lookups = 0;
var registrations = 0;
var missingRegistration = false;
var missingSummary = false;
var fail = false;
var cancel = false;
var category = "PetSitter";
var sql = Fake<IEventSqlStore>((m,a) => m.Name switch {
    "GetAsync" => Task.FromResult<EventSqlSnapshot?>(rows.SingleOrDefault(r => r.EventId == (Guid)a[0]!)),
    "CountEventsByOrganizerAsync" => Task.FromResult(3),
    "ListAsync" or "ListByProviderAsync" or "ListByPetParentAsync" or "ListTrendingAsync" => Task.FromResult(rows),
    _ => throw new Exception(m.Name)
});
var locations = Fake<IProviderServiceLocationRegistry>((m,a) => {
    registrations++;
    return Task.FromResult<ProviderServiceLocation?>(missingRegistration ? null :
        new(Guid.NewGuid(), (Guid)a[0]!, category, "Freelance", 0,0,default,default));
});
var discovery = Fake<IProviderDiscoveryService>((m,a) => {
    lookups++;
    if ((string)a[1]! != category) throw new Exception("Wrong provider category");
    if (cancel) return Task.FromCanceled<ProviderSummary?>((CancellationToken)a[2]!);
    if (fail) return Task.FromException<ProviderSummary?>(new InvalidOperationException("Unavailable profile"));
    return Task.FromResult<ProviderSummary?>(missingSummary ? null : new(providerId,category,"Freelance",null,photo,"City",null,[]));
});
var cosmos = Fake<IEventCosmosStore>((m,a) => Task.FromResult<PhysicalEventResult?>(null));
var current = Fake<ICurrentBlockParty>((m,a) => Task.FromResult<BlockParty?>(null));
var type = typeof(IEventService).Assembly.GetType("Pawfront.Application.Events.EventService")!;
var logger = typeof(NullLogger<>).MakeGenericType(type).GetField("Instance")!.GetValue(null)!;
var service = (IEventService)Activator.CreateInstance(type, sql, cosmos, locations, discovery, current, logger)!;
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
var filter = new EventListFilter(null,null,null,null,null,null);
var listed = await service.ListAsync(filter, default);
Check(listed.Where(e => e.Organizer.Type == "Provider").All(e => e.Organizer.ImageUrl == photo), "Provider photos missing from catalog");
Check(listed.Single(e => e.Organizer.Type == "PetParent").Organizer.ImageUrl == parentEvent.OrganizerImageUrl, "Parent photo changed");
Check(lookups == 1 && registrations == 1, "Duplicate host must be looked up once per list");
photo = "https://example.test/provider-v2.jpg";
Check((await service.GetAsync(providerEvent.EventId, default))!.Organizer.ImageUrl == photo, "Detail retained old profile photo");
Check((await service.ListAsync(filter, default)).First().Organizer.ImageUrl == photo, "List retained old profile photo");
Check((await service.GetAsync(providerEvent.EventId, default))!.Organizer.TotalEventsOrganized == 3, "Detail count changed");
foreach (var hostCategory in new[] { "PetSitter", "PetGroomer", "PetTrainer", "PetAdoptionAndSale", "Vet" }) {
    category = hostCategory;
    Check((await service.GetAsync(providerEvent.EventId, default))!.Organizer.ImageUrl == photo, "Category photo missing: " + category);
}
foreach (var list in new Func<Task<IReadOnlyCollection<EventResult>>>[] {
    () => service.ListByProviderAsync(providerId, default),
    () => service.ListByPetParentAsync(parentEvent.PetParentId!.Value, default),
    () => service.ListTrendingAsync(10, default) })
    Check((await list()).Where(e => e.Organizer.Type == "Provider").All(e => e.Organizer.ImageUrl == photo), "Alternate list photo missing");
rows = [providerEvent with { EventType = "Physical" }];
Check((await service.ListAsync(filter, default)).Single().Organizer.ImageUrl == photo, "Physical event photo missing");
photo = null;
Check((await service.GetAsync(providerEvent.EventId, default))!.Organizer.ImageUrl is null, "Removed profile photo retained");
missingSummary = true;
Check((await service.ListAsync(filter, default)).Single().Organizer.ImageUrl is null, "Missing profile must return null photo");
missingSummary = false; missingRegistration = true;
Check((await service.GetAsync(providerEvent.EventId, default))!.Organizer.ImageUrl is null, "Unregistered provider must return null photo");
missingRegistration = false; fail = true;
Check((await service.ListAsync(filter, default)).Count == 1, "Profile failure must not remove event");
fail = false; cancel = true;
using var cts = new CancellationTokenSource(); cts.Cancel();
try { await service.GetAsync(providerEvent.EventId, cts.Token); throw new Exception("Cancellation was swallowed"); }
catch (OperationCanceledException) { checks++; }
Console.WriteLine($"Passed {checks} live event organizer photo checks.");

static T Fake<T>(Func<MethodInfo, object?[], object> callback) where T : class {
    var proxy = DispatchProxy.Create<T, RecordingProxy>();
    ((RecordingProxy)(object)proxy).Callback = callback; return proxy;
}
public class RecordingProxy : DispatchProxy {
    public Func<MethodInfo, object?[], object> Callback { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Callback(method!, args!);
}

