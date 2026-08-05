namespace Pawfront.Application.Notifications;

/// <summary>
/// One notification's copy, category and destination. <c>{placeholder}</c> tokens
/// are filled from the outbox row's <c>DataJson</c> by <see cref="NotificationRenderer"/>.
/// </summary>
public sealed record NotificationTemplate(
    string TitleTemplate,
    string BodyTemplate,
    string Route,
    string Category);

/// <summary>
/// The single source of every user-facing notification string in the product.
///
/// Copy lives here — not at the call sites and not in T-SQL — because
/// notifications are enqueued from three different processes (both API hosts and
/// the Pawfront.Functions sweeps, the last of which is pure SQL). Rendering
/// centrally is what lets a sweep sproc enqueue nothing but a type and a few
/// parameters, and still produce the same wording as the API hosts.
///
/// Adding a notification = add a key to <see cref="NotificationTypes"/> and an
/// entry here. A type with no entry renders nothing and is recorded as a failed
/// dispatch, so the gap surfaces rather than silently sending a blank push.
///
/// <b>Copy can vary by audience, the wire type cannot.</b> Several events are
/// mirrored to both apps with different wording ("Has the service started?" to the
/// parent, "You haven't started the job" to the provider). Those live in
/// <see cref="AudienceOverrides"/> under the SAME
/// <see cref="NotificationTypes"/> key, because they are one event: duplicating
/// the type would force the mobile apps to handle two keys for one thing, and
/// would let the two halves drift apart.
/// </summary>
public static class NotificationTemplateCatalog
{
    private const string Booking = NotificationCategories.Booking;
    private const string Event = NotificationCategories.Event;

