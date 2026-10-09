using Microsoft.AspNetCore.Http;
using System.Reflection;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Application.ProviderServices;

var assembly = Assembly.Load("Pawfront.Infrastructure.Sql");
var onboarding = (IProviderOnboardingService)Activator.CreateInstance(
    assembly.GetType("Pawfront.Infrastructure.Sql.ProviderOnboarding.InMemoryProviderOnboardingService")!, new object?[] { null })!;
var catalog = (IProviderServiceCatalog)Activator.CreateInstance(
    assembly.GetType("Pawfront.Infrastructure.Sql.ProviderServices.InMemoryProviderServiceCatalog")!)!;
var auth = await onboarding.SaveFirebaseAuthAsync(new("status-check", "EmailPassword", null,
    "status-check@example.invalid", true, null, null, null, null, null, null, null), default);
var profile = await onboarding.CompleteProviderProfileAsync(new(auth.ProviderAuthIdentityId,
    "Status", "Check", "Other", "+41", "000000000", new(1990, 1, 1)), default);
var id = profile.ProviderId;
var checks = 0;
foreach (var online in new[] { true, false })
{
    await onboarding.SetActiveStatusAsync(id, online, false, default);
    await catalog.UpsertAsync(id, "PetSitter", "PetHotel", "DayCare", default);
    await AssertStatus(online, "activating a service");
    await catalog.DeactivateAsync(id, "DayCare", default);
    if ((await catalog.ListByProviderAsync(id, false, default)).Count != 0)
        throw new Exception("Expected no active services");
    await AssertStatus(online, "deactivating the last service");
    await catalog.DeactivateAsync(id, "DayCare", default);
    await AssertStatus(online, "repeating deactivation");
    await catalog.UpsertAsync(id, "PetSitter", "PetHotel", "DayCare", default);
    await AssertStatus(online, "reactivating a service");
}
var endpoint = Assembly.Load("Pawfront.Api")
    .GetType("Pawfront.Api.Endpoints.ProviderServiceCatalogEndpoints")!
    .GetMethod("DeactivateService", BindingFlags.Static | BindingFlags.NonPublic)!;
async Task<int?> Deactivate(Guid provider, Guid service)
{
    var result = await (Task<IResult>)endpoint.Invoke(null, [provider, service, catalog, CancellationToken.None])!;
    return ((IStatusCodeHttpResult)result).StatusCode;
}
foreach (var online in new[] { true, false })
{
    await onboarding.SetActiveStatusAsync(id, online, false, default);
    var day = await catalog.UpsertAsync(id, "PetSitter", "PetHotel", "DayCare", default);
    var night = await catalog.UpsertAsync(id, "PetSitter", "PetHotel", "NightStay", default);
    if (await Deactivate(Guid.NewGuid(), day.ServiceId) != 404 ||
        !(await catalog.GetByIdAsync(day.ServiceId, default))!.IsActive)
        throw new Exception("Another provider could deactivate the service");
    checks++;
    if (await Deactivate(id, Guid.NewGuid()) != 404) throw new Exception("Missing service must return 404");
    checks++;
    if (await Deactivate(id, day.ServiceId) != 204 ||
        (await catalog.GetByIdAsync(day.ServiceId, default))!.IsActive ||
        !(await catalog.GetByIdAsync(night.ServiceId, default))!.IsActive)
        throw new Exception("Deactivation did not target only the requested service");
    checks++;
    if (await Deactivate(id, night.ServiceId) != 204 ||
        (await catalog.ListByProviderAsync(id, false, default)).Count != 0)
        throw new Exception("Last service could not be deactivated");
    checks++;
    await AssertStatus(online, "deactivating all services through the endpoint");
    var inactive = await catalog.GetByIdAsync(night.ServiceId, default);
    if (await Deactivate(id, night.ServiceId) != 204 ||
        await catalog.GetByIdAsync(night.ServiceId, default) != inactive)
        throw new Exception("Repeated deactivation changed the inactive service");
    checks++;
    if ((await catalog.ListByProviderAsync(id, true, default)).Count != 2)
        throw new Exception("Deactivated services were deleted");
    checks++;
}
Console.WriteLine($"Passed {checks} business/service status and endpoint checks.");
async Task AssertStatus(bool expected, string action)
{
    if ((await onboarding.GetProviderProfileAsync(id, default)).IsActive != expected)
        throw new Exception($"Business status changed when {action}");
    checks++;
}
