namespace Pawfront.Application.Notifications;

/// <summary>
/// The stable machine keys carried as <c>data.type</c> in every push. The mobile
/// apps branch on these, so treat them as a wire contract: <b>never rename or
/// reword one</b> — add a new key and retire the old one instead.
///
/// Every key here must have an entry in <see cref="NotificationTemplateCatalog"/>,
/// which is what turns it into user-facing copy and a deep link.
/// </summary>
public static class NotificationTypes
{
    // --- Booking lifecycle, to the PET PARENT --------------------------------

    public const string BookingAccepted = "BOOKING_ACCEPTED";
    public const string BookingDeclined = "BOOKING_DECLINED";
    public const string BookingCancelledByProvider = "BOOKING_CANCELLED_BY_PROVIDER";
    /// <summary>Nobody accepted it in time (BR-17 24h, or BR-53 under 2h to the service).</summary>
    public const string BookingExpiredForParent = "BOOKING_EXPIRED_FOR_PARENT";
    /// <summary>Provider tapped "Start Job" — the parent must now show their start code.</summary>
    public const string BookingStartOtpIssued = "BOOKING_START_OTP_ISSUED";
    public const string BookingInProgress = "BOOKING_IN_PROGRESS";
    public const string BookingCompleted = "BOOKING_COMPLETED";
    public const string BookingPaid = "BOOKING_PAID";
    public const string BookingModificationRequestedByProvider = "BOOKING_MODIFICATION_REQUESTED_BY_PROVIDER";
    public const string BookingModificationAcceptedByProvider = "BOOKING_MODIFICATION_ACCEPTED_BY_PROVIDER";
    public const string BookingModificationDeclinedByProvider = "BOOKING_MODIFICATION_DECLINED_BY_PROVIDER";
    public const string BookingPrescriptionRecorded = "BOOKING_PRESCRIPTION_RECORDED";

    // --- Booking lifecycle, to the PROVIDER -----------------------------------

    /// <summary>A parent booked a single-day service — "New Service Booking".</summary>
    public const string BookingRequested = "BOOKING_REQUESTED";

    /// <summary>
    /// A parent booked a multi-night boarding stay. Separate from
    /// <see cref="BookingRequested"/> because a stay has no start/end time — it
    /// spans check-in to check-out — so it needs its own wording, and because it
    /// deep-links to a different entity.
    /// </summary>
    public const string NightStayBookingRequested = "NIGHT_STAY_BOOKING_REQUESTED";
    public const string BookingCancelledByParent = "BOOKING_CANCELLED_BY_PARENT";
    /// <summary>The provider ran out of time to accept — they lost the job.</summary>
    public const string BookingExpiredForProvider = "BOOKING_EXPIRED_FOR_PROVIDER";
    public const string BookingModificationRequestedByParent = "BOOKING_MODIFICATION_REQUESTED_BY_PARENT";
    public const string BookingModificationAcceptedByParent = "BOOKING_MODIFICATION_ACCEPTED_BY_PARENT";
    public const string BookingModificationDeclinedByParent = "BOOKING_MODIFICATION_DECLINED_BY_PARENT";

    // --- Booking lifecycle, to BOTH parties -----------------------------------

    /// <summary>An unanswered proposal lapsed at the 2-hour cutoff; the booking is CONFIRMED again (BR-30).</summary>
    public const string BookingModificationExpired = "BOOKING_MODIFICATION_EXPIRED";
    /// <summary>Six wrong start-codes cancelled the job.</summary>
    public const string BookingOtpAttemptsExceeded = "BOOKING_OTP_ATTEMPTS_EXCEEDED";

    // --- Event tickets --------------------------------------------------------

    /// <summary>To the buyer: their tickets are booked.</summary>
    public const string EventBookingConfirmed = "EVENT_BOOKING_CONFIRMED";
    /// <summary>To the buyer: the payment gateway reported the result.</summary>
    public const string EventBookingPaymentSucceeded = "EVENT_BOOKING_PAYMENT_SUCCEEDED";
    public const string EventBookingPaymentFailed = "EVENT_BOOKING_PAYMENT_FAILED";
    public const string EventBookingCancelled = "EVENT_BOOKING_CANCELLED";
    /// <summary>To the organiser: somebody bought tickets for their event.</summary>
    public const string EventTicketSold = "EVENT_TICKET_SOLD";
    /// <summary>To the organiser: an attendee cancelled, freeing their seat.</summary>
    public const string EventTicketCancelled = "EVENT_TICKET_CANCELLED";

    // --- Reminders before the service, to BOTH parties ------------------------

