namespace Pawfront.Application.Support;

/// <summary>
/// The caller's own open ticket on one subject, as surfaced next to it —
/// <c>isTicketRaisedByMe</c> / <c>ticketId</c> / <c>ticketRef</c> on a booking, an event or
/// a conversation.
/// </summary>
/// <param name="TicketRef">
/// The friendly <c>TK-000123</c> label. Both halves travel because the app needs both: the
/// GUID is what the <c>.../support-tickets/{ticketId}</c> routes take, and the ref is what
/// a screen can actually show.
/// </param>
public sealed record MySupportTicketRef(Guid TicketId, int TicketNumber, string TicketType)
{
    public string TicketRef => $"TK-{TicketNumber:D6}";
}

/// <summary>
/// One caller's open tickets, indexed by what they were raised on.
/// </summary>
/// <remarks>
/// <para>
/// Built from ONE read of every open ticket the caller raised, then asked once per card.
/// That is why the query is scoped to the actor rather than taking a list of subject ids: a
/// booking list page costs one round trip instead of one per row, the same reasoning behind
/// <c>Review.ListPetParentBookingReviews</c>.
/// </para>
/// <para>
/// OPEN tickets only, and RAISED BY the caller only. Both restrictions matter: a closed
/// ticket is exactly the state in which a fresh report is allowed again, and the
/// counterparty's ticket is neither the caller's to open nor theirs to be told about.
/// </para>
/// </remarks>
public sealed class MySupportTicketSubjects
{
    /// <summary>Nobody has reported anything — also what a failed read degrades to.</summary>
    public static readonly MySupportTicketSubjects Empty = new([]);

    // Keyed by "SingleDay:<guid>" / "NightStay:<guid>", because the two booking tables
    // share no id space and a bare GUID could name either.
    private readonly Dictionary<string, MySupportTicketRef> _byBooking = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, MySupportTicketRef> _byConversation = [];
    private readonly Dictionary<Guid, MySupportTicketRef> _byEvent = [];

    public MySupportTicketSubjects(IEnumerable<MySupportTicketSubject> tickets)
    {
        ArgumentNullException.ThrowIfNull(tickets);

        foreach (var ticket in tickets)
        {
            var reference = new MySupportTicketRef(ticket.TicketId, ticket.TicketNumber, ticket.TicketType);

            // TryAdd, not the indexer: at most one open ticket can exist per subject, but a
            // duplicate arriving here must not throw and blank out a whole page.
            if (ticket.BookingId is { } bookingId && ticket.BookingType is { } bookingType)
            {
                _byBooking.TryAdd(BookingKey(bookingType, bookingId), reference);
            }
            else if (ticket.ConversationId is { } conversationId)
            {
                _byConversation.TryAdd(conversationId, reference);
            }
            else if (ticket.EventId is { } eventId)
            {
                _byEvent.TryAdd(eventId, reference);
            }

            // An app issue has no subject and so appears on no card. It is carried this far
            // rather than filtered in SQL so the query stays "my open tickets".
        }
    }

    public MySupportTicketRef? ForBooking(string? bookingType, Guid bookingId) =>
        bookingType is null
            ? null
            : _byBooking.GetValueOrDefault(BookingKey(bookingType, bookingId));

    public MySupportTicketRef? ForConversation(Guid conversationId) =>
        _byConversation.GetValueOrDefault(conversationId);

    public MySupportTicketRef? ForEvent(Guid eventId) =>
        _byEvent.GetValueOrDefault(eventId);

    private static string BookingKey(string bookingType, Guid bookingId) =>
        string.Concat(bookingType, ":", bookingId.ToString("N"));
}

/// <summary>One row of <c>Support.ListMyOpenTicketSubjects</c>: a ticket and what it is on.</summary>
public sealed record MySupportTicketSubject(
    Guid TicketId,
    int TicketNumber,
    string TicketType,
    string? BookingType,
    Guid? BookingId,
    Guid? ConversationId,
    Guid? EventId);

/// <summary>
/// "Which of my subjects have I already reported?" — read once per request by every surface
/// that offers a Report button: the booking detail and both booking lists, the event list
/// and detail, and the chat inbox and thread, across all three hosts.
/// </summary>
/// <remarks>
/// Its own narrow interface rather than a method on <see cref="ISupportTicketService"/>
/// because the chat host consumes it and has no other reason to know the support module
/// exists — the same reasoning that gave <see cref="ISupportLegalHoldReader"/> its own.
/// </remarks>
public interface IMySupportTicketLookup
{
    /// <summary>
    /// The caller's open tickets, keyed by subject. <b>Best-effort by contract</b>:
    /// implementations return <see cref="MySupportTicketSubjects.Empty"/> rather than
    /// throwing, because this decorates a list and must never be the reason one fails.
    /// </summary>
    /// <param name="actorType">A <see cref="SupportRaisedByTypes"/> value.</param>
    Task<MySupportTicketSubjects> GetAsync(
        string actorType,
        Guid actorId,
        CancellationToken cancellationToken);
}
