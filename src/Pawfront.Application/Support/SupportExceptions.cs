namespace Pawfront.Application.Support;

/// <summary>
/// The booking named by a "Report Incident" does not exist. Maps to
/// <b>404 BookingNotFound</b> / <b>404 NightStayBookingNotFound</b> depending on the
/// reported kind. THROW 51340.
/// </summary>
public sealed class SupportBookingNotFoundException(Guid bookingId)
    : Exception($"Booking '{bookingId}' was not found.");

/// <summary>
/// The conversation named by a "Report Chat" does not exist. Maps to
/// <b>404 ConversationNotFound</b>. THROW 51342.
/// </summary>
public sealed class SupportConversationNotFoundException(Guid conversationId)
    : Exception($"Conversation '{conversationId}' was not found.");

/// <summary>
/// The caller is not a party to the booking or conversation they are reporting. Maps to
/// <b>403 Forbidden</b>, matching every other "you are not a party to this" case in the
/// product. THROW 51341.
/// </summary>
/// <remarks>
/// This is the check that makes the derived counterparty trustworthy: the reporter names
/// only the subject and their own side, so being a party to that subject is the whole
/// authorisation.
/// </remarks>
public sealed class SupportForbiddenException()
    : Exception("You are not a party to this.");

/// <summary>
/// The reported booking is a Custom walk-in, which carries free-text customer details and
/// no PetParentId — there is no second party to report or to be reported. Maps to
/// <b>400 ReportNotAppBooking</b>, mirroring <c>PaymentNotAppBooking</c> and
/// <c>ReviewNotAppBooking</c>. THROW 51348.
/// </summary>
public sealed class SupportNotAppBookingException(Guid bookingId)
    : Exception($"Booking '{bookingId}' is a private job and cannot be reported.");

/// <summary>
/// No such ticket for this creator. Unknown id and "not the one you raised" are
/// deliberately the same case, so a ticket id cannot be probed for existence — the same
/// posture as <c>DeviceTokenNotFound</c> and <c>ReviewNotFound</c>. Maps to
/// <b>404 TicketNotFound</b>. THROW 51344.
/// </summary>
public sealed class SupportTicketNotFoundException(Guid ticketId)
    : Exception($"Ticket '{ticketId}' was not found.");

/// <summary>
/// The ticket already carries <see cref="SupportTicketLimits.MaxPhotos"/> photos. Maps to
/// <b>409 TicketPhotoLimitReached</b>. THROW 51345.
/// </summary>
public sealed class SupportTicketPhotoLimitReachedException(Guid ticketId, int maxPhotos)
    : Exception($"Ticket '{ticketId}' already has the maximum of {maxPhotos} photos.");

/// <summary>
/// Photos were attached to a chat incident. Only booking incidents carry them — the images
/// already in the thread are the evidence, and the whole conversation is under legal hold.
/// Maps to <b>400 TicketPhotoNotBookingIncident</b>. THROW 51346.
/// </summary>
public sealed class SupportTicketPhotoNotBookingIncidentException(Guid ticketId)
    : Exception($"Ticket '{ticketId}' is a chat incident and cannot carry photos.");

/// <summary>
/// The ticket is closed, so it accepts no further evidence or clarification. Maps to
/// <b>409 TicketClosed</b>. THROW 51347.
/// </summary>
public sealed class SupportTicketClosedException(Guid ticketId)
    : Exception($"Ticket '{ticketId}' is closed.");

/// <summary>
/// A clarification was submitted for a ticket support never asked one of. Rejecting rather
/// than silently accepting keeps the status honest — support reads
/// <c>CLARIFICATION_RECEIVED_FROM_CREATOR</c> as "the question I asked has been answered".
/// Maps to <b>409 NoClarificationRequested</b>. THROW 51349.
/// </summary>
public sealed class SupportNoClarificationRequestedException(Guid ticketId)
    : Exception($"No clarification has been requested on ticket '{ticketId}'.");

/// <summary>
/// The conversation is under legal hold by an open ticket, so its history cannot be
/// cleared and its messages cannot be retracted. Maps to <b>409 ConversationUnderLegalHold</b>.
/// </summary>
/// <remarks>
/// <para>
/// Carries the ticket number so the refusal can name it. Both parties are held, not just
/// the reporter: the accused is the one with a motive to erase, so a hold binding only the
/// person who raised it would be decorative.
/// </para>
/// <para>
/// Raised from two places for one rule. <c>Chat.DeleteConversationForParticipant</c>
/// enforces it in T-SQL (THROW 51352) because clearing a thread is a SQL write; deleting a
/// MESSAGE has no SQL statement in its path — the body is a Cosmos document replace — so
/// there the service reads <c>Support.GetConversationLegalHold</c> first and throws this.
/// </para>
/// </remarks>
public sealed class ConversationUnderLegalHoldException(Guid conversationId, int? ticketNumber = null)
    : Exception(ticketNumber is null
        ? $"Conversation '{conversationId}' cannot be changed while a support ticket is open on it."
        : $"Conversation '{conversationId}' cannot be changed while support ticket TK-{ticketNumber:D6} is open on it.")
{
    /// <summary>The holding ticket's friendly number, when known.</summary>
    public int? TicketNumber { get; } = ticketNumber;
}

// There is deliberately no "block frozen by a ticket" exception here. Reporting somebody
// does not block them, so a ticket has no severance to freeze: every block is the
// blocker's own and Chat.UnblockChatParticipant lifts it on request, always.
