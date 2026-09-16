using System.Net;
using Microsoft.Azure.Cosmos;
using Pawfront.Application.Support;
using Pawfront.Infrastructure.Cosmos.Documents;

namespace Pawfront.Infrastructure.Cosmos.Support;

/// <summary>
/// A support ticket's narrative, in the SupportTickets container. Every operation is a
/// point read or write on <c>(ticketId, ticketId)</c> — the document id and the partition
/// key are the same value — so nothing here ever runs a query.
/// </summary>
internal sealed class CosmosSupportTicketNarrativeStore(
    ISupportTicketsContainerAccessor containerAccessor) : ISupportTicketNarrativeStore
{
    public async Task<SupportTicketNarrative> CreateAsync(
        Guid ticketId,
        string authorType,
        Guid authorId,
        string comment,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var id = ticketId.ToString();
        var now = DateTimeOffset.UtcNow;

        var document = new SupportTicketDocument
        {
            Id = id,
            TicketId = id,
            Entries =
            [
                new SupportTicketEntryDocument
                {
                    EntryId = Guid.NewGuid().ToString(),
                    AuthorType = authorType,
                    AuthorId = authorId.ToString(),
                    Kind = SupportNarrativeEntryKinds.Report,
                    Text = comment,
                    CreatedAtUtc = now
                }
            ],
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        try
        {
            var response = await container.CreateItemAsync(
                document,
                new PartitionKey(id),
                cancellationToken: cancellationToken);

            return ToNarrative(response.Resource);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            // A retry after a response that was lost in flight. The first write wins and
            // this returns it, so a ticket can never end up with two opening reports.
            var existing = await GetAsync(ticketId, cancellationToken);
            return existing ?? ToNarrative(document);
        }
    }

    public async Task<SupportTicketNarrative> AppendAsync(
        Guid ticketId,
        string authorType,
        Guid? authorId,
        string kind,
        string text,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var id = ticketId.ToString();
        var partitionKey = new PartitionKey(id);

        SupportTicketDocument document;
        string? etag;

        try
        {
            var current = await container.ReadItemAsync<SupportTicketDocument>(
                id, partitionKey, cancellationToken: cancellationToken);
            document = current.Resource;
            etag = current.ETag;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // The document is written just after the row on the create path, so it can be
            // missing if that leg failed. Rebuilding it here rather than refusing means a
            // clarification is never lost to an earlier outage — the thread simply starts
            // from this entry.
            document = new SupportTicketDocument
            {
                Id = id,
                TicketId = id,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            etag = null;
        }

        var now = DateTimeOffset.UtcNow;
        document.Entries.Add(new SupportTicketEntryDocument
        {
            EntryId = Guid.NewGuid().ToString(),
            AuthorType = authorType,
            AuthorId = authorId?.ToString(),
            Kind = kind,
            Text = text,
            CreatedAtUtc = now
        });
        document.UpdatedAtUtc = now;

        // The ETag makes the read-modify-write safe: support adding a note while the
        // creator submits a reply would otherwise let one overwrite the other's entry.
        var options = etag is null
            ? null
            : new ItemRequestOptions { IfMatchEtag = etag };

        var saved = await container.UpsertItemAsync(
            document, partitionKey, options, cancellationToken);

        return ToNarrative(saved.Resource);
    }

    public async Task<SupportTicketNarrative?> GetAsync(
        Guid ticketId,
        CancellationToken cancellationToken)
    {
        var container = await containerAccessor.GetContainerAsync(cancellationToken);
        var id = ticketId.ToString();

        try
        {
            var response = await container.ReadItemAsync<SupportTicketDocument>(
                id, new PartitionKey(id), cancellationToken: cancellationToken);

            return ToNarrative(response.Resource);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Not an error — see ISupportTicketNarrativeStore.GetAsync.
            return null;
        }
    }

    private static SupportTicketNarrative ToNarrative(SupportTicketDocument document)
    {
        var entries = document.Entries
            // Ordered here rather than trusted from the document: entries are appended, so
            // the stored order is already correct, but a concurrent write reconciled by a
            // retry could land one out of place.
            .OrderBy(entry => entry.CreatedAtUtc)
            .Select(entry => new SupportTicketNarrativeEntry(
                EntryId: Guid.TryParse(entry.EntryId, out var entryId) ? entryId : Guid.Empty,
                AuthorType: entry.AuthorType,
                AuthorId: Guid.TryParse(entry.AuthorId, out var authorId) ? authorId : null,
                Kind: entry.Kind,
                Text: entry.Text,
                CreatedAtUtc: entry.CreatedAtUtc))
            .ToArray();

        return new SupportTicketNarrative(
            TicketId: Guid.TryParse(document.TicketId, out var ticketId) ? ticketId : Guid.Empty,
            Entries: entries,
            CreatedAtUtc: document.CreatedAtUtc,
            UpdatedAtUtc: document.UpdatedAtUtc);
    }
}
