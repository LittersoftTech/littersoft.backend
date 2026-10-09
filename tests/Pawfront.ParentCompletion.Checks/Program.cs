using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pawfront.Application;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.Providers;
using Pawfront.Contracts.ParentPets;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}

var pet = new PetParentPetResponse(Guid.NewGuid(), Guid.NewGuid(), "Dog", "Milo",
    "Labrador", "Male", new DateOnly(2020, 1, 1), 20, null, null,
    null, null, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
    null, null, null, null);
Check(!pet.IsPetProfileComplete, "New pet must be incomplete");
pet = pet with { VaccinationStatus = "Vaccinated", SterilizationStatus = "Neutered" };
Check(!pet.IsPetProfileComplete, "Medical submission alone needs photo");
pet = pet with { ProfilePhotoUrl = "https://example.test/milo.jpg" };
Check(pet.IsPetProfileComplete, "Optional medical fields must not gate completion");
Check(!(pet with { Breed = " " }).IsPetProfileComplete, "Blank breed is incomplete");
Check(!(pet with { PetName = " " }).IsPetProfileComplete, "Blank name is incomplete");
Check(!(pet with { PetType = " " }).IsPetProfileComplete, "Blank species is incomplete");
Check(!(pet with { Gender = " " }).IsPetProfileComplete, "Blank gender is incomplete");
Check(!(pet with { DateOfBirth = default }).IsPetProfileComplete, "Missing DOB is incomplete");
Check(!(pet with { VaccinationStatus = " " }).IsPetProfileComplete, "Vaccination required");
Check(!(pet with { SterilizationStatus = null }).IsPetProfileComplete, "Sterilization required");
Check(!(pet with { ProfilePhotoUrl = null }).IsPetProfileComplete, "Photo removal resets completion");
var fullPet = new PetParentPetWithPhotosResponse(pet.PetId, pet.PetParentId, pet.PetType,
    pet.PetName, pet.Breed, pet.Gender, pet.DateOfBirth, pet.Weight, null, null,
    pet.VaccinationStatus, pet.SterilizationStatus, null, null, [], pet.CreatedAtUtc,
    pet.UpdatedAtUtc, pet.ProfilePhotoUrl, null, null, null, []);
Check(fullPet.IsPetProfileComplete, "GET response agrees with write response");
Check(!(fullPet with { ProfilePhotoUrl = null }).IsPetProfileComplete, "GET reflects deletion");
var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
Check(JsonSerializer.Serialize(fullPet, options).Contains("\"isPetProfileComplete\":true"),
    "Completion must be serialized");
var spoofed = JsonSerializer.Serialize(fullPet with { ProfilePhotoUrl = null }, options)
    .Replace("\"isPetProfileComplete\":false", "\"isPetProfileComplete\":true");
Check(!JsonSerializer.Deserialize<PetParentPetWithPhotosResponse>(spoofed, options)!.IsPetProfileComplete,
    "Client cannot override computed completion");

var reader = new SnapshotReader(new(pet.PetParentId, null, true, true,
    [new(pet.PetId, pet.PetName, false)], "Passport", false));
var services = new ServiceCollection().AddPawfrontApplication();
services.AddSingleton<IPetParentOnboardingStatusReader>(reader);
using var provider = services.BuildServiceProvider();
var statusService = provider.GetRequiredService<IPetParentOnboardingStatusService>();
var before = await statusService.GetAsync(pet.PetParentId, default);
Check(before.BasicInfo.Status == "Remaining", "Basic info checks saved fields");
Check(before.ProfilePhoto.Status == "Remaining", "Missing parent photo");
Check(before.Pets.Status == "Complete", "Pets stage means at least one pet");
Check(before.PetMedicalInfo.Pets.Single().MissingSections!.SequenceEqual(["MedicalInfo", "ProfilePhoto"]),
    "Initial missing sections");
reader.Snapshot = reader.Snapshot with { ProfilePhotoUrl = "https://example.test/parent.jpg",
    IsBasicInfoComplete = true, Pets = [new(pet.PetId, pet.PetName, true, true)] };
var after = await statusService.GetAsync(pet.PetParentId, default);
Check(after.BasicInfo.Status == "Complete" && after.ProfilePhoto.Status == "Complete",
    "Settings changes reflected on next read");
Check(after.PetMedicalInfo.Status == "Complete" && after.PetMedicalInfo.Pets.Single().MissingSections!.Count == 0,
    "Saved medical and photo clear missing sections");
reader.Snapshot = reader.Snapshot with { Pets = [new(pet.PetId, pet.PetName, true, false, IsDetailsComplete: false)] };
var removed = await statusService.GetAsync(pet.PetParentId, default);
Check(removed.PetMedicalInfo.Pets.Single().MissingSections!.SequenceEqual(["Details", "ProfilePhoto"]),
    "Missing details and removed photo reflected");
reader.Snapshot = reader.Snapshot with { Pets = [] };
var empty = await statusService.GetAsync(pet.PetParentId, default);
Check(empty.Pets.Status == "Remaining" && empty.PetMedicalInfo.Status == "Remaining", "No pets is incomplete");

var discovery = new Discovery();
var cities = await ProviderCities.ListAsync(discovery, default);
Check(cities.SequenceEqual(["Basel", "Zurich"]), "Cities trimmed, distinct, sorted, nonblank");
Check(discovery.Filter!.Take == int.MaxValue && discovery.Filter.Skip == 0, "Cities include all pages");
Console.WriteLine($"Passed {checks} parent completion and city checks.");

sealed class SnapshotReader(PetParentOnboardingStatusSnapshot snapshot) : IPetParentOnboardingStatusReader
{
    public PetParentOnboardingStatusSnapshot Snapshot { get; set; } = snapshot;
    public Task<PetParentOnboardingStatusSnapshot?> ReadAsync(Guid id, CancellationToken ct) => Task.FromResult<PetParentOnboardingStatusSnapshot?>(Snapshot);
}
sealed class Discovery : IProviderDiscoveryService
{
    public ProviderDiscoveryFilter? Filter { get; private set; }
    public Task<IReadOnlyList<ProviderSummary>> ListAsync(ProviderDiscoveryFilter filter, CancellationToken ct)
    {
        Filter = filter;
        return Task.FromResult<IReadOnlyList<ProviderSummary>>(
            new[] { " Zurich ", "Basel", "zurich", "", " " }.Select(city =>
                new ProviderSummary(Guid.NewGuid(), "Vet", "Vet", null, null, city, null, [])).ToArray());
    }
    public Task<ProviderSummary?> GetSummaryAsync(Guid id, string category, CancellationToken ct) => throw new NotSupportedException();
}
