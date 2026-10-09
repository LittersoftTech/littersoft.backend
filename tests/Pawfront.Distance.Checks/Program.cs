using System.Reflection;
using Microsoft.AspNetCore.Http;
using Pawfront.Application.Locations;
using Pawfront.Application.Providers;
using Pawfront.Application.Services.ProviderServiceLocations;
using Pawfront.Contracts.Providers;

var checks = 0;
void Check(bool condition) { if (!condition) throw new Exception($"Check {checks + 1} failed"); checks++; }
Check(new SearchDistance(0, 0, null).To(0, 1) == 111.2);
Check(new SearchDistance(0, 0, "mi").To(0, 1) == 69.1);
Check(new SearchDistance(47, 8, "KM").To(47, 8) == 0);
Check(new SearchDistance(0, 179, "km").To(0, -179) == 222.4);
Check(new SearchDistance(0, 0, "km").To(0, 180) == 20015.1);
Check(new SearchDistance(null, null, "mi").To(0, 0) == null);
Check(new SearchDistance(0, 0, "km").To(null, 0) == null);
Check(new SearchDistance(0, 0, "km").To(double.NaN, 0) == null);
foreach (var input in new (double?, double?, string?)[] {
    (0, null, null), (null, 0, null), (91, 0, null), (0, -181, null),
    (double.NaN, 0, null), (0, double.PositiveInfinity, null), (0, 0, "miles"), (null, null, "") })
{
    try { _ = new SearchDistance(input.Item1, input.Item2, input.Item3); throw new Exception("Invalid origin accepted"); }
    catch (ArgumentException) { checks++; }
}
var assembly = Assembly.Load("Pawfront.PetParentApi");
var type = assembly.GetType("Pawfront.PetParentApi.Endpoints.ProviderSearchEndpoints")!;
foreach (var name in new[] { "SearchDayCare", "SearchNightStay", "SearchGroomers", "SearchVets", "SearchTrainers" })
{
    var handler = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
    var handlerArgs = handler.GetParameters().Select(p => p.Name == "latitude" ? (object)91d :
        p.Name == "longitude" ? 0d : p.ParameterType == typeof(CancellationToken) ? (object)CancellationToken.None : null).ToArray();
    var response = await (Task<IResult>)handler.Invoke(null, handlerArgs)!;
    Check(((IStatusCodeHttpResult)response).StatusCode == 400);
}
var events = assembly.GetType("Pawfront.PetParentApi.Endpoints.EventEndpoints")!;
var list = events.GetMethod("List", BindingFlags.NonPublic | BindingFlags.Static)!;
var eventArgs = list.GetParameters().Select(p => p.Name == "distanceUnit" ? (object)"invalid" :
    p.ParameterType == typeof(CancellationToken) ? (object)CancellationToken.None : null).ToArray();
Check(((IStatusCodeHttpResult)await (Task<IResult>)list.Invoke(null, eventArgs)!).StatusCode == 400);

var id = Guid.NewGuid();
var registry = DispatchProxy.Create<IProviderServiceLocationRegistry, LocationStub>();
var stub = (LocationStub)(object)registry;
stub.Location = new(Guid.NewGuid(), id, "PetSitter", "PetHotel", 0, 1, default, default);
var rows = new[] { new ProviderSearchResult(id, Guid.NewGuid(), "PetHotel", "Test", 0, 10, "PerHour", null, null) };
var enrich = type.GetMethod("WithDistancesAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
async Task<ProviderSearchResultResponse[]> Enrich(SearchDistance distance) =>
    await (Task<ProviderSearchResultResponse[]>)enrich.Invoke(null, [rows.Concat(rows), distance, registry, CancellationToken.None])!;
var output = await Enrich(new(0, 0, "mi"));
Check(output.Length == 2 && output.All(r => r.Distance == 69.1 && r.DistanceUnit == "mi"));
Check(stub.Reads == 1);
output = await Enrich(new(null, null, null));
Check(output.All(r => r.Distance == null && r.DistanceUnit == "km") && stub.Reads == 1);
stub.Location = null;
Check((await Enrich(new(0, 0, "km"))).All(r => r.Distance == null));
Console.WriteLine($"Passed {checks} distance and search integration checks.");
public class LocationStub : DispatchProxy
{
    public ProviderServiceLocation? Location;
    public int Reads;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name != "GetByProviderIdAsync") throw new Exception(method.Name);
        Reads++;
        return Task.FromResult(Location);
    }
}

