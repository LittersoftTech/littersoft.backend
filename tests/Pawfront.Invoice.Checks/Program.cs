using Pawfront.Functions.Invoices;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Pawfront.Application.Billing;
using Pawfront.Application.Storage;

QuestPDF.Settings.License = LicenseType.Community;
var facts = new InvoiceBookingFacts(
    Guid.NewGuid(), "SingleDay", "PF-000123", Guid.NewGuid(), Guid.NewGuid(),
    "PetSitter", "Dog", "DayCare", new DateOnly(2026, 9, 16), null,
    new TimeOnly(9, 0), new TimeOnly(17, 0), 20m,
    "Alex", "Example", "+41", "123456789", "provider@example.test",
    "Sam", "Example", "Example Street 12", "Zurich", "8000", "parent@example.test",
    "Buddy", "Dog", "Retriever", DateTimeOffset.UtcNow, "Cash", "DayCare");
var invoice = new ClaimedInvoice(Guid.NewGuid(), "PF-INV-2026-000123", "PetParent", 160m, 16m, DateTimeOffset.UtcNow, 1);
foreach (var nightStay in new[] { false, true })
foreach (var longIdentity in new[] { false, true })
{
    var booking = nightStay ? facts with { BookingType = "NightStay", EndDate = facts.ServiceDate.AddDays(3) } : facts;
    var identity = longIdentity
        ? new InvoiceProviderIdentity(new string('W', 200), new string('A', 400), "Zurich", "8000")
        : InvoiceProviderIdentity.Unknown;
    IDocument[] documents = [
        new ParentInvoiceDocument(booking, invoice, identity),
        new ProviderInvoiceDocument(booking, invoice with { Recipient = "Provider" }, identity, new InvoiceOptions().Littersoft, 10m, 0m, 8.1m)
    ];
    foreach (var document in documents)
    {
        var bytes = document.GeneratePdf();
        if (bytes.Length < 1000 || System.Text.Encoding.ASCII.GetString(bytes, 0, 5) != "%PDF-")
            throw new Exception("Invoice rendering did not produce a PDF.");
        Console.WriteLine($"PASS {document.GetType().Name}: nightStay={nightStay}, longIdentity={longIdentity}");
    }
}

var row = new BookingInvoice(invoice.InvoiceId, invoice.InvoiceNumber, facts.BookingType,
    facts.BookingId, "PetParent", facts.ProviderId, facts.PetParentId, 160m, 16m,
    InvoiceStatuses.Pending, null, DateTimeOffset.UtcNow, null);
var query = new InvoiceDownloadQuery(facts.BookingType, facts.BookingId, "PetParent", null, facts.PetParentId);
foreach (var status in new[] { "Pending", "Generating", "Failed", "Generated", "Missing" })
{
    var queue = new QueueFake();
    var blob = new BlobFake();
    var service = new InvoiceService(new StoreFake(status == "Missing" ? null : row with
    {
        Status = status, InvoiceUrl = status == "Generated" ? "https://example.test/invoice.pdf" : null
    }), blob, queue);
    try
    {
        var download = await service.GetDownloadAsync(query, CancellationToken.None);
        using var content = download.Content;
        if (status != "Generated") throw new Exception("Unexpected successful download.");
    }
    catch (InvoiceNotReadyException ex) when (status is "Pending" or "Generating" or "Failed")
    {
        if (ex.Status != status) throw;
    }
    catch (InvoiceNotFoundException) when (status == "Missing") { }
    if (queue.Count != (status == "Pending" ? 1 : 0)) throw new Exception("Incorrect recovery publication.");
    if (blob.Reads != (status == "Generated" ? 1 : 0)) throw new Exception("Premature blob download.");
    Console.WriteLine($"PASS download state {status}");
}

sealed class StoreFake(BookingInvoice? invoice) : IInvoiceStore
{
    public Task<BookingInvoice?> GetAsync(InvoiceDownloadQuery query, CancellationToken token) => Task.FromResult(invoice);
}
sealed class QueueFake : IInvoiceQueuePublisher
{
    public int Count { get; private set; }
    public Task PublishAsync(InvoiceGenerationRequest request, CancellationToken token)
    {
        Count++;
        return Task.CompletedTask;
    }
}
sealed class BlobFake : IPawfrontBlobStorage
{
    public int Reads { get; private set; }
    public Task<BlobDownload?> DownloadAsync(string url, CancellationToken token)
    {
        Reads++;
        return Task.FromResult<BlobDownload?>(new BlobDownload(new MemoryStream([1, 2, 3]), "application/pdf", 3));
    }
    public Task<string> UploadAsync(BlobUploadKind kind, Guid ownerId, string fileName, Stream content, string contentType, CancellationToken token) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(string url, CancellationToken token) => throw new NotSupportedException();
}
