using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Pawfront.Application.Blocks;
using Pawfront.Application.Bookings;
using Pawfront.Application.Support;

internal static class BookingDisputeChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++;
        }
        var provider = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var memoryType = Assembly.Load("Pawfront.Infrastructure.Sql")
            .GetType("Pawfront.Infrastructure.Sql.Support.InMemorySupportTicketStore")!;

        foreach (var night in new[] { false, true })
        foreach (var reporter in new[] { SupportRaisedByTypes.Provider, SupportRaisedByTypes.PetParent })
        foreach (var closed in new[] { false, true })
        {
            var store = (ISupportTicketStore)Activator.CreateInstance(memoryType)!;
            var kind = night ? BookingTypes.NightStay : BookingTypes.SingleDay;
            var ticket = (await store.CreateAsync(new(SupportTicketTypes.BookingIncident, reporter,
                reporter == SupportRaisedByTypes.Provider ? provider : parent, kind, bookingId,
                null, "Incident", null, null), default)).Ticket;
            // The fallback cannot resolve counterparties; seed the SQL-shaped party pair.
            var tickets = (ConcurrentDictionary<Guid, SupportTicketRecord>)memoryType
                .GetField("tickets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
            tickets[ticket.TicketId] = ticket with
            {
                ProviderId = provider, PetParentId = parent,
                Status = closed ? SupportTicketStatuses.Closed : SupportTicketStatuses.Opened,
                ClosedAtUtc = closed ? DateTimeOffset.UtcNow : null
            };
            var lookup = (IMySupportTicketLookup)store;
            foreach (var actor in new[] { SupportRaisedByTypes.Provider, SupportRaisedByTypes.PetParent })
            {
                var actorId = actor == SupportRaisedByTypes.Provider ? provider : parent;
                var subjects = await lookup.GetAsync(actor, actorId, default);
                Check(subjects.IsDisputeRaisedForBooking(kind, bookingId), "Either party sees the dispute, including after closure");
                Check((subjects.ForBooking(kind, bookingId) is not null) == (actor == reporter && !closed),
                    "Own open-ticket references retain their meaning");
                Check(!subjects.IsDisputeRaisedForBooking(night ? BookingTypes.SingleDay : BookingTypes.NightStay, bookingId),
                    "Day and night booking identifiers are isolated");
                Check(!(await lookup.GetAsync(actor, Guid.NewGuid(), default)).IsDisputeRaisedForBooking(kind, bookingId),
                    "Unrelated users cannot see the dispute");

                var host = actor == SupportRaisedByTypes.Provider ? "Pawfront.Api" : "Pawfront.PetParentApi";
                var endpoints = Assembly.Load(host).GetType(host + ".Endpoints." +
                    (night ? "NightStayBookingEndpoints" : actor == SupportRaisedByTypes.Provider ? "BookingEndpoints" : "PetParentEndpoints"))!;
                var rowValues = new Dictionary<string, object?>
                {
                    ["BookingId"] = bookingId, ["NightStayBookingId"] = bookingId,
                    ["ProviderId"] = provider, ["PetParentId"] = parent, ["Source"] = "App",
                    ["Status"] = BookingStatuses.PaymentNotReceived, ["ServiceCategory"] = "PetSitter", ["SubCategory"] = "PetHotel"
                };
                var row = Make(night ? typeof(NightStayBookingResult) : typeof(BookingResult), rowValues);
                var location = new BookingLocationResult(null, null, null, null, null, null);
                var detailRow = Make(night ? typeof(NightStayBookingDetailRow) : typeof(BookingDetailRow), rowValues);
                var detail = Make(night ? typeof(NightStayBookingDetailResult) : typeof(BookingDetailResult),
                    new() { ["Row"] = detailRow, ["JobId"] = "PF-000001", ["Location"] = location });
                var detailMethod = endpoints.GetMethod(night ? "ToDetailResponse" : "ToBookingDetailResponse",
                    BindingFlags.Static | BindingFlags.NonPublic)!;
                var mapped = detailMethod.Invoke(null, Arguments(detailMethod,
                    new() { ["detail"] = detail, ["isDisputeRaised"] = true, ["myTicket"] = subjects.ForBooking(kind, bookingId) }));
                var json = JsonSerializer.SerializeToElement(mapped, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                Check(json.GetProperty("isDisputeRaised").GetBoolean(), "Both hosts' details expose dispute flag");
                Check(json.GetProperty("bookingDetails").GetProperty("status").GetString() == BookingStatuses.PaymentNotReceived,
                    "Dispute display retains payment lifecycle status");

                var service = night
                    ? (object)Fake<INightStayBookingService>((method, _) => method.Name == "ListByProviderAsync"
                        ? Task.FromResult<IReadOnlyList<NightStayBookingResult>>([(NightStayBookingResult)row])
                        : Task.FromResult<IReadOnlyList<NightStayBookingListItemResult>>([new((NightStayBookingResult)row, 20m, null, location)]))
                    : Fake<IBookingService>((method, _) => method.Name == "ListByProviderAsync"
                        ? Task.FromResult<IReadOnlyList<BookingResult>>([(BookingResult)row])
                        : Task.FromResult<IReadOnlyList<BookingListItemResult>>([new((BookingResult)row, null, location)]));
                var enrichment = Fake<IParentBookingEnrichmentService>((_, _) => night
                    ? Task.FromResult<IReadOnlyList<EnrichedNightStayBookingCard>>([new((NightStayBookingResult)row, null, 20m, null, location)])
                    : Task.FromResult<IReadOnlyList<EnrichedBookingCard>>([new((BookingResult)row, null, "PetHotel", 20m, null, location)]));
                var listMethod = endpoints.GetMethod(actor == SupportRaisedByTypes.Provider ? "ListByProvider"
                    : night ? "ListByPetParent" : "ListServiceBookings", BindingFlags.Static | BindingFlags.NonPublic)!;
                var result = await (Task<IResult>)listMethod.Invoke(null, Arguments(listMethod, new()
                {
                    ["providerId"] = provider, ["petParentId"] = parent, ["bookingService"] = service,
                    ["ticketLookup"] = lookup, ["enrichment"] = enrichment,
                    ["blockLookup"] = Fake<IMyBlockLookup>((_, _) => Task.FromResult(MyBlockedCounterparties.Empty))
                }))!;
                var listJson = JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("data")[0];
                if (actor == SupportRaisedByTypes.PetParent) listJson = listJson.GetProperty("booking");
                Check(listJson.GetProperty("isDisputeRaised").GetBoolean(), "Both hosts' lists expose either party's dispute");
                Check(listJson.GetProperty("isTicketRaisedByMe").GetBoolean() == (actor == reporter && !closed),
                    "Counterparty ticket does not become the caller's ticket");
            }
            Check(!(await lookup.GetAsync("Unknown", parent, default)).IsDisputeRaisedForBooking(kind, bookingId),
                "Invalid actor cannot access markers");
        }
        Check(!MySupportTicketSubjects.Empty.IsDisputeRaisedForBooking(BookingTypes.SingleDay, bookingId), "Unreported booking is false");
        return checks;
    }

    private static object Make(Type type, Dictionary<string, object?> values)
    {
        var ctor = type.GetConstructors().Single();
        return ctor.Invoke(ctor.GetParameters().Select(p => Value(p, values)).ToArray());
    }
    private static object?[] Arguments(MethodInfo method, Dictionary<string, object?> values) =>
        method.GetParameters().Select(p => Value(p, values)).ToArray();
    private static object? Value(ParameterInfo p, Dictionary<string, object?> values) =>
        values.TryGetValue(p.Name!, out var value) ? value : p.HasDefaultValue ? p.DefaultValue
            : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null;
    private static T Fake<T>(Func<MethodInfo, object?[], object> callback) where T : class
    {
        var proxy = DispatchProxy.Create<T, RecordingProxy>();
        ((RecordingProxy)(object)proxy).Callback = callback;
        return proxy;
    }
}
