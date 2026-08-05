namespace Pawfront.Application.Notifications;

/// <summary>
/// The deep-link routes sent as <c>data.route</c>, which the mobile apps use to
/// open the right screen when a notification is tapped.
///
/// <b>PROVISIONAL — these strings are placeholders awaiting the mobile team's
/// route table.</b> They are deliberately isolated in this one file (rather than
/// inlined into <see cref="NotificationTemplateCatalog"/>) so that adopting the
/// real routes is a single-file change with no effect on templates, triggers, or
/// the wire contract's shape.
///
/// The route is a path only. Ids travel as separate <c>data</c> keys
/// (<c>entityId</c>, <c>bookingId</c>, …) rather than being interpolated into the
/// path, so the app can build its own navigation without string-parsing.
/// </summary>
public static class NotificationRoutes
{
    /// <summary>Single-day booking detail. Pair with <c>bookingId</c>.</summary>
    public const string BookingDetail = "/bookings/detail";

    /// <summary>Night-stay (multi-night boarding) booking detail. Pair with <c>bookingId</c>.</summary>
    public const string NightStayBookingDetail = "/night-stay-bookings/detail";

    /// <summary>The provider's list of incoming requests awaiting accept/decline.</summary>
    public const string BookingRequests = "/bookings/requests";

    /// <summary>The pending-modification review screen for a booking.</summary>
    public const string BookingModification = "/bookings/modification";

    /// <summary>The parent's screen showing the start code to read out to the provider.</summary>
    public const string BookingStartOtp = "/bookings/start-code";

    /// <summary>The vet prescription attached to a completed booking.</summary>
    public const string BookingPrescription = "/bookings/prescription";

    /// <summary>Public event detail. Pair with <c>eventId</c>.</summary>
    public const string EventDetail = "/events/detail";

    /// <summary>A ticket booking's detail. Pair with <c>eventBookingId</c>.</summary>
    public const string EventBookingDetail = "/event-bookings/detail";

    /// <summary>The organiser's attendee/metrics dashboard for their event.</summary>
    public const string EventDashboard = "/events/dashboard";

    /// <summary>
    /// The provider's list of requests that expired unanswered. Per the V3 spec,
    /// the provider's expiry notification lands here rather than on the booking,
    /// so they see the cumulative cost of ignoring requests.
    /// </summary>
    public const string IgnoredJobs = "/payouts/ignored-jobs";

    /// <summary>A chat thread. Pair with <c>conversationId</c>.</summary>
    public const string Conversation = "/messages/thread";

    /// <summary>An invoice, opened in the in-app PDF viewer. Pair with <c>invoiceId</c>.</summary>
    public const string Invoice = "/invoices/detail";

    /// <summary>
    /// A single support ticket — Helpline → Raised Tickets → Ticket Details. Never
    /// the ticket list: the V3 spec is explicit that it deep-links to the ticket.
    /// </summary>
    public const string TicketDetail = "/helpline/tickets/detail";

    /// <summary>Campaign content has no entity, so a promotional push opens the inbox.</summary>
    public const string NotificationInbox = "/notifications";
}
