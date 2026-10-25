using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Pawfront.Application.Bookings;
using Pawfront.Application.Services.PetSitter;
using Pawfront.Application.Services.PetGroomer;
using Pawfront.Application.Services.PetTrainer;
using Pawfront.Application.Services.Vet;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
    Console.WriteLine($"PASS {name}");
}
var cosmos = Assembly.Load("Pawfront.Infrastructure.Cosmos");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { RespectNullableAnnotations = true };
foreach (var category in new[] { "PetSitter", "PetGroomer", "PetTrainer" })
{
    var requestTypes = typeof(Pawfront.Contracts.Services.PetTrainer.SaveTrainingSchoolOfferingRequest).Assembly.GetTypes()
        .Where(t => t.Namespace == $"Pawfront.Contracts.Services.{category}" && t.Name.StartsWith("Save") && t.Name.EndsWith("OfferingRequest")).ToArray();
    Check(requestTypes.Length == 2, $"{category} has both offering request shapes");
    foreach (var type in requestTypes)
    {
        // Missing license properties must deserialize even when nullability is enforced.
        var requestJson = category switch
        {
            "PetSitter" => """{"animalsHandled":[],"dogTemperaments":[],"serviceLocation":"PetHotel"}""",
            "PetGroomer" => """{"session":{"services":[],"addOns":[],"dropOffTime":"10:00","pickUpTime":"11:00"},"animalsHandled":[],"dogTemperaments":[],"serviceLocation":"GroomerShop"}""",
            _ => """{"session":{"sessionDurationHours":1,"pricePerSession":10},"petsTrained":[],"ageGroups":[],"temperaments":[],"serviceLocations":[],"trainingApproaches":[]}"""
        };
        var request = JsonSerializer.Deserialize(requestJson, type, json)!;
        Check(type.GetProperty("LicenseType")!.GetValue(request) is null && type.GetProperty("LicenseImageUrl")!.GetValue(request) is null,
            $"{type.Name} accepts omitted identity fields");
    }
    var builder = cosmos.GetType($"Pawfront.Infrastructure.Cosmos.Services.{category}.Cosmos{category}ServiceRegistry")!
        .GetMethod("BuildLicenseAndOffering", BindingFlags.NonPublic | BindingFlags.Static)!;
    object?[] Tail() => category switch
    {
        "PetSitter" => [new BoardingOfferingInput(10, [], 1, 0, null, null, false), null, new[] { "Dog" }, 1, new[] { "Friendly" }, "PetHotel", false],
        "PetGroomer" => [new GroomingOfferingInput([new("BathAndDry", null, 10, 60, true)], [], 0, new(10,0), new(11,0)), new[] { "Dog" }, 1, new[] { "Friendly" }, "GroomerShop"],
        _ => [new TrainingSessionInput(1, 10), new[] { "Dog" }, new[] { "Adult" }, new[] { "Friendly" }, 1, new[] { "TrainingSchool" }, new[] { "Positive reinforcement" }, null, null]
    };
    ITuple Build(string? number, string? type, string? image) => (ITuple)builder.Invoke(null, new object?[] { number, type, image }.Concat(Tail()).ToArray())!;
    Check(Build(null, null, null)[0] is null, $"{category} offering saves without identity");
    Check(Build("old-number", "old-type", null)[0] is null, $"{category} clearing image removes stored identity even if old text remains");
    Check(Build(null, null, "https://test.invalid/id.png")[0] is not null, $"{category} license type and number are optional");
    Check(Build("123", "Identity", "https://test.invalid/id.png")[0] is not null && Build(null,null,null)[1] is not null,
        $"{category} identity changes preserve service offering");
}
var vetBuilder = cosmos.GetType("Pawfront.Infrastructure.Cosmos.Services.Vet.CosmosVetServiceRegistry")!
    .GetMethod("BuildCertificateAndOffering", BindingFlags.NonPublic | BindingFlags.Static)!;
try { vetBuilder.Invoke(null, [null, new VetAppointmentInput(1, 10), new[] { "Dog" }, 1, "VetClinic"]); throw new Exception("Vet certificate was optional"); }
catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { Check(true, "Vet certificate remains required"); }
var at = new DateTimeOffset(2030,1,1,13,30,0,TimeSpan.Zero);
var first = at.AddHours(4);
var later = at.AddHours(4.5);
Check(BookingAcceptanceDeadline.ComputeModification(at, first, later) == at.AddHours(2), "Later proposal expires at 15:30");
Check(BookingAcceptanceDeadline.ComputeModification(at, later, first) == at.AddHours(2), "Earlier proposal expires at 15:30");
Check(BookingAcceptanceDeadline.ComputeModification(at, at.AddDays(3), at.AddDays(4)) == at.AddHours(24), "24-hour review deadline wins for distant slots");
if (args.Contains("--sql")) await SqlChecks.Run(Check);
Console.WriteLine($"{checks} services module checks passed.");
