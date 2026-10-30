using System.Reflection;
using Pawfront.Application.Availability;
using Pawfront.Application.Bookings;
using Pawfront.Application.Closures;
using Pawfront.Application.Offerings;
using Pawfront.Application.ProviderBanners;
using Pawfront.Application.ProviderServiceBanners;
using Pawfront.Application.ProviderServices;
using Pawfront.Application.Providers;

internal static class NightStaySearchChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception(description);
            checks++;
        }
        var provider = Guid.NewGuid();
        var service = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var days = Enumerable.Range(0, 7).Select(d => new DayAvailabilityResult(d, true,
            new(8, 0), new(9, 0), null, null)).ToArray();
        var closures = new Dictionary<DateOnly, IReadOnlyList<ActiveClosure>>();
        var occupancy = new Dictionary<DateOnly, int>();
        var offering = new OfferingResolution.Resolved(service, provider, "PetSitter", "PetHotel", "NightStay", 2, 0, false, 10);
        var dependencies = new Dictionary<Type, object>
        {
            [typeof(IProviderOfferingResolver)] = Fake<IProviderOfferingResolver>((_, _) => Task.FromResult<OfferingResolution>(offering)),
            [typeof(IProviderAvailabilityService)] = Fake<IProviderAvailabilityService>((_, _) =>
                Task.FromResult(new ProviderWeeklyAvailabilityResult(provider, days))),
            [typeof(IProviderClosureReader)] = Fake<IProviderClosureReader>((_, args) =>
                Task.FromResult(closures.GetValueOrDefault((DateOnly)args[1]!, []))),
            [typeof(INightStayOccupancyReader)] = Fake<INightStayOccupancyReader>((_, _) =>
                Task.FromResult<IReadOnlyDictionary<DateOnly, int>>(occupancy)),
            [typeof(IDailyAgendaReader)] = Fake<IDailyAgendaReader>((_, args) =>
                Task.FromResult<IReadOnlyList<AgendaBookingRow>>(Enumerable.Range(0,
                    occupancy.GetValueOrDefault((DateOnly)args[1]!)).Select(i => new AgendaBookingRow(
                        "NightStay", Guid.NewGuid(), i + 1, Guid.NewGuid(), TimeOnly.MinValue, new(23, 59, 59), "CONFIRMED")).ToArray())),
            [typeof(IProviderDiscoveryService)] = Fake<IProviderDiscoveryService>((_, _) =>
                Task.FromResult<IReadOnlyList<ProviderSummary>>([new(provider, "PetSitter", "PetHotel", "Hotel", null, "Zurich", null, [])])),
            [typeof(IProviderServiceCatalog)] = Fake<IProviderServiceCatalog>((_, _) =>
                Task.FromResult<IReadOnlyList<ProviderService>>([new(service, provider, "PetSitter", "PetHotel", "NightStay", true, default, default)])),
            [typeof(IProviderBookingStatsReader)] = Fake<IProviderBookingStatsReader>((_, _) =>
                Task.FromResult<IReadOnlyDictionary<Guid, int>>(new Dictionary<Guid, int>())),
            [typeof(IProviderNameReader)] = Fake<IProviderNameReader>((_, _) =>
                Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>())),
            [typeof(IProviderServiceBannerService)] = Fake<IProviderServiceBannerService>((_, _) =>
                Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>())),
            [typeof(IProviderBannerImageService)] = Fake<IProviderBannerImageService>((_, _) =>
                Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>()))
        };
        var slots = (IProviderAvailabilitySlotService)Make("Availability.ProviderAvailabilitySlotService", dependencies);
        dependencies[typeof(IProviderAvailabilitySlotService)] = slots;
        var search = (IProviderSearchService)Make("Providers.ProviderSearchService", dependencies);
        var agenda = (IProviderDailyAgendaService)Make("Availability.ProviderDailyAgendaService", dependencies);
        Task<AvailableSlotsResult> Nights(DateOnly date) => slots.GetAvailableSlotsAsync(provider, service, date, 0, 15, null, default);
        Task<IReadOnlyList<ProviderSearchResult>> Search(DateOnly start, DateOnly pickup) =>
            search.SearchNightStayAsync(new(null, null, null, start, pickup, 0, 20), default);

        foreach (var dropOff in new TimeOnly?[] { null, TimeOnly.MinValue, TimeOnly.MaxValue })
        {
            offering = offering with { DropOffTime = dropOff };
            var available = (await Nights(today)).Nights!.Single();
            var expected=!BookingLeadTime.IsTooSoon(today,dropOff??TimeOnly.MinValue,DateTimeOffset.UtcNow);
            Check(available.IsAvailable==expected && available.RemainingCapacity == 2,
                "Night availability enforces three hours before check-in");
            Check((await Search(today, today.AddDays(1))).Count==(expected?1:0),
                "Parent search agrees with check-in lead time");
            Check((await agenda.GetDailyAgendaAsync(provider, service, today, null, default)).Entries.Single().IsBookable==expected,
                "Parent night calendar agrees with check-in lead time");
        }
        offering=offering with { DropOffTime=TimeOnly.MaxValue };
        Check(!(await Nights(today.AddDays(-1))).Nights!.Single().IsAvailable, "Past nights remain unavailable");
        Check((await Search(today.AddDays(-1), today)).Count == 0, "Search excludes a past start date");
        Check(!(await agenda.GetDailyAgendaAsync(provider, service, today.AddDays(-1), null, default)).Entries.Single().IsBookable,
            "Past calendar dates remain unavailable");
        Check((await Search(today.AddDays(1), today.AddDays(3))).Count == 1, "Future stay dates remain searchable");

        days[(int)today.DayOfWeek] = days[(int)today.DayOfWeek] with { IsOpen = false };
        Check((await Search(today, today.AddDays(1))).Count == 0, "Closed weekdays still exclude providers");
        days[(int)today.DayOfWeek] = days[(int)today.DayOfWeek] with { IsOpen = true };
        closures[today.AddDays(1)] = [new(Guid.NewGuid(), null, null, "Pickup day closed")];
        Check((await Search(today, today.AddDays(1))).Count == 0, "Pickup date closures still exclude providers");
        closures.Clear();

        occupancy[today] = 2;
        Check((await Search(today, today.AddDays(1))).Count == 0, "Full nights still exclude providers");
        Check((await Nights(today)).Nights!.Single() is { IsAvailable: false, RemainingCapacity: 0 }, "Night capacity remains accurate");
        Check(!(await agenda.GetDailyAgendaAsync(provider, service, today, null, default)).Entries.Single().IsBookable,
            "Full night remains unavailable in calendar");
        occupancy.Clear();
        occupancy[today.AddDays(1)] = 2;
        Check((await Search(today, today.AddDays(2))).Count == 0, "Every stayed night must have capacity");
        Check((await Search(today, today.AddDays(1))).Count == 1, "Pickup date is not counted as a stayed night");
        return checks;
    }

    private static object Make(string name, Dictionary<Type, object> dependencies)
    {
        var constructor = typeof(BookingLeadTime).Assembly.GetType("Pawfront.Application." + name)!.GetConstructors().Single();
        return constructor.Invoke(constructor.GetParameters().Select(p => dependencies.GetValueOrDefault(p.ParameterType)).ToArray());
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub>();
        ((Stub)(object)proxy).Handler = handler;
        return proxy;
    }
}
