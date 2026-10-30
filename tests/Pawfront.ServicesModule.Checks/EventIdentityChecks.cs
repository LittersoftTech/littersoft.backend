using System.Reflection;
using Pawfront.Application.Blocks;
using Pawfront.Application.Events;

static class EventIdentityChecks
{
    public static async Task Run(Action<bool,string> check)
    {
        foreach (var host in new[] { "Pawfront.Api", "Pawfront.PetParentApi" })
        {
            var assembly = Assembly.Load(host);
            var contextType = assembly.GetType(host + ".Auth." + (host == "Pawfront.Api" ? "ICurrentProviderContext" : "ICurrentPetParentContext"))!;
            var account = Guid.NewGuid();
            var context = DispatchProxy.Create(contextType, typeof(HttpChecks.EndpointProxy));
            ((HttpChecks.EndpointProxy)context).Call = (_,_) => Task.FromResult<Guid?>(account);
            var party = (ICurrentBlockParty)Activator.CreateInstance(assembly.GetType(host + ".Auth.CurrentBlockParty")!, context)!;
            var expectedType = host == "Pawfront.Api" ? BlockPartyType.Provider : BlockPartyType.PetParent;
            var expected = new BlockParty(expectedType, account);
            check(await party.GetAsync(default) == expected, $"{host} resolves its own account and role");

            var events = DispatchProxy.Create<IEventSqlStore,HttpChecks.EndpointProxy>();
            ((HttpChecks.EndpointProxy)events).Call = (method, values) =>
            {
                check(method.Name == "IncrementCounterAsync" && (BlockParty?)values![3] == expected,
                    $"{host} forwards account identity when recording a view");
                return Task.FromResult(new EventCounters(1,0,0));
            };
            var service = (IEventService)Activator.CreateInstance(typeof(IEventService).Assembly.GetType("Pawfront.Application.Events.EventService")!,
                new object?[] { events,null,null,null,party,null })!;
            await service.IncrementCounterAsync(Guid.NewGuid(),"View",default);

            var bookings = DispatchProxy.Create<IEventBookingSqlStore,HttpChecks.EndpointProxy>();
            ((HttpChecks.EndpointProxy)bookings).Call = (method, values) =>
            {
                check((BlockParty?)values![^1] == expected, $"{host} attendance lookup scopes the shared email to its own account");
                return method.Name == "ListByBookerEmailAsync"
                    ? (object)Task.FromResult<IReadOnlyList<EventBookingSummary>>([])
                    : Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
            };
            var bookingService = (IEventBookingService)Activator.CreateInstance(typeof(IEventBookingService).Assembly.GetType("Pawfront.Application.Events.EventBookingService")!,
                new object?[] { null,bookings,null,party,null,null })!;
            await bookingService.ListByBookerEmailAsync("shared@example.invalid",default);
            await bookingService.ListBookedEventIdsByBookerEmailAsync("shared@example.invalid",default);
        }
    }
}
