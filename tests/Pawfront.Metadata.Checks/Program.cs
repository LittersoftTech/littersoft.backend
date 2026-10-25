using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

string[] expectedNames =
[
    "Day Care", "Night Stay", "Private Pet Training", "Vet Consultation",
    "Bath & Blow Dry", "Puppy First Groom", "Breed-Specific Styling", "Bath, Dry & Brush",
    "Tick & Flea Removal", "Medicated Bath", "De-matting", "De-Shedding",
    "Wire Coat Hand Stripping", "Cat Grooming", "Coat Dyeing", "Nails Clipping",
    "Ears Cleaning", "Oral Hygiene Pack", "Anal Gland Expression", "Paw Pad Trimming & Care",
    "Summer Coat Preparation", "Winter Coat Preparation"
];
string? providerPayload = null;
var checks = 0;

foreach (var provider in new[] { true, false })
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    await using var app = builder.Build();
    var api = app.MapGroup("/api/v1");
    if (provider)
        Pawfront.Api.Endpoints.MetadataEndpoints.MapMetadataEndpoints(api);
    else
        Pawfront.PetParentApi.Endpoints.MetadataEndpoints.MapMetadataEndpoints(api);

    await app.StartAsync();
    using var client = new HttpClient
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
    };

    async Task<JsonElement> GetData(string path)
    {
        using var response = await client.GetAsync(path);
        Check(response.StatusCode == HttpStatusCode.OK, $"HTTP 200: {path}");
        Check(response.Content.Headers.ContentType?.MediaType == "application/json", "JSON content type");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var envelope = json.RootElement;
        Check(envelope.GetProperty("success").GetBoolean(), "Success envelope");
        Check(envelope.GetProperty("error").ValueKind == JsonValueKind.Null, "No error");
        return envelope.GetProperty("data").Clone();
    }

    var all = await GetData("/api/v1/metadata/services");
    Check(all.GetArrayLength() == 22, "All 22 services");
    Check(all.EnumerateArray().Select(row => row.GetProperty("serviceName").GetString())
        .SequenceEqual(expectedNames), "Requested names and catalogue order");
    for (var i = 0; i < all.GetArrayLength(); i++)
    {
        var row = all[i];
        var expectedOwner = i < 2 ? "Pet Sitter" : i == 2 ? "Pet Trainer" : i == 3 ? "Vet" : "Pet Groomer";
        Check(row.GetProperty("serviceOwner").GetString() == expectedOwner, "Requested service owner");
        Check(!string.IsNullOrWhiteSpace(row.GetProperty("serviceDescription").GetString()), "Description present");
        var filtered = await GetData("/api/v1/metadata/services?SR=" + Uri.EscapeDataString(expectedNames[i]));
        Check(filtered.GetArrayLength() == 1 && filtered[0].GetRawText() == row.GetRawText(),
            "Full service-name lookup, including encoded punctuation");
    }
    Check(all[0].GetProperty("serviceDescription").GetString() ==
        "Pet day care facility for Short stays up to 12 hrs.", "Day Care description");
    Check(all[21].GetProperty("serviceDescription").GetString() ==
        "Fluff up your pet’s coat to prepare them for winter", "Unicode description");

    var normalized = await GetData("/api/v1/metadata/services?SR=%20dAy%20cArE%20");
    Check(normalized.GetArrayLength() == 1 && normalized[0].GetRawText() == all[0].GetRawText(),
        "Case-insensitive, trimmed matching");
    foreach (var value in new[] { "", "%20%20" })
        Check((await GetData("/api/v1/metadata/services?SR=" + value)).GetRawText() == all.GetRawText(),
            "Blank filter returns the whole catalogue");
    foreach (var value in new[] { "Unknown", "Day", "DayCare", "Pet%20Groomer" })
        Check((await GetData("/api/v1/metadata/services?SR=" + value)).GetArrayLength() == 0,
            "Unknown names, partial names, internal codes, and owners do not match");

    var metadata = await GetData("/api/v1/metadata");
    Check(metadata.GetProperty("animals").GetArrayLength() > 0 &&
        metadata.GetProperty("behaviours").GetArrayLength() > 0 &&
        metadata.EnumerateObject().Count() == 2, "Existing metadata response unchanged");
    if (provider)
        providerPayload = all.GetRawText();
    else
        Check(all.GetRawText() == providerPayload, "Both hosts return identical service metadata");

    await app.StopAsync();
}
Console.WriteLine($"Passed {checks} metadata endpoint checks across both hosts.");

void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}