    /// <summary>T-24h: the service is tomorrow (V3 cards P-S4 / V-S5).</summary>
    public const string BookingReminderDayBefore = "BOOKING_REMINDER_DAY_BEFORE";

    /// <summary>T-5min: the service is about to start (P-S5 / V-S6).</summary>
    public const string BookingReminderStartingSoon = "BOOKING_REMINDER_STARTING_SOON";

    // --- The job never got under way, to BOTH parties -------------------------

    /// <summary>Half-way through the booked window with no "Start Job" (P-S6 / V-S7).</summary>
    public const string BookingNotStartedHalfway = "BOOKING_NOT_STARTED_HALFWAY";

    /// <summary>The whole booked window elapsed with no "Start Job" (P-S7 / V-S8).</summary>
    public const string BookingNotStartedWindowEnded = "BOOKING_NOT_STARTED_WINDOW_ENDED";

    // --- Start-code nudges ----------------------------------------------------

    /// <summary>
    /// To the PARENT, who has not yet opened their start code (P-S9). Distinct
    /// from <see cref="BookingStartOtpNotShared"/> by the OTP's SeenAtUtc stamp.
    /// </summary>
    public const string BookingStartOtpNotSeen = "BOOKING_START_OTP_NOT_SEEN";

    /// <summary>To the PARENT, who has seen the code but the provider still hasn't entered it (P-S10).</summary>
    public const string BookingStartOtpNotShared = "BOOKING_START_OTP_NOT_SHARED";

    /// <summary>To the PROVIDER, who still hasn't entered the parent's code (V-S10).</summary>
    public const string BookingStartOtpNotEntered = "BOOKING_START_OTP_NOT_ENTERED";

    // --- Pick-up and closure --------------------------------------------------

    /// <summary>To the PARENT: the job's end time passed and it's still running (P-S13).</summary>
    public const string BookingCompletionTimePassed = "BOOKING_COMPLETION_TIME_PASSED";

    /// <summary>To the PARENT: the provider is closing and the pet is still there (P-S14).</summary>
    public const string BookingPickupOverdue = "BOOKING_PICKUP_OVERDUE";

    /// <summary>To the PROVIDER: past closing time with the job still not completed (V-S12).</summary>
    public const string BookingNotMarkedComplete = "BOOKING_NOT_MARKED_COMPLETE";

    // --- No-show, split by who decided it -------------------------------------

    /// <summary>
    /// A party REPORTED the counterparty absent, so only the counterparty is told
    /// — the reporter tapped it and saw the result (P-U9 / P-U10 / V-U-side).
    /// </summary>
    public const string BookingNoShowReported = "BOOKING_NO_SHOW_REPORTED";

    /// <summary>
    /// The BR-38 sweep derived the no-show, so BOTH parties are told — nobody
    /// tapped anything (P-S12 / V-S11, P-S8 / V-S9).
    /// </summary>
    public const string BookingNoShowAutoSettled = "BOOKING_NO_SHOW_AUTO_SETTLED";

    // --- Modification expiry, split by which deadline bit ---------------------

    /// <summary>
    /// The 24-hour review window lapsed (P-S2 / V-S3). Separate from
    /// <see cref="BookingModificationExpired"/> so the copy can name the reason;
    /// exactly one of the two fires per proposal.
    /// </summary>
    public const string BookingModificationTimedOut = "BOOKING_MODIFICATION_TIMED_OUT";

    // --- Modules not built yet: contract only, nothing enqueues these ---------

    /// <summary>
    /// Chat message (P-U14 / V-U5). <b>Wired 2026-08-09.</b> Enqueued by
    /// <c>Chat.CommitMessageAppend</c> — and only when the recipient has no live
    /// connection viewing that thread, so an open chat never buzzes.
    ///
    /// Unlike every other type here it is normally sent by <c>Pawfront.ChatApi</c>
    /// itself rather than by the scheduled dispatcher: its row is enqueued
    /// pre-claimed so the timer skips it, and the dispatcher only takes over if
    /// the chat host dies mid-send. See <c>docs/chat.md</c>.
    /// </summary>
    public const string MessageReceived = "MESSAGE_RECEIVED";

    /// <summary>
    /// Invoice ready (P-S16 / V-S14).
    ///
    /// <b>The PROVIDER half is wired (2026-08-11)</b> — enqueued by
    /// <c>Booking.MarkBookingPaid</c> and its night-stay twin when the provider
    /// records the cash, which is the moment their invoice for the job is settled.
    /// It deep-links to the booking rather than to a document, since there is none.
    ///
    /// <b>The PARENT half has no trigger</b>: issuing them an invoice needs an
    /// invoicing module, which does not exist. Their receipt for the same event is
    /// <see cref="BookingPaid"/>.
    /// </summary>
    public const string InvoiceIssued = "INVOICE_ISSUED";

