using Microsoft.Extensions.Logging;
using Pawfront.Application.Storage;

namespace Pawfront.Application.Support;

/// <summary>
/// Composes the ticket's two halves: the SQL row that decides who may see it and what
/// rules it holds, and the Cosmos narrative that carries what was actually said.
/// </summary>
/// <remarks>
/// Every validation here runs BEFORE the store is touched, so a malformed request never
/// reaches SQL. The procedures re-check the same things and THROW, which is defence in
/// depth against a direct caller rather than duplication for its own sake — the same
/// posture the booking creates take.
/// </remarks>
internal sealed class SupportTicketService(
    ISupportTicketStore store,
    ISupportTicketNarrativeStore narrativeStore,
    // Only the combined report-with-photos flow needs this. The per-photo endpoint
    // uploads at the edge, but that flow has to interleave create and upload, and
    // duplicating that across two hosts is exactly the drift this layer exists to stop.
    IPawfrontBlobStorage blobStorage,
    ILogger<SupportTicketService> logger) : ISupportTicketService
{
    public Task<CreateSupportTicketResult> CreateAsync(
        CreateSupportTicketCommand command,
        CancellationToken cancellationToken)
        => CreateCoreAsync(Normalise(command), cancellationToken);

    public async Task<CreateSupportTicketWithPhotosResult> CreateWithPhotosAsync(
        CreateSupportTicketCommand command,
        IReadOnlyList<SupportTicketPhotoUpload> photos,
        CancellationToken cancellationToken)
    {
        var normalised = Normalise(command);
        photos ??= [];

        // EVERYTHING is validated before the ticket is created, so a caller who sent a
        // sixth photo is refused while that is still free rather than being left with a
        // raised report only half of their evidence reached.
        if (photos.Count > 0 && normalised.TicketType != SupportTicketTypes.BookingIncident)
        {
            throw new ArgumentException(
                "Only a reported booking incident can carry photos. The images already in the "
                + "thread are the evidence for a chat report.",
                nameof(photos));
        }

        if (photos.Count > SupportTicketLimits.MaxPhotos)
        {
            throw new ArgumentException(
                $"A ticket can carry at most {SupportTicketLimits.MaxPhotos} photos.", nameof(photos));
        }

        foreach (var photo in photos)
        {
            if (photo is null || photo.Length <= 0)
            {
                throw new ArgumentException("An empty image file was supplied.", nameof(photos));
            }

            if (photo.Length > SupportTicketLimits.MaxPhotoBytes)
            {
                throw new ArgumentException(
                    $"'{photo.FileName}' is larger than "
                    + $"{SupportTicketLimits.MaxPhotoBytes / (1024 * 1024)} MB.",
                    nameof(photos));
            }

            if (string.IsNullOrWhiteSpace(photo.ContentType)
                || !SupportTicketLimits.AllowedPhotoContentTypes.Contains(photo.ContentType))
            {
                throw new ArgumentException(
                    $"'{photo.FileName}' must be a JPEG, PNG, or WebP image.", nameof(photos));
            }
        }

        var created = await CreateCoreAsync(normalised, cancellationToken);

        if (created.Outcome == CreateSupportTicketOutcome.TicketAlreadyOpen)
        {
            // This call created nothing. Attaching evidence to a ticket somebody already
            // had open on this booking — possibly the counterparty's, about their own
            // account of it — would be wrong, so the photos are dropped and the caller is
            // handed the conflict.
            return new CreateSupportTicketWithPhotosResult(created.Outcome, created.Ticket, []);
        }

        var ticket = created.Ticket;
        var failures = new List<SupportTicketPhotoFailure>();

        foreach (var photo in photos)
        {
            try
            {
                var url = await blobStorage.UploadAsync(
                    BlobUploadKind.IncidentPhoto,
                    ticket.TicketId,
                    photo.FileName,
                    photo.Content,
                    photo.ContentType,
                    cancellationToken);

                // Each attach returns the whole ticket with its photos so far, so the last
                // success is the freshest view and no re-read is needed.
                ticket = await store.AddPhotoAsync(
                    ticket.TicketId,
                    normalised.RaisedByType,
                    normalised.ActorId,
                    url,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The ticket has committed. Failing the whole request over one photo
                // would report a report that DID happen as not happening — the same
                // failure the chat send was rebuilt to avoid. Name the casualty instead;
                // the client can retry it against the per-photo endpoint.
                logger.LogError(
                    exception,
                    "Support ticket {TicketNumber} ({TicketId}) was created but photo '{FileName}' "
                    + "could not be attached.",
                    ticket.TicketNumber,
                    ticket.TicketId,
                    photo.FileName);

                failures.Add(new SupportTicketPhotoFailure(
                    photo.FileName,
                    "PhotoUploadFailed",
                    "This photo could not be attached. Retry it on the ticket's photos endpoint."));
            }
        }

        return new CreateSupportTicketWithPhotosResult(created.Outcome, ticket, failures);
    }

    /// <summary>
    /// The shared create: SQL row first, then the narrative. Both entry points run through
    /// here so the ordering and the best-effort narrative rule cannot diverge between them.
    /// </summary>
    private async Task<CreateSupportTicketResult> CreateCoreAsync(
        CreateSupportTicketCommand normalised,
        CancellationToken cancellationToken)
    {
        var result = await store.CreateAsync(normalised, cancellationToken);

        if (result.Outcome == CreateSupportTicketOutcome.TicketAlreadyOpen)
        {
            // Nothing was written, so there is no narrative to attach. The caller answers
            // 409 naming the ticket already open.
            return result;
        }

        // The row is committed. The narrative is a best-effort second leg: failing the
        // request now would report a ticket that DOES exist as not created, and leave the
        // reporter believing support has heard nothing.
        try
        {
            await narrativeStore.CreateAsync(
                result.Ticket.TicketId,
                normalised.RaisedByType,
                normalised.ActorId,
                normalised.Comment,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Support ticket {TicketNumber} ({TicketId}) was created but its narrative could not be stored. " +
                "The ticket stands; support must request the detail through clarification.",
                result.Ticket.TicketNumber,
                result.Ticket.TicketId);
        }

        return result;
    }

    /// <summary>
    /// Validates a report and rebuilds it with only the columns its type governs. Shared
    /// by both create entry points; throws <see cref="ArgumentException"/> before any
    /// store is touched.
    /// </summary>
    private static CreateSupportTicketCommand Normalise(CreateSupportTicketCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!SupportTicketTypes.IsKnown(command.TicketType))
        {
            throw new ArgumentException(
                $"Unsupported ticketType '{command.TicketType}'.", nameof(command));
        }

        if (!SupportRaisedByTypes.IsKnown(command.RaisedByType))
        {
            throw new ArgumentException(
                $"Unsupported raisedByType '{command.RaisedByType}'.", nameof(command));
        }

        var comment = command.Comment?.Trim();
        if (string.IsNullOrEmpty(comment))
        {
            // The comment is the substance of the report. A ticket without one gives
            // support nothing to act on, so it is required here even though no column
            // constrains it — the narrative lives in Cosmos.
            throw new ArgumentException("A description of the incident is required.", nameof(command));
        }

        if (comment.Length > SupportTicketLimits.MaxCommentLength)
        {
            throw new ArgumentException(
                $"The description must be {SupportTicketLimits.MaxCommentLength} characters or fewer.",
                nameof(command));
        }

        // Both classifiers are optional and free-form: blank collapses to null so a read
        // never has to tell an empty picker value apart from an unset one. Their VALUES
        // are deliberately unvalidated — the vocabulary belongs to the app's picker, and
        // pinning it here would make adding a category a backend release.
        var category = string.IsNullOrWhiteSpace(command.Category) ? null : command.Category.Trim();
        if (category is { Length: > SupportTicketLimits.MaxCategoryLength })
        {
            throw new ArgumentException(
                $"The category must be {SupportTicketLimits.MaxCategoryLength} characters or fewer.",
                nameof(command));
        }

        var reason = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim();
        if (reason is { Length: > SupportTicketLimits.MaxReasonLength })
        {
            throw new ArgumentException(
                $"The reason must be {SupportTicketLimits.MaxReasonLength} characters or fewer.",
                nameof(command));
        }

        // Exactly one subject, matching the type — the same rule CK_Tickets_SubjectMatchesType
        // enforces on the row. Checked here so the caller gets a 400 naming the missing
        // field rather than a defensive 51343.
        string? bookingType = null;
        Guid? bookingId = null;
        Guid? conversationId = null;

        if (command.TicketType == SupportTicketTypes.BookingIncident)
        {
            if (command.BookingId is not { } reportedBookingId || reportedBookingId == Guid.Empty)
            {
                throw new ArgumentException("A bookingId is required to report an incident.", nameof(command));
            }

            // Case-insensitive, matching how a bulk cancellation accepts the same pair.
            bookingType = Bookings.BookingTypes.Normalize(command.BookingType)
                ?? throw new ArgumentException(
                    $"Unsupported bookingType '{command.BookingType}'. Expected 'SingleDay' or 'NightStay'.",
                    nameof(command));

            bookingId = reportedBookingId;
        }
        else
        {
            if (command.ConversationId is not { } reportedConversationId || reportedConversationId == Guid.Empty)
            {
                throw new ArgumentException("A conversationId is required to report a chat.", nameof(command));
            }

            conversationId = reportedConversationId;
        }

        // Rebuilt rather than passed through, so the columns the type does NOT govern are
        // sent as NULL whatever the caller supplied — a chat report carrying a stray
        // bookingId would otherwise trip CK_Tickets_SubjectMatchesType at the database.
        return command with
        {
            Comment = comment,
            Category = category,
            Reason = reason,
            BookingType = bookingType,
            BookingId = bookingId,
            ConversationId = conversationId
        };
    }

    public async Task<SupportTicketDetail?> GetAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var ticket = await store.GetAsync(ticketId, actorType, actorId, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        return new SupportTicketDetail(ticket, await TryReadNarrativeAsync(ticketId, cancellationToken));
    }

    public Task<SupportTicketListResult> ListAsync(
        SupportTicketQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var take = query.Take <= 0
            ? SupportTicketLimits.MaxPageSize
            : Math.Min(query.Take, SupportTicketLimits.MaxPageSize);

        return store.ListAsync(
            query with { Skip = Math.Max(query.Skip, 0), Take = take },
            cancellationToken);
    }

    public Task<SupportTicketRecord> AddPhotoAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        string photoUrl,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(photoUrl))
        {
            throw new ArgumentException("A photo url is required.", nameof(photoUrl));
        }

        return store.AddPhotoAsync(ticketId, actorType, actorId, photoUrl.Trim(), cancellationToken);
    }

    public async Task<SupportTicketDetail> RecordClarificationAsync(
        Guid ticketId,
        string actorType,
        Guid actorId,
        string reply,
        CancellationToken cancellationToken)
    {
        var text = reply?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("A reply is required.", nameof(reply));
        }

        if (text.Length > SupportTicketLimits.MaxCommentLength)
        {
            throw new ArgumentException(
                $"The reply must be {SupportTicketLimits.MaxCommentLength} characters or fewer.",
                nameof(reply));
        }

        // Authorise before writing anything. The status move re-checks all of this, but
        // that check happens AFTER the Cosmos append below — so without this a stranger,
        // or somebody replying to a closed ticket, could still leave text on the document
        // before being refused.
        var ticket = await store.GetAsync(ticketId, actorType, actorId, cancellationToken)
            ?? throw new SupportTicketNotFoundException(ticketId);

        if (!ticket.WasRaisedBy(actorType))
        {
            // Support asks the creator and receives from the creator; the counterparty is
            // never in this loop. Reported as "not found" for the same reason the
            // procedure does — the two cases must not be distinguishable.
            throw new SupportTicketNotFoundException(ticketId);
        }

        if (!ticket.IsOpen)
        {
            throw new SupportTicketClosedException(ticketId);
        }

        if (!string.Equals(
                ticket.Status, SupportTicketStatuses.ClarificationAskedToCreator, StringComparison.Ordinal))
        {
            throw new SupportNoClarificationRequestedException(ticketId);
        }

        // Cosmos FIRST, then the status. If the document write fails after the status has
        // moved, the ticket claims an answer that was never recorded; this way a failure
        // leaves it in ASKED with the reply stored — visibly unfinished, and fixed by
        // retrying. Same lesson the chat send's reserve/commit split encodes.
        var narrative = await narrativeStore.AppendAsync(
            ticketId,
            actorType,
            actorId,
            SupportNarrativeEntryKinds.ClarificationReply,
            text,
            cancellationToken);

        var updated = await store.RecordClarificationAsync(ticketId, actorType, actorId, cancellationToken);

        return new SupportTicketDetail(updated, narrative);
    }

    /// <summary>
    /// Best-effort narrative read. A Cosmos outage degrades the ticket detail to its row
    /// rather than failing it — the row is what proves the ticket exists and who may see
    /// it, and a reporter checking on their complaint should not get a 500 because the
    /// text store is briefly unavailable.
    /// </summary>
    private async Task<SupportTicketNarrative?> TryReadNarrativeAsync(
        Guid ticketId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await narrativeStore.GetAsync(ticketId, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Could not read the narrative for support ticket {TicketId}; returning the ticket without it.",
                ticketId);
            return null;
        }
    }
}
