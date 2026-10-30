using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Pawfront.Application.Events;
using Pawfront.Contracts.Events;

internal static class EventPatchChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var hostId = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var conflictId = Guid.NewGuid();
        var day = new DateOnly(2030, 10, 12);
        var snapshot = new EventSqlSnapshot(eventId, null, hostId, "MeetupsAndSocials", true,
            "Test", "Description", null, [], "Online", day, day, new(10, 0), new(12, 0),
            false, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, new(0, 0, 0));
        var toResult = Assembly.Load("Pawfront.Application").GetType("Pawfront.Application.Events.EventService")!
            .GetMethod("ToResult", BindingFlags.Static | BindingFlags.NonPublic)!;
        var current = (EventResult)toResult.Invoke(null, [snapshot, null, null])!;
        var patch = Assembly.Load("Pawfront.PetParentApi").GetType("Pawfront.PetParentApi.Endpoints.EventEndpoints")!
            .GetMethod("Patch", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var body in new[] { "{\"startDate\":\"2030-10-13\"}", "{\"endDate\":\"2030-10-13\"}",
            "{\"startTime\":\"11:00:00\"}", "{\"endTime\":\"13:00:00\"}" })
        {
            var request = JsonSerializer.Deserialize<PatchEventRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            UpdateParentEventCommand? command = null;
            var service = DispatchProxy.Create<IEventService, PatchServiceProxy>();
            ((PatchServiceProxy)(object)service).Call = (method, args) => method.Name switch
            {
                nameof(IEventService.GetAsync) => (object)Task.FromResult<EventResult?>(current),
                nameof(IEventService.UpdateByParentAsync) => Reject((UpdateParentEventCommand)args![0]!),
                _ => throw new InvalidOperationException(method.Name)
            };
            Task<EventResult> Reject(UpdateParentEventCommand value)
            {
                command = value;
                return Task.FromException<EventResult>(new HostEventOverlapException(conflictId, "Other event"));
            }
            try
            {
                await (Task<IResult>)patch.Invoke(null, [hostId, eventId, request, service, CancellationToken.None])!;
                throw new Exception($"Expected parent PATCH to reach overlap validation: {body}");
            }
            catch (HostEventOverlapException ex)
            {
                check(ex.ConflictingEventId == conflictId && command is not null
                    && command.EventId == eventId && command.PetParentId == hostId
                    && command.StartDate == request.StartDate.Or(current.StartDate)
                    && command.EndDate == request.EndDate.Or(current.EndDate)
                    && command.StartTime == request.StartTime.Or(current.StartTime)
                    && command.EndTime == request.EndTime.Or(current.EndTime),
                    $"parent PATCH merges schedule and propagates overlap conflict: {body}");
            }
        }
    }
}

public class PatchServiceProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
}