    /// <summary>
    /// The default copy for each type — used as-is for single-audience types, and
    /// as the fallback for any audience with no override below.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, NotificationTemplate> Templates =
        new Dictionary<string, NotificationTemplate>(StringComparer.Ordinal)
        {
            // ================================================================
            // To the PET PARENT — the provider acted (V3 cards P-U*)
            // ================================================================

            // P-U1
            [NotificationTypes.BookingAccepted] = new(
                "Booking confirmed",
                "{providerName} accepted your {serviceName} booking for {petName} on {serviceDate} at {startTime}. You're all set!",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-U2
            [NotificationTypes.BookingDeclined] = new(
                "Booking request declined",
                "{providerName} couldn't take your {serviceName} booking for {petName} on {serviceDate}. No payment is needed — try another provider.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-U3
            [NotificationTypes.BookingCancelledByProvider] = new(
                "Booking cancelled by provider",
                "{providerName} has cancelled {petName}'s {serviceName} booking on {serviceDate}. No payment is needed.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-S1. Deliberately does NOT say "within 24 hours": two rules expire an
            // unaccepted booking (BR-17's 24 hours, and BR-53's under-2-hours-to-service),
            // and quoting the wrong one would contradict what the sweep actually did.
            [NotificationTypes.BookingExpiredForParent] = new(
                "Job request expired",
                "{providerName} didn't respond in time, so your {serviceName} request for {petName} has expired. Please re-book. No payment is needed.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-U7
            [NotificationTypes.BookingStartOtpIssued] = new(
                "Share your OTP",
                "{providerName} has started {petName}'s {serviceName}. Tap to view and share your OTP with the provider.",
                NotificationRoutes.BookingStartOtp,
                Booking),

            // P-U8
            [NotificationTypes.BookingInProgress] = new(
                "Your job has started",
                "{petName}'s {serviceName} has started. Thank you for the OTP! We'll let you know when it's done.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-U11 / P-U12. The flow draws two completion nodes — one where the
            // customer has not yet come to collect, one at hand-over — but nothing
            // in the schema records which happened, so this single card covers both:
            // the pet needs collecting and the cash is due either way.
            [NotificationTypes.BookingCompleted] = new(
                "Job complete — please pay",
                "{petName}'s {serviceName} is complete. Please collect your pet and pay {amount} in cash to {providerName}.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-U13
            [NotificationTypes.BookingPaid] = new(
                "Thank you for your payment",
                "{providerName} has confirmed receiving {amount} in cash for {petName}'s {serviceName}. Thank you!",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-U4
            [NotificationTypes.BookingModificationRequestedByProvider] = new(
                "Provider requested a modification",
                "{providerName} has proposed a new timing for {petName}'s {serviceName} booking. Review it within 24 hours.",
                NotificationRoutes.BookingModification,
                Booking),

            // P-U5
            [NotificationTypes.BookingModificationAcceptedByProvider] = new(
                "Modification confirmed",
                "{providerName} accepted your modification request. The new timing for {petName}'s {serviceName} is confirmed: {newServiceDate} at {newStartTime}.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-U6
            [NotificationTypes.BookingModificationDeclinedByProvider] = new(
                "Modification declined",
                "{providerName} declined your modification request. Your booking stays as originally scheduled: {serviceDate} at {startTime}.",
                NotificationRoutes.BookingDetail,
                Booking),

            // Not in the V3 flow — a Pawfront feature the spec doesn't cover.
            [NotificationTypes.BookingPrescriptionRecorded] = new(
                "Prescription added",
                "{providerName} added a prescription for {petName}.",
                NotificationRoutes.BookingPrescription,
                Booking),

            // P-S11
            [NotificationTypes.BookingOtpAttemptsExceeded] = new(
                "Booking cancelled — OTP not verified",
                "The OTP for {petName}'s {serviceName} couldn't be verified after the maximum attempts, so the job has been cancelled. No payment is needed.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-S9 — the parent has not yet opened their start code.
            [NotificationTypes.BookingStartOtpNotSeen] = new(
                "You're late for your appointment",
                "You are late for {petName}'s {serviceName} appointment. Tap to view and share your OTP with the provider.",
                NotificationRoutes.BookingStartOtp,
                Booking),

            // P-S10 — seen, but the provider still hasn't entered it.
            [NotificationTypes.BookingStartOtpNotShared] = new(
                "Share your OTP now",
                "The job completion time has passed and you haven't shared your OTP with {providerName}. Please share it, or you'll be marked as a no-show.",
                NotificationRoutes.BookingStartOtp,
                Booking),

            // V-S10
            [NotificationTypes.BookingStartOtpNotEntered] = new(
                "Ask for the customer's OTP",
                "The job completion time has passed and you haven't entered the customer's OTP. Please ask {parentName} for their OTP to start the job.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-S13
            [NotificationTypes.BookingCompletionTimePassed] = new(
                "Service completion time has passed",
                "{petName}'s {serviceName} completion time has passed. Please check with {providerName} whether you can pick up your pet.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-S14
            [NotificationTypes.BookingPickupOverdue] = new(
                "You're late to pick up {petName}",
                "You're late to pick up your pet! {providerName} is closing at {closingTime}. Please collect {petName}.",
                NotificationRoutes.BookingDetail,
                Booking),

            // V-S12
            [NotificationTypes.BookingNotMarkedComplete] = new(
                "You haven't marked the job complete",
                "It's past your business hours and {petName}'s {serviceName} still isn't marked complete. Please complete the job or report an issue.",
                NotificationRoutes.BookingDetail,
                Booking),

            // ================================================================
            // To the PROVIDER — the parent acted (V3 cards V-U*)
            // ================================================================

            // V-S1. Copy predates the V3 spec and is deliberately kept: it quotes the
            // real accept-by deadline, which is the EARLIER of BR-17 (24h) and BR-53
            // (2h before the service). The spec's "Respond within 24 hours" would be
            // wrong for a same-day booking. See BookingAcceptanceDeadline.
            [NotificationTypes.BookingRequested] = new(
                "New Service Booking",
                "{petName} · {serviceDate} at {startTime}. Please accept by {acceptBy}, else the booking will be removed.",
                NotificationRoutes.BookingRequests,
                Booking),

            // Night-stay twin: a stay has no start time, so the "time" slot is
            // the drop-off on the check-in day.
            [NotificationTypes.NightStayBookingRequested] = new(
                "New Service Booking",
                "{petName} · {checkInDate} at {dropOffTime}. Please accept by {acceptBy}, else the booking will be removed.",
                NotificationRoutes.BookingRequests,
                Booking),

            // V-U1
            [NotificationTypes.BookingCancelledByParent] = new(
                "Booking cancelled by customer",
                "{parentName} has cancelled {petName}'s {serviceName} scheduled for {serviceDate}.",
                NotificationRoutes.BookingDetail,
                Booking),

            // V-S2 — opens Payouts → Ignored Jobs, not the booking, so the provider
            // sees the cumulative cost of unanswered requests.
            [NotificationTypes.BookingExpiredForProvider] = new(
                "You missed a job opportunity",
                "You missed a job opportunity! {parentName}'s {serviceName} request for {petName} expired because it wasn't answered in time.",
                NotificationRoutes.IgnoredJobs,
                Booking),

            // V-U2
            [NotificationTypes.BookingModificationRequestedByParent] = new(
                "Customer requested a modification",
                "{parentName} has proposed a new timing for {petName}'s {serviceName}. Review it within 24 hours.",
                NotificationRoutes.BookingModification,
                Booking),

            // V-U3
            [NotificationTypes.BookingModificationAcceptedByParent] = new(
                "Modification confirmed",
                "{parentName} accepted your modification request. The new timing for {petName}'s {serviceName} is confirmed: {newServiceDate} at {newStartTime}. Your agenda has been updated.",
                NotificationRoutes.BookingDetail,
                Booking),

            // V-U4
            [NotificationTypes.BookingModificationDeclinedByParent] = new(
                "Modification declined",
                "{parentName} declined your modification request. The job stays as originally scheduled: {serviceDate} at {startTime}.",
                NotificationRoutes.BookingDetail,
                Booking),

            // ================================================================
            // To BOTH parties — defaults; per-app wording in AudienceOverrides
            // ================================================================

            // P-S2 / V-S3 — the 24-hour review window lapsed.
            [NotificationTypes.BookingModificationTimedOut] = new(
                "Modification request timed out",
                "The modification request on {petName}'s {serviceName} timed out. It stays as originally scheduled: {serviceDate} at {startTime}.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-S3 / V-S4 — the 2-hour cut-off bit first (short-notice bookings).
            [NotificationTypes.BookingModificationExpired] = new(
                "Modification request expired",
                "The modification request on {petName}'s {serviceName} has expired — modifications close 2 hours before the service is due. It stays as originally scheduled: {serviceDate} at {startTime}.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-S4 / V-S5
            [NotificationTypes.BookingReminderDayBefore] = new(
                "Service tomorrow",
                "{petName}'s {serviceName} is tomorrow at {startTime}. Don't forget to be there on time.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-S5 / V-S6
            [NotificationTypes.BookingReminderStartingSoon] = new(
                "Service starts soon",
                "{petName}'s {serviceName} starts in 5 minutes at {location}. Please be there on time.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-S6 / V-S7
            [NotificationTypes.BookingNotStartedHalfway] = new(
                "Has the service started?",
                "{petName}'s {serviceName} hasn't started yet. Is everything OK?",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-S7 / V-S8
            [NotificationTypes.BookingNotStartedWindowEnded] = new(
                "Service time has ended",
                "{petName}'s {serviceName} time has ended but the job still hasn't started. Is everything OK?",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-U9 / P-U10 — a party REPORTED the counterparty absent, so only the
            // counterparty is told. {absentParty} names who was missing, which keeps
            // one template correct in both directions.
            [NotificationTypes.BookingNoShowReported] = new(
                "Marked as no-show",
                "{petName}'s {serviceName} on {serviceDate} has been marked as a no-show — {absentParty} didn't attend. No payment is needed.",
                NotificationRoutes.BookingDetail,
                Booking),

            // P-S8 / V-S9 / P-S12 / V-S11 — the BR-38 sweep derived it, so both
            // parties are told. Same {absentParty} treatment.
            [NotificationTypes.BookingNoShowAutoSettled] = new(
                "Marked as no-show",
                "{petName}'s {serviceName} on {serviceDate} has been closed as a no-show — {absentParty} didn't attend before the provider's working hours ended. No payment is needed.",
                NotificationRoutes.BookingDetail,
                Booking),

            // ================================================================
            // Event tickets — not covered by the V3 services spec
            // ================================================================

            [NotificationTypes.EventBookingConfirmed] = new(
                "Tickets booked",
                "You booked {ticketCount} ticket(s) for {eventTitle}.",
                NotificationRoutes.EventBookingDetail,
                Event),

            [NotificationTypes.EventBookingPaymentSucceeded] = new(
                "Payment successful",
                "Your payment for {eventTitle} went through. Your tickets are confirmed.",
                NotificationRoutes.EventBookingDetail,
                Event),

            [NotificationTypes.EventBookingPaymentFailed] = new(
                "Payment failed",
                "We couldn't take payment for {eventTitle}. Your tickets aren't confirmed yet.",
                NotificationRoutes.EventBookingDetail,
                Event),

            [NotificationTypes.EventBookingCancelled] = new(
                "Tickets cancelled",
                "Your tickets for {eventTitle} have been cancelled.",
                NotificationRoutes.EventBookingDetail,
                Event),

            [NotificationTypes.EventTicketSold] = new(
                "Tickets sold",
                "{parentName} booked {ticketCount} ticket(s) for {eventTitle}.",
                NotificationRoutes.EventDashboard,
                Event),

            [NotificationTypes.EventTicketCancelled] = new(
                "Tickets cancelled",
                "{parentName} cancelled {ticketCount} ticket(s) for {eventTitle}. The seats are available again.",
                NotificationRoutes.EventDashboard,
                Event),

            // ================================================================
            // Modules not built yet — CONTRACT ONLY.
            // Nothing enqueues these. They exist so the mobile apps can code
            // against the payload now, and so wiring them later is a one-line
            // publisher call rather than a contract change.
            // ================================================================

            // P-U14 / V-U5. No messaging module exists in this backend.
            [NotificationTypes.MessageReceived] = new(
                "{senderName}",
                "Sent you a message.",
                NotificationRoutes.Conversation,
                NotificationCategories.Messaging),

            // P-S16 / V-S14. No invoicing exists. {issuedBy} is what differs
            // between the two apps: the provider's name on the parent's copy,
            // "Littersoft / Pawfront" on the provider's — so one template serves both.
            [NotificationTypes.InvoiceIssued] = new(
                "Invoice ready",
                "Your invoice {invoiceId} for {petName}'s {serviceName} is ready. Issued by {issuedBy} · {amount}. Tap to view.",
                NotificationRoutes.Invoice,
                Booking),

            // P-S15 / V-S13. No Helpline / ticket module exists. Goes to the party
            // who raised the ticket, never to both.
            [NotificationTypes.DisputeResolved] = new(
                "Dispute resolved",
                "Ticket {ticketId} for {petName}'s {serviceName} has been resolved. Tap to see the outcome.",
                NotificationRoutes.TicketDetail,
                Booking),

            // No campaign module exists. Copy is supplied whole by the caller
            // rather than templated, because campaign wording is the point of a
            // campaign — hence the bare pass-through.
            [NotificationTypes.PromotionalMessage] = new(
                "{title}",
                "{body}",
                NotificationRoutes.NotificationInbox,
                NotificationCategories.Promotional)
        };

    /// <summary>
    /// Per-app wording for events that are mirrored to both parties. The V3 spec
    /// gives these a card in each app with the same trigger but different copy —
    /// the parent is told to bring cash, the provider to ask for the OTP.
    ///
    /// Only the entries that genuinely differ are listed; anything absent falls
    /// back to <see cref="Templates"/>, so a both-parties notification never has
    /// to be duplicated just to exist.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Type, NotificationAudience Audience), NotificationTemplate>
        AudienceOverrides =
            new Dictionary<(string, NotificationAudience), NotificationTemplate>
            {
                // P-S4 — the parent needs to bring cash; the provider needs the address.
                [(NotificationTypes.BookingReminderDayBefore, NotificationAudience.PetParent)] = new(
                    "Service tomorrow",
                    "{petName}'s {serviceName} with {providerName} is tomorrow at {startTime}. Don't forget to be there on time — please keep {amount} in cash ready.",
                    NotificationRoutes.BookingDetail,
                    Booking),

                // V-S5
                [(NotificationTypes.BookingReminderDayBefore, NotificationAudience.Provider)] = new(
                    "Job tomorrow",
                    "{petName}'s {serviceName} for {parentName} is tomorrow at {startTime}, {location}. Don't forget to be there on time.",
                    NotificationRoutes.BookingDetail,
                    Booking),

                // P-S5
                [(NotificationTypes.BookingReminderStartingSoon, NotificationAudience.PetParent)] = new(
                    "Service starts soon",
                    "{petName}'s {serviceName} with {providerName} starts in 5 minutes at {location}. Please be there on time.",
                    NotificationRoutes.BookingDetail,
                    Booking),

                // V-S6 — the provider's next action is to collect the OTP.
                [(NotificationTypes.BookingReminderStartingSoon, NotificationAudience.Provider)] = new(
                    "Job starts soon",
                    "{petName}'s {serviceName} starts in 5 minutes. Please be there on time — ask {parentName} for their OTP to start the job.",
                    NotificationRoutes.BookingDetail,
                    Booking),

                // P-S6 — the parent is asking a question; V-S7 the provider is being prompted.
                [(NotificationTypes.BookingNotStartedHalfway, NotificationAudience.PetParent)] = new(
                    "Has the service started?",
                    "{petName}'s {serviceName} with {providerName} hasn't started yet. Is everything OK?",
                    NotificationRoutes.BookingDetail,
                    Booking),

                // V-S7
                [(NotificationTypes.BookingNotStartedHalfway, NotificationAudience.Provider)] = new(
                    "You haven't started the job",
                    "{petName}'s {serviceName} hasn't started yet. Is everything OK? Tap Proceed to Start when you're ready.",
                    NotificationRoutes.BookingDetail,
                    Booking),

                // V-S8 — last chance before the no-show, so it says so.
                [(NotificationTypes.BookingNotStartedWindowEnded, NotificationAudience.Provider)] = new(
                    "Service time has ended",
                    "{petName}'s {serviceName} time has ended but the job hasn't started. Proceed to Start stays available until the end of your working hours.",
                    NotificationRoutes.BookingDetail,
                    Booking),

                // P-S2 / V-S3 — "your booking" vs "the job".
                [(NotificationTypes.BookingModificationTimedOut, NotificationAudience.PetParent)] = new(
                    "Modification request timed out",
                    "The modification request on {petName}'s {serviceName} booking timed out. Your booking stays as originally scheduled: {serviceDate} at {startTime}.",
                    NotificationRoutes.BookingDetail,
                    Booking),

                // P-S3 / V-S4
                [(NotificationTypes.BookingModificationExpired, NotificationAudience.PetParent)] = new(
                    "Modification request expired",
                    "Your modification request for {petName}'s {serviceName} has expired — modifications close 2 hours before the service is due. Your booking stays as originally scheduled: {serviceDate} at {startTime}.",
                    NotificationRoutes.BookingDetail,
                    Booking)
            };

    /// <summary>
    /// The template for a type as worded for one app, falling back to the shared
    /// copy when that audience has no override.
    /// </summary>
    public static NotificationTemplate? Find(string notificationType, NotificationAudience audience)
    {
        if (AudienceOverrides.TryGetValue((notificationType, audience), out var audienceTemplate))
        {
            return audienceTemplate;
        }

        return Templates.TryGetValue(notificationType, out var template) ? template : null;
    }

    /// <summary>Every registered type — used by tests to assert full coverage.</summary>
    public static IReadOnlyCollection<string> RegisteredTypes => (IReadOnlyCollection<string>)Templates.Keys;
}
