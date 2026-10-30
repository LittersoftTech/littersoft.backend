using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pawfront.Application.Badges;
using Pawfront.Application.Notifications;
using Pawfront.Application.ParentPets;
using Pawfront.Contracts.Badges;
using Pawfront.Contracts.Common;
using Pawfront.Contracts.ParentOnboarding;
using Pawfront.Contracts.PetParentLookup;

var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
foreach (var prefix in new[] { "004", "008", "158", "356", "756", "840", "894", "900", "981", "998" })
    Check(MicrochipId.IsValid(prefix + "123456789012"), $"Accept {prefix}");
foreach (var number in new[] { "000123456789012", "001123456789012", "899123456789012", "999123456789012", "75612345678901", "7561234567890123", "75612345678901a", "\uff1756123456789012", "75612345678901\u0661", " 756123456789012", "756123456789012 " })
{
    Check(!MicrochipId.IsValid(number), $"Reject {number}");
    try { MicrochipId.Validate(number); throw new Exception("Expected invalid chip exception"); }
    catch (MicrochipIdInvalidException) { checks++; }
}
Check(MicrochipId.Validate(null) is null && MicrochipId.Validate("") is null, "Optional chip may be absent");

// Both writes reject invalid input before attempting any database connection.
var sqlAssembly = typeof(Pawfront.Infrastructure.Sql.SqlServiceRegistration).Assembly;
var petService = (IParentPetService)Activator.CreateInstance(sqlAssembly.GetType("Pawfront.Infrastructure.Sql.ParentPets.SqlParentPetService")!, new object?[] { null, null })!;
try
{
    await petService.AddPetAsync(Guid.NewGuid(), new("Dog", "Milo", "Labrador", "Male", new(2020,1,1), 20, "999123456789012", null), default);
    throw new Exception("Create accepted invalid microchip");
}
catch (MicrochipIdInvalidException) { checks++; }
try
{
    await petService.UpdatePetAsync(Guid.NewGuid(), new("Dog", "Milo", "Labrador", "Male", new(2020,1,1), 20, "001123456789012", null), default);
    throw new Exception("Update accepted invalid microchip");
}
catch (MicrochipIdInvalidException) { checks++; }

var id = Guid.NewGuid(); var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var json = JsonSerializer.Serialize(new { petParentId = id, petId = id, participantType = "PetParent", participantId = id, type = "PetParent", id, bookingId = id }, options);
var targetTypes = typeof(IParentBadgeTarget).Assembly.GetTypes().Where(t => !t.IsInterface &&
    (typeof(IParentBadgeTarget).IsAssignableFrom(t) || typeof(IPetBadgeTarget).IsAssignableFrom(t) || typeof(IEventBookerBadgeTarget).IsAssignableFrom(t))).ToArray();
var cards = targetTypes.Select(t => JsonSerializer.Deserialize(json, t, options)!).ToArray();
var reader = new Reader(id);
using var services = new ServiceCollection().AddSingleton<IBadgeReader>(reader).BuildServiceProvider();
var http = new DefaultHttpContext { RequestServices = services };
var context = new DefaultEndpointFilterInvocationContext(http);
var filter = new BadgeResponseFilter();
var result = Results.Ok(new ApiResponse<IReadOnlyList<object>>(true, cards, null));
await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(result));
Check(reader.Calls == 1, "All nested badges resolved in one batch");
foreach (var card in cards)
{
    if (card is IParentBadgeTarget parent) Check(parent.HasIdBadge, $"{card.GetType().Name} ID badge");
    if (card is IPetBadgeTarget pet) Check(pet.HasMicrochipBadge, $"{card.GetType().Name} chip badge");
    if (card is IEventBookerBadgeTarget booking) Check(booking.HasIdBadge, $"{card.GetType().Name} booker badge");
    var wire = JsonSerializer.Serialize(card, card.GetType(), options);
    Check(!wire.Contains("badgeParentId") && !wire.Contains("badgePetId") && !wire.Contains("badgeEventBookingId"), "Lookup keys remain internal");
}
reader.Enabled = false;
await filter.InvokeAsync(context, _ => ValueTask.FromResult<object?>(result));
Check(cards.OfType<IParentBadgeTarget>().All(p => !p.HasIdBadge), "Replacement/rejection/deletion removes all ID badges live");
Check(cards.OfType<IPetBadgeTarget>().All(p => !p.HasMicrochipBadge), "Removed/invalid chip has no badge");
Check(cards.OfType<IEventBookerBadgeTarget>().All(p => !p.HasIdBadge), "PawPrints booker badge clears live");
Check(typeof(PetDetailsResponse).GetProperty("MicrochipId") is null, "Provider pet contract never exposes chip number");
foreach (var type in new[] { typeof(PetParentDetailsResponse), typeof(PetDetailsResponse) })
    Check(type.GetProperty("IdentityType") is null && type.GetProperty("IdentityPhotoUrl") is null, "Provider contracts never expose identity details");

foreach (var type in new[] { NotificationTypes.IdentityAdded, NotificationTypes.IdentityNotApproved })
{
    var rendered = NotificationRenderer.Render(type, NotificationAudience.PetParent, new Dictionary<string,string>())!;
    Check(rendered is not null && rendered.Route == NotificationRoutes.Identity, "Identity notification renders");
    var payload = NotificationPayloadBuilder.Build(new(Guid.NewGuid(), NotificationAudience.PetParent, id, type, "Identity", id, null, null, 1, DateTimeOffset.UtcNow), rendered!, DateTimeOffset.UtcNow);
    Check(payload["entityId"] == id.ToString() && payload["type"] == type, "Push and inbox retain identity entity id");
}
var identity = new PetParentIdentityResponse(id, id, "Other", "https://example.test/private", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "NotApproved", "The photo is blurry.");
var identityWire = JsonSerializer.Serialize(identity, options);
Check(identityWire.Contains("NotApproved") && identityWire.Contains("The photo is blurry."), "Owner sees review status and reason");
checks += await AdminApiChecks.RunAsync();
if (args.Contains("--sql")) checks += await SqlChecks.RunAsync();
Console.WriteLine($"Passed {checks} identity checks across {targetTypes.Length} display contracts.");

sealed class Reader(Guid id) : IBadgeReader
{
    public int Calls; public bool Enabled = true;
    public Task<BadgeSnapshot> ReadAsync(IReadOnlyCollection<Guid> parents, IReadOnlyCollection<Guid> pets, IReadOnlyCollection<Guid> bookings, CancellationToken ct)
    {
        Calls++;
        if (!parents.Contains(id) || !pets.Contains(id) || !bookings.Contains(id)) throw new Exception("Missing batch targets");
        IReadOnlySet<Guid> ids = Enabled ? new HashSet<Guid> { id } : new HashSet<Guid>();
        return Task.FromResult(new BadgeSnapshot(ids, ids, ids));
    }
}