    /// <summary>
    /// Support closed a ticket (P-S15 / V-S13), to the raiser only. <b>No trigger</b>
    /// — the Helpline / ticket module does not exist.
    /// </summary>
    public const string DisputeResolved = "DISPUTE_RESOLVED";

    /// <summary>
    /// Marketing / campaign push. <b>No trigger</b> — there is no campaign module.
    /// Its copy is supplied whole by the caller rather than templated, so the
    /// template here is a pass-through of {title} / {body}.
    /// </summary>
    public const string PromotionalMessage = "PROMOTIONAL_MESSAGE";
}

/// <summary>
/// The coarse bucket carried as <c>data.category</c> — what KIND of thing the
/// notification is about, as opposed to <see cref="NotificationTypes"/>, which
/// says exactly which event occurred.
///
/// The mobile apps use it to pick a top-level destination (and, if they want, a
/// notification channel or an inbox filter) without having to know every type:
/// an unrecognised type in a known category still routes somewhere sensible,
/// which is what makes adding a type non-breaking.
///
/// Same rule as the types: a wire contract, never renamed or reworded.
/// </summary>
public static class NotificationCategories
{
    /// <summary>A service booking — single-day or night-stay. Pairs with <c>bookingId</c>.</summary>
    public const string Booking = "BOOKING";

    /// <summary>An event or its ticket booking. Pairs with <c>eventId</c>.</summary>
    public const string Event = "EVENT";

    /// <summary>A chat message.</summary>
    public const string Messaging = "MESSAGING";

    /// <summary>Marketing / campaign content, tied to no entity.</summary>
    public const string Promotional = "PROMOTIONAL";
}

/// <summary>
/// Keys used inside the FCM <c>data</c> payload and inside the outbox
/// <c>DataJson</c> template parameters.
///
/// <b>FCM requires every data value to be a string</b> — there is no way to send
/// a nested object — so anything structured is flattened into these keys rather
/// than being serialised as an object.
/// </summary>
public static class NotificationDataKeys
{
    // --- Envelope keys the mobile apps read on every notification ------------

    /// <summary>Payload contract version, so the apps can evolve the shape safely.</summary>
    public const string Version = "v";
    public const string Type = "type";
    public const string Route = "route";
    public const string EntityType = "entityType";
    public const string EntityId = "entityId";
    public const string NotificationId = "notificationId";
    public const string SentAtUtc = "sentAtUtc";

    /// <summary>Current value of <see cref="Version"/>.</summary>
    public const string CurrentVersion = "1";

    // --- The canonical id block ----------------------------------------------
    // Present on EVERY notification, so the app can read a field without first
    // checking whether this particular type carries it. A field that does not
    // apply is sent as an EMPTY STRING rather than omitted: "absent key" and
    // "key I forgot to set" are indistinguishable to the client, and an empty
    // string makes the contract self-describing. See NotificationPayloadBuilder.

    /// <summary>One of <see cref="NotificationCategories"/> — the coarse bucket.</summary>
    public const string Category = "category";

    /// <summary>The pet parent this notification concerns; empty when none.</summary>
    public const string ParentId = "parentId";

    /// <summary>The provider this notification concerns; empty when none.</summary>
    public const string ProviderId = "providerId";

    /// <summary>The pet the booking is for; empty for Custom walk-ins and non-booking types.</summary>
    public const string PetId = "petId";

    /// <summary>
    /// "true" / "false" — whether <see cref="BookingId"/> points at
    /// <c>Booking.NightStayBookings</c> rather than <c>Booking.Bookings</c>. The
    /// two tables share no id space, so the app cannot tell them apart from the
    /// id alone. Empty for non-booking types.
    /// </summary>
    public const string IsNightStay = "isNightStay";

    /// <summary>
    /// The payment/payout reference on the booking row, for payment-related
    /// notifications; empty otherwise.
    /// </summary>
    public const string PayoutId = "payoutId";

    // --- Template parameters (also forwarded to the app inside `data`) -------

    public const string BookingId = "bookingId";
    /// <summary>
    /// "SingleDay" or "NightStay". Retained alongside the newer
    /// <see cref="IsNightStay"/> boolean, which is what the mobile contract reads;
    /// this one predates it and is still sent so nothing already built breaks.
    /// </summary>
    public const string BookingType = "bookingType";
    public const string JobId = "jobId";
    public const string EventId = "eventId";
    public const string EventBookingId = "eventBookingId";
    public const string ProviderName = "providerName";
    public const string ParentName = "parentName";
    public const string PetName = "petName";
    public const string EventTitle = "eventTitle";

