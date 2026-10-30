using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Pawfront.Application.Storage;

foreach (var provider in new[] { true, false })
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    var storage = new FakeStorage();
    builder.Services.AddSingleton<IDocumentationStorage>(storage);
    await using var app = builder.Build();
    app.MapGroup("/api/v1").MapDocumentationEndpoints(provider);
    await app.StartAsync();
    using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
    foreach (var category in new[] { "terms-and-usage", "provider-faq", "parent-faq", "cookies-policy", "privacy-policy", "community-faq", "unknown", "TERMS-AND-USAGE" })
    {
        var expected = DocumentationCatalog.ResolveFileName(category, provider);
        storage.LastFile = null;
        using var response = await client.GetAsync($"/api/v1/documents/{category}");
        Check(response.StatusCode == (expected is null ? HttpStatusCode.NotFound : HttpStatusCode.OK), category);
        Check(storage.LastFile == expected, "Host-specific mapping: " + category);
        if (expected is null) continue;
        Check(await response.Content.ReadAsStringAsync() == "document bytes", "Stream bytes");
        Check(response.Content.Headers.ContentType?.MediaType == DocumentationCatalog.ContentType(expected), "MIME type");
        Check(response.Content.Headers.ContentDisposition?.DispositionType == "attachment", "Attachment");
        Check(response.Content.Headers.ContentDisposition?.FileNameStar == expected, "Filename");
    }
    storage.Missing = true;
    using var missing = await client.GetAsync("/api/v1/documents/privacy-policy");
    Check(missing.StatusCode == HttpStatusCode.NotFound, "Missing blob");
    await app.StopAsync();
}
Console.WriteLine("Documentation endpoint checks passed for both hosts.");
static void Check(bool condition, string name) { if (!condition) throw new Exception(name); }
sealed class FakeStorage : IDocumentationStorage
{
    public string? LastFile;
    public bool Missing;
    public Task<BlobDownload?> DownloadDocumentAsync(string fileName, CancellationToken cancellationToken)
    {
        LastFile = fileName;
        return Task.FromResult<BlobDownload?>(Missing ? null : new BlobDownload(new MemoryStream(Encoding.UTF8.GetBytes("document bytes")), "application/octet-stream", 14));
    }
}