    // --- Times: raw instants in, display strings out --------------------------
    // Producers emit the *Utc keys below; NotificationRenderer converts them to
    // the recipient's timezone (Swiss today) and derives the display keys from
    // them. See NotificationLocalTime for why the split exists. Both halves reach
    // the app: the display strings match the body it is showing, the instants let
    // it re-format or count down on its own.

    /// <summary>
    /// When the service begins, as a UTC instant — <c>BookingDate + StartTime</c>,
    /// or <c>CheckInDate + DropOffTime</c> for a stay. The same instant BR-01 /
    /// BR-53 and the modification cutoff all measure against.
    /// </summary>
    public const string ServiceStartUtc = "serviceStartUtc";

    /// <summary>
    /// Night-stay only: when the stay ends, as a UTC instant —
    /// <c>CheckOutDate + PickUpTime</c>.
    /// </summary>
    public const string CheckOutUtc = "checkOutUtc";

    /// <summary>Local date of <see cref="ServiceStartUtc"/> ("5 Aug").</summary>
    public const string ServiceDate = "serviceDate";

    /// <summary>
    /// Local date of <see cref="ServiceStartUtc"/> carrying its year
    /// ("5 Aug 2026"). Used by the two booking-request notifications, which are
    /// the provider's decision prompt rather than a nudge about something they
    /// already know is coming — the rest of the copy stays on the shorter
    /// <see cref="ServiceDate"/>.
    /// </summary>
    public const string ServiceDateWithYear = "serviceDateWithYear";

    /// <summary>Local time of <see cref="ServiceStartUtc"/> ("14:00").</summary>
    public const string StartTime = "startTime";

    /// <summary>Night-stay alias of <see cref="ServiceDate"/>.</summary>
    public const string CheckInDate = "checkInDate";

    /// <summary>Local date of <see cref="CheckOutUtc"/>.</summary>
    public const string CheckOutDate = "checkOutDate";

    /// <summary>
    /// Hand-over time on the check-in day, for night-stay bookings — the
    /// night-stay alias of <see cref="StartTime"/>.
    /// </summary>
    public const string DropOffTime = "dropOffTime";

    /// <summary>
    /// Display form of the accept-by deadline ("4 Aug 10:00"), rendered into the
    /// notification body in the recipient's timezone. Derived from
    /// <see cref="AcceptByUtc"/>; carries its date as well as its time because the
    /// deadline can fall on a different day from the service.
    /// </summary>
    public const string AcceptBy = "acceptBy";

    /// <summary>
    /// The accept-by deadline as a UTC instant, so the app can show a live
    /// countdown rather than only the rendered <see cref="AcceptBy"/> string.
    /// </summary>
    public const string AcceptByUtc = "acceptByUtc";

    /// <summary>
    /// When the booking was created, as a UTC instant. Emitted so the renderer
    /// can derive <see cref="RespondWithin"/> — the length of the window, which
    /// is what a provider acts on — from the same pair of instants the app has.
    /// </summary>
    public const string CreatedAtUtc = "createdAtUtc";

    /// <summary>
    /// How long the provider has to answer, as human copy ("24 hours",
    /// "2 hours 15 minutes"). Derived from <see cref="AcceptByUtc"/> minus
    /// <see cref="CreatedAtUtc"/>, so it collapses to a clean "24 hours" when
    /// BR-17 governs and reports the real, shorter window when BR-53 does.
    /// </summary>
    public const string RespondWithin = "respondWithin";

    /// <summary>
    /// When an open modification proposal stops being answerable, as an instant.
    /// The EARLIER of the two deadlines
    /// <c>Booking.RevertExpiredModificationRequests</c> enforces: the 24-hour
    /// review window, and the 2-hour pre-service cutoff. Quoting a flat 24 hours
    /// would promise a review window that outlives the service itself on a
    /// short-notice booking.
    /// </summary>
    public const string ReviewByUtc = "reviewByUtc";

    /// <summary>
    /// When the proposal was made. Emitted so <see cref="ReviewWithin"/> can be
    /// derived from the same pair of readings the app has, and measured from the
    /// request rather than from send time — see
    /// <see cref="CreatedAtUtc"/> for why that reads better.
    /// </summary>
    public const string RequestedAtUtc = "requestedAtUtc";

    /// <summary>
    /// How long the counterparty has to answer a modification, as human copy
    /// ("24 hours", "3 hours 20 minutes"). Derived from <see cref="ReviewByUtc"/>
    /// minus <see cref="RequestedAtUtc"/>, so it collapses to a clean "24 hours"
    /// whenever the service is far enough out and reports the real, shorter window
    /// when the 2-hour cutoff governs.
    /// </summary>
    public const string ReviewWithin = "reviewWithin";

    /// <summary>
    /// What was booked, as a customer-facing name — the specific grooming menu
    /// item where the category has one ("Nails Clipping"), otherwise the bookable
    /// service ("Day Care", "Vet Appointment"). Built by
    /// <see cref="BookingServiceLabel"/>.
    /// </summary>
    public const string ServiceName = "serviceName";

    /// <summary>
    /// The catalog row's <c>ServiceType</c> (<c>DayCare</c>, <c>VetAppointment</c>, …).
    /// Sent so the renderer can derive <see cref="ServiceName"/> when the enqueuer
    /// could not: a T-SQL sweep has no access to the grooming display-name catalog,
    /// which lives in C#, so it emits the raw type + item code and lets
    /// <see cref="BookingServiceLabel"/> do the naming at render time.
    /// </summary>
    public const string ServiceType = "serviceType";

    /// <summary>The grooming menu item's code, where the booking names one.</summary>
    public const string ServiceItemCode = "serviceItemCode";
    public const string Amount = "amount";
    public const string TicketCount = "ticketCount";
    public const string AbsentParty = "absentParty";
    public const string Reason = "reason";

    /// <summary>
    /// The proposed start of the service on a modification, as a UTC instant. Same
    /// arithmetic as <see cref="ServiceStartUtc"/>, applied to the staged proposal.
    /// </summary>
    public const string NewServiceStartUtc = "newServiceStartUtc";

    /// <summary>
    /// Night-stay only: the proposed end of the stay on a modification, as a UTC
    /// instant.
    /// </summary>
    public const string NewCheckOutUtc = "newCheckOutUtc";

    /// <summary>Proposed date on a modification — local date of <see cref="NewServiceStartUtc"/>.</summary>
    public const string NewServiceDate = "newServiceDate";

    /// <summary>Proposed time on a modification — local time of <see cref="NewServiceStartUtc"/>.</summary>
    public const string NewStartTime = "newStartTime";

    /// <summary>
    /// Night-stay only: proposed check-out — local date of
    /// <see cref="NewCheckOutUtc"/>.
    /// </summary>
    public const string NewCheckOutDate = "newCheckOutDate";

    /// <summary>
    /// Where the service happens, for the T-5min reminder. Free-text address line;
    /// absent when the booking's location can't be resolved.
    /// </summary>
    public const string Location = "location";

    /// <summary>
    /// When the provider closes on the service date, as a UTC instant. An instant
    /// rather than a bare time-of-day because converting a clock time to the
    /// recipient's zone needs the date it falls on.
    /// </summary>
    public const string ClosingAtUtc = "closingAtUtc";

    /// <summary>
    /// The provider's closing time, for the pick-up nudges — local time of
    /// <see cref="ClosingAtUtc"/>.
    /// </summary>
    public const string ClosingTime = "closingTime";

    // --- Parameters for the not-yet-built modules -----------------------------

    /// <summary>Support ticket reference, e.g. "TKT-00123".</summary>
    public const string TicketId = "ticketId";

    /// <summary>Invoice reference shown in the notification body.</summary>
    public const string InvoiceId = "invoiceId";

    /// <summary>
    /// Who the invoice is issued under — the provider's name on the parent's copy,
    /// "Littersoft / Pawfront" on the provider's.
    /// </summary>
    public const string IssuedBy = "issuedBy";

    /// <summary>Chat: who sent the message.</summary>
    public const string SenderName = "senderName";

    /// <summary>Chat: the thread to open.</summary>
    public const string ConversationId = "conversationId";

    /// <summary>Promotional: caller-supplied title, since campaign copy isn't templated.</summary>
    public const string Title = "title";

    /// <summary>Promotional: caller-supplied body.</summary>
    public const string Body = "body";
}

/// <summary>Values for <see cref="NotificationDataKeys.EntityType"/>.</summary>
public static class NotificationEntityTypes
{
    public const string Booking = "Booking";
    public const string NightStayBooking = "NightStayBooking";
    public const string Event = "Event";
    public const string EventBooking = "EventBooking";

    // --- Entities of the modules that aren't built yet ------------------------
    public const string Conversation = "Conversation";
    public const string Invoice = "Invoice";
    public const string Ticket = "Ticket";
}
