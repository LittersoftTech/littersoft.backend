namespace Pawfront.Application.Notifications;

/// <summary>Rendered copy and destination; Code identifies the Services V4 / Events V3 card.</summary>
public sealed record NotificationTemplate(
    string TitleTemplate, string BodyTemplate, string Route, string Category, string? Code = null);

/// <summary>
/// Approved Services V4, Events V3 and identification copy. Stable wire types are
/// preserved; audience and scenario select a card without duplicating deliveries.
/// </summary>
public static class NotificationTemplateCatalog
{
    private static readonly IReadOnlyDictionary<string, NotificationTemplate> Cards =
        new Dictionary<string, NotificationTemplate>(StringComparer.Ordinal)
        {
            ["P-U1"] = new("Booking Confirmed", "{providerName} accepted your {serviceName} booking. Tap to view!", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-U1"),
            ["P-U2"] = new("Booking Request Declined", "{providerName} declined your {serviceName} booking. Try another slot or provider.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-U2"),
            ["P-U3"] = new("Booking Cancelled by Provider", "{providerName} cancelled your {serviceName} booking. Try another slot or provider.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-U3"),
            ["P-S1"] = new("Job Request Expired", "{providerName} didn’t respond in time, so your {serviceName} request has expired. Please re-book.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-S1"),
            ["P-U7"] = new("Share your OTP", "{providerName} has started {serviceName}. Tap to view and share your OTP with the provider.", NotificationRoutes.BookingStartOtp, NotificationCategories.Booking, "P-U7"),
            ["P-U8"] = new("Your Job Has Started", "Thank you for the OTP! We’ll let you know once the service is complete.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-U8"),
            ["P-U11"] = new("Service Completed! Pay & Collect {petName}", "{providerName} has completed {serviceName}. Please collect your pet and pay the provider.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-U11"),
            ["P-U12"] = new("Payment Requested", "Please pay CHF {amount} in cash to {providerName}.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-U12"),
            ["P-U13"] = new("Thank You for Your Payment", "{providerName} has confirmed receiving CHF {amount} in cash for {serviceName}.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-U13"),
            ["P-U15"] = new("Payment Not Received", "{providerName} has reported that you have not made the payment for {serviceName}. Please make the payment to avoid receiving a red flag.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-U15"),
            ["P-U4"] = new("Provider Requested a Modification", "{providerName} has proposed a new timing for your {serviceName} booking. Tap to review!", NotificationRoutes.BookingModification, NotificationCategories.Booking, "P-U4"),
            ["P-U5"] = new("Modification Confirmed", "{providerName} accepted your modification request for {serviceName}. Tap to view!", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-U5"),
            ["P-U6"] = new("Modification Declined", "{providerName} declined your modification request for {serviceName}. Your booking stays as originally scheduled.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-U6"),
            ["P-S11"] = new("Booking Cancelled: OTP Not Verified", "The OTP for your {serviceName} booking couldn’t be verified, so your booking is cancelled.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-S11"),
            ["P-S9"] = new("You’re Late for Your Appointment!", "Tap to view and share your OTP with the provider.", NotificationRoutes.BookingStartOtp, NotificationCategories.Booking, "P-S9"),
            ["P-S10"] = new("Share Your OTP Now", "Please share your OTP with {providerName} or you’ll be marked as a no-show.", NotificationRoutes.BookingStartOtp, NotificationCategories.Booking, "P-S10"),
            ["V-S10"] = new("Ask for the Customer’s OTP", "Please enter {parentName}'s OTP to start the job.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "V-S10"),
            ["P-S13"] = new("Service Completion Time Has Passed", "Your {serviceName} appointment completion time has passed. Please chat with {providerName} to check whether you can pick up your pet.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-S13"),
            ["P-S14"] = new("You’re Late to Pick Up {petName}", "{providerName}’s shop is closing. Please collect {petName}.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-S14"),
            ["V-S12"] = new("Please Complete The Job", "{parentName}’s {serviceName} job still isn’t marked complete. Please mark the job complete or report an issue.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "V-S12"),
            ["V-S15"] = new("Please Complete The Job", "{parentName}’s {serviceName} job still isn’t marked complete. Please mark the job complete.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "V-S15"),
            ["V-S1"] = new("New Job Request Received", "{parentName} has requested {serviceName} on {serviceDate}. Tap to review!", NotificationRoutes.BookingRequests, NotificationCategories.Booking, "V-S1"),
            ["V-U1"] = new("Booking Cancelled by Customer", "{parentName} has cancelled the {serviceName} appointment scheduled for {serviceDate}.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "V-U1"),
            ["V-S2"] = new("You Missed a Job Opportunity", "{parentName}’s {serviceName} request expired because you didn't answer in time.", NotificationRoutes.IgnoredJobs, NotificationCategories.Booking, "V-S2"),
            ["V-U2"] = new("Customer Requested a Modification", "{parentName} has proposed a new timing for {serviceName} appointment. Tap to review!", NotificationRoutes.BookingModification, NotificationCategories.Booking, "V-U2"),
            ["V-U3"] = new("Modification Confirmed", "{parentName} accepted your modification request for {serviceName}. Your Agenda has been updated.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "V-U3"),
            ["V-U4"] = new("Modification Declined", "{parentName} declined your modification request for {serviceName}. The job stays in your agenda as originally scheduled.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "V-U4"),
            ["P-S2"] = new("Modification Request Expired", "Your modification request for {serviceName} has expired. Your booking stays as originally scheduled.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-S2"),
            ["V-S3"] = new("Modification Request Expired", "Your modification request for {parentName}'s {serviceName} has expired. The job stays in your agenda as originally scheduled.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "V-S3"),
            ["P-S4"] = new("Service Tomorrow", "Don't forget your {serviceName} booking with {providerName} tomorrow. Be there on time.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-S4"),
            ["V-S5"] = new("Job Tomorrow", "Don't forget your {serviceName} booking with {parentName} tomorrow. Be there on time.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "V-S5"),
            ["P-S5"] = new("Service Starts Soon", "Your {serviceName} booking with {providerName} starts soon. Please be there on time.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-S5"),
            ["V-S6"] = new("Job Starts Soon", "{parentName}’s {serviceName} starts soon. Please be there on time.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "V-S6"),
            ["P-S6"] = new("Has the Service Started?", "Your {serviceName} with {providerName} hasn’t started yet. Is everything OK?", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-S6"),
            ["V-S7"] = new("You Haven’t Started the Job", "{parentName}’s {serviceName} hasn’t started yet. Tap to start the job.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "V-S7"),
            ["P-S7"] = new("Service Time Has Ended", "Your {serviceName} time has ended but the job still hasn’t started. Is everything OK?", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-S7"),
            ["V-S8"] = new("Service Time Has Ended", "{parentName}’s {serviceName} time has ended but the job hasn’t started. Is everything OK?", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "V-S8"),
            ["P-U9"] = new("You're marked as No-Show", "{providerName} marked you as a no-show for {serviceName} as you did not arrive on time.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-U9"),
            ["P-U10"] = new("You're marked as No-Show", "{providerName} marked you as a no-show for {serviceName} as you did not share the OTP with them.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-U10"),
            ["P-S8"] = new("Provider Didn’t Start the Service", "{providerName} didn’t start your {serviceName} as per the booking time. Book another provider.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-S8"),
            ["V-S9"] = new("You're marked as No-Show", "You did not start {parentName}’s {serviceName} job within your working hours. You have been marked as no-show.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "V-S9"),
            ["P-S12"] = new("You're marked as No-Show", "You were marked as a no-show for the {serviceName} booking with {providerName} as you did not arrive for the service.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "P-S12"),
            ["V-S11"] = new("Customer No-Show", "{parentName} has been marked as No Show as they did not arrive for the {serviceName} appointment today.", NotificationRoutes.BookingDetail, NotificationCategories.Booking, "V-S11"),
            ["P-U14"] = new("{providerName}", "Sent you a message.", NotificationRoutes.Conversation, NotificationCategories.Messaging, "P-U14"),
            ["V-U5"] = new("{parentName}", "Sent you a message.", NotificationRoutes.Conversation, NotificationCategories.Messaging, "V-U5"),
            ["P-S16"] = new("Invoice Ready", "Your invoice issued by {providerName} for {serviceName} is ready. Tap to view.", NotificationRoutes.Invoice, NotificationCategories.Booking, "P-S16"),
            ["V-S14"] = new("Pawfront Invoice Issued", "Pawfront has issued you an invoice for {parentName}'s service. Tap to view.", NotificationRoutes.Invoice, NotificationCategories.Booking, "V-S14"),
            ["P-S15"] = new("Dispute Resolved", "Your ticket for {serviceName} with {providerName} has been resolved. Tap to see the outcome.", NotificationRoutes.TicketDetail, NotificationCategories.Booking, "P-S15"),
            ["V-S13"] = new("Dispute Resolved", "Your ticket for {serviceName} with {parentName} has been resolved. Tap to see the outcome.", NotificationRoutes.TicketDetail, NotificationCategories.Booking, "V-S13"),
            ["A-U2"] = new("Thank You for Your Payment", "{hostName} has confirmed receiving CHF {amount} for {eventTitle}.", NotificationRoutes.EventDetail, NotificationCategories.Event, "A-U2"),
            ["A-U3"] = new("Event Payment Pending", "{hostName} marked your CHF {amount} payment for {eventTitle} as pending. Please check with the host.", NotificationRoutes.EventDetail, NotificationCategories.Event, "A-U3"),
            ["A-U1"] = new("Event Cancelled by Host", "{hostName} cancelled {eventTitle} scheduled for {serviceDate}.", NotificationRoutes.EventDetail, NotificationCategories.Event, "A-U1"),
            ["A-S4"] = new("A Spot Just Opened Up", "A spot opened up for {eventTitle}. Book now before it’s gone!", NotificationRoutes.EventDetail, NotificationCategories.Event, "A-S4"),
            ["A-S1"] = new("Be There For The Event Tomorrow", "Don’t forget to be there on time for {eventTitle} tomorrow.", NotificationRoutes.EventMyTickets, NotificationCategories.Event, "A-S1"),
            ["A-S2"] = new("Event Starts Soon", "{eventTitle} starts in 1 hour. Please be there on time.", NotificationRoutes.EventMyTickets, NotificationCategories.Event, "A-S2"),
            ["A-S5"] = new("Thanks for Attending!", "We hope you and your pet enjoyed {eventTitle}. See you at the next one!", NotificationRoutes.EventDetail, NotificationCategories.Event, "A-S5"),
            ["A-S6"] = new("You Missed the Event", "Your attendance at {eventTitle} wasn’t recorded, so you’re marked as a no-show. This counts towards a red flag.", NotificationRoutes.EventDetail, NotificationCategories.Event, "A-S6"),
            ["A-S7"] = new("Your Events Profile Is Flagged", "Your last two event bookings were cancelled late or missed, so your Events profile has a red flag for 30 days. Tap to see how to remove it.", NotificationRoutes.EventAccountFlagged, NotificationCategories.Event, "A-S7"),
            ["A-S8"] = new("Red Flag Removed", "Congratulations! The red flag is removed from your Events profile. You can again book up to 5 events at a time and host your own events.", NotificationRoutes.Events, NotificationCategories.Event, "A-S8"),
            ["A-S9"] = new("Issue Resolved", "Your ticket for {eventTitle} has been resolved. Tap to see the outcome.", NotificationRoutes.TicketDetail, NotificationCategories.Event, "A-S9"),
            ["H-U1"] = new("First Booking For Your Event!", "Congratulations! {parentName} booked the first ticket for {eventTitle}. Your event details can no longer be changed.", NotificationRoutes.EventMyEvent, NotificationCategories.Event, "H-U1"),
            ["H-U4"] = new("Your Event Is Editable Again", "All bookings for {eventTitle} were cancelled. You can edit your event details again.", NotificationRoutes.EventMyEvent, NotificationCategories.Event, "H-U4"),
            ["H-S1"] = new("Your Event Is Sold Out!", "Congratulations! All {seats} seats for {eventTitle} are booked.", NotificationRoutes.EventAttendees, NotificationCategories.Event, "H-S1"),
            ["H-S2"] = new("Get Ready For Your Event Tomorrow", "{attendees} {attendeeVerb} booked for {eventTitle}. Good luck!", NotificationRoutes.EventPawPrints, NotificationCategories.Event, "H-S2"),
            ["H-S3"] = new("Your Event Starts Soon!", "{attendees} {attendeeVerb} coming. Bookings are now closed.", NotificationRoutes.EventAttendees, NotificationCategories.Event, "H-S3"),
            ["H-S4"] = new("Your Event Is Live", "Record attendance and cash received in the Attendees tab as guests arrive.", NotificationRoutes.EventAttendees, NotificationCategories.Event, "H-S4"),
            ["H-S4-Free"] = new("Your Event Is Live", "{eventTitle} has started. Check who’s coming in the Attendees tab.", NotificationRoutes.EventAttendees, NotificationCategories.Event, "H-S4-Free"),
            ["H-S5"] = new("Record Remaining Attendance and Cash", "Please record the remaining {attendees} by midnight. Unrecorded attendees are marked as no-show.", NotificationRoutes.EventAttendees, NotificationCategories.Event, "H-S5"),
            ["H-S6"] = new("Thank You for Hosting!", "{attendees} joined your event. You received CHF {totalAmount}. Congratulations!", NotificationRoutes.EventPawPrints, NotificationCategories.Event, "H-S6"),
            ["H-S6-Free"] = new("Thank You for Hosting!", "{attendees} joined your event. Congratulations!", NotificationRoutes.EventPawPrints, NotificationCategories.Event, "H-S6-Free"),
            ["ID-1"] = new("ID added", "Your ID has been accepted. Your ID Added badge is now visible.", NotificationRoutes.Identity, "Identity", "ID-1"),
            ["ID-2"] = new("ID not approved", "Your ID was not approved. Open your ID to see the reason and upload a replacement.", NotificationRoutes.Identity, "Identity", "ID-2"),
        };

    private static readonly IReadOnlyDictionary<(string Type, NotificationAudience Audience), string> AudienceCodes =
        new Dictionary<(string, NotificationAudience), string>
        {
            [(NotificationTypes.BookingAccepted, NotificationAudience.PetParent)] = "P-U1",
            [(NotificationTypes.BookingDeclined, NotificationAudience.PetParent)] = "P-U2",
            [(NotificationTypes.BookingCancelledByProvider, NotificationAudience.PetParent)] = "P-U3",
            [(NotificationTypes.BookingExpiredForParent, NotificationAudience.PetParent)] = "P-S1",
            [(NotificationTypes.BookingStartOtpIssued, NotificationAudience.PetParent)] = "P-U7",
            [(NotificationTypes.BookingInProgress, NotificationAudience.PetParent)] = "P-U8",
            [(NotificationTypes.BookingCompleted, NotificationAudience.PetParent)] = "P-U11",
            [(NotificationTypes.BookingPaid, NotificationAudience.PetParent)] = "P-U13",
            [(NotificationTypes.BookingCashNotReceived, NotificationAudience.PetParent)] = "P-U15",
            [(NotificationTypes.BookingModificationRequestedByProvider, NotificationAudience.PetParent)] = "P-U4",
            [(NotificationTypes.BookingModificationAcceptedByProvider, NotificationAudience.PetParent)] = "P-U5",
            [(NotificationTypes.BookingModificationDeclinedByProvider, NotificationAudience.PetParent)] = "P-U6",
            [(NotificationTypes.BookingOtpAttemptsExceeded, NotificationAudience.PetParent)] = "P-S11",
            [(NotificationTypes.BookingStartOtpNotSeen, NotificationAudience.PetParent)] = "P-S9",
            [(NotificationTypes.BookingStartOtpNotShared, NotificationAudience.PetParent)] = "P-S10",
            [(NotificationTypes.BookingStartOtpNotEntered, NotificationAudience.Provider)] = "V-S10",
            [(NotificationTypes.BookingCompletionTimePassed, NotificationAudience.PetParent)] = "P-S13",
            [(NotificationTypes.BookingPickupOverdue, NotificationAudience.PetParent)] = "P-S14",
            [(NotificationTypes.BookingNotMarkedComplete, NotificationAudience.Provider)] = "V-S12",
            [(NotificationTypes.BookingRequested, NotificationAudience.Provider)] = "V-S1",
            [(NotificationTypes.NightStayBookingRequested, NotificationAudience.Provider)] = "V-S1",
            [(NotificationTypes.BookingCancelledByParent, NotificationAudience.Provider)] = "V-U1",
            [(NotificationTypes.BookingExpiredForProvider, NotificationAudience.Provider)] = "V-S2",
            [(NotificationTypes.BookingModificationRequestedByParent, NotificationAudience.Provider)] = "V-U2",
            [(NotificationTypes.BookingModificationAcceptedByParent, NotificationAudience.Provider)] = "V-U3",
            [(NotificationTypes.BookingModificationDeclinedByParent, NotificationAudience.Provider)] = "V-U4",
            [(NotificationTypes.BookingModificationTimedOut, NotificationAudience.PetParent)] = "P-S2",
            [(NotificationTypes.BookingModificationExpired, NotificationAudience.PetParent)] = "P-S2",
            [(NotificationTypes.BookingModificationTimedOut, NotificationAudience.Provider)] = "V-S3",
            [(NotificationTypes.BookingModificationExpired, NotificationAudience.Provider)] = "V-S3",
            [(NotificationTypes.BookingReminderDayBefore, NotificationAudience.PetParent)] = "P-S4",
            [(NotificationTypes.BookingReminderDayBefore, NotificationAudience.Provider)] = "V-S5",
            [(NotificationTypes.BookingReminderStartingSoon, NotificationAudience.PetParent)] = "P-S5",
            [(NotificationTypes.BookingReminderStartingSoon, NotificationAudience.Provider)] = "V-S6",
            [(NotificationTypes.BookingNotStartedHalfway, NotificationAudience.PetParent)] = "P-S6",
            [(NotificationTypes.BookingNotStartedHalfway, NotificationAudience.Provider)] = "V-S7",
            [(NotificationTypes.BookingNotStartedWindowEnded, NotificationAudience.PetParent)] = "P-S7",
            [(NotificationTypes.BookingNotStartedWindowEnded, NotificationAudience.Provider)] = "V-S8",
            [(NotificationTypes.BookingNoShowReported, NotificationAudience.PetParent)] = "P-U9",
            [(NotificationTypes.BookingNoShowAutoSettled, NotificationAudience.PetParent)] = "P-S8",
            [(NotificationTypes.BookingNoShowAutoSettled, NotificationAudience.Provider)] = "V-S9",
            [(NotificationTypes.MessageReceived, NotificationAudience.PetParent)] = "P-U14",
            [(NotificationTypes.MessageReceived, NotificationAudience.Provider)] = "V-U5",
            [(NotificationTypes.InvoiceIssued, NotificationAudience.PetParent)] = "P-S16",
            [(NotificationTypes.InvoiceIssued, NotificationAudience.Provider)] = "V-S14",
            [(NotificationTypes.DisputeResolved, NotificationAudience.PetParent)] = "P-S15",
            [(NotificationTypes.DisputeResolved, NotificationAudience.Provider)] = "V-S13",
            [(NotificationTypes.BookingCompletionOverdue, NotificationAudience.Provider)] = "V-S15",
        };

    private static readonly IReadOnlyDictionary<string, string> SharedCodes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [NotificationTypes.EventBookingCashReceived] = "A-U2",
        [NotificationTypes.EventBookingPaymentPending] = "A-U3",
        [NotificationTypes.EventCancelled] = "A-U1",
        [NotificationTypes.EventSeatsAvailable] = "A-S4",
        [NotificationTypes.EventReminderDayBefore] = "A-S1",
        [NotificationTypes.EventReminderStartingSoon] = "A-S2",
        [NotificationTypes.EventAttended] = "A-S5",
        [NotificationTypes.EventMissed] = "A-S6",
        [NotificationTypes.EventProfileFlagged] = "A-S7",
        [NotificationTypes.EventProfileFlagRemoved] = "A-S8",
        [NotificationTypes.EventIssueResolved] = "A-S9",
        [NotificationTypes.EventFirstBooking] = "H-U1",
        [NotificationTypes.EventEditableAgain] = "H-U4",
        [NotificationTypes.EventSoldOut] = "H-S1",
        [NotificationTypes.EventHostReminderDayBefore] = "H-S2",
        [NotificationTypes.EventHostReminderStartingSoon] = "H-S3",
        [NotificationTypes.EventLive] = "H-S4",
        [NotificationTypes.EventLiveFree] = "H-S4-Free",
        [NotificationTypes.EventRecordRemaining] = "H-S5",
        [NotificationTypes.EventHosted] = "H-S6",
        [NotificationTypes.EventHostedFree] = "H-S6-Free",
        [NotificationTypes.IdentityAdded] = "ID-1",
        [NotificationTypes.IdentityNotApproved] = "ID-2",
    };

    // Compatibility for previously queued types and the reverse-direction manual
    // no-show not covered by V4. Enqueue/claim rules suppress retired messages.
    private static readonly IReadOnlyDictionary<string, NotificationTemplate> Legacy = new Dictionary<string, NotificationTemplate>(StringComparer.Ordinal)
    {
        [NotificationTypes.BookingNoShowReported] = new("Marked as no-show", "{petName}'s {serviceName} on {serviceDate} has been marked as a no-show — {absentParty} didn't attend. No payment is needed.", NotificationRoutes.BookingDetail, NotificationCategories.Booking),
        [NotificationTypes.BookingPrescriptionRecorded] = new("Prescription added", "{providerName} added a prescription for {petName}.", NotificationRoutes.BookingPrescription, NotificationCategories.Booking),
        [NotificationTypes.EventBookingConfirmed] = new("Tickets booked", "You booked {ticketCount} ticket(s) for {eventTitle}.", NotificationRoutes.EventBookingDetail, NotificationCategories.Event),
        [NotificationTypes.EventBookingPaymentSucceeded] = new("Payment successful", "Your payment for {eventTitle} went through. Your tickets are confirmed.", NotificationRoutes.EventBookingDetail, NotificationCategories.Event),
        [NotificationTypes.EventBookingPaymentFailed] = new("Payment failed", "We couldn't take payment for {eventTitle}. Your tickets aren't confirmed yet.", NotificationRoutes.EventBookingDetail, NotificationCategories.Event),
        [NotificationTypes.EventBookingCancelled] = new("Tickets cancelled", "Your tickets for {eventTitle} have been cancelled.", NotificationRoutes.EventBookingDetail, NotificationCategories.Event),
        [NotificationTypes.EventTicketSold] = new("Tickets sold", "{parentName} booked {ticketCount} ticket(s) for {eventTitle}.", NotificationRoutes.EventDashboard, NotificationCategories.Event),
        [NotificationTypes.EventTicketCancelled] = new("Tickets cancelled", "{parentName} cancelled {ticketCount} ticket(s) for {eventTitle}. The seats are available again.", NotificationRoutes.EventDashboard, NotificationCategories.Event),
        [NotificationTypes.PromotionalMessage] = new("{title}", "{body}", NotificationRoutes.NotificationInbox, NotificationCategories.Promotional),
    };

    public static NotificationTemplate? Find(string notificationType, NotificationAudience audience,
        IReadOnlyDictionary<string, string>? data = null)
    {
        string? Value(string key) => data is not null && data.TryGetValue(key, out var value) ? value : null;
        string? code;
        if (notificationType == NotificationTypes.BookingCompleted)
            code = Value(NotificationDataKeys.CompletionScenario) == "HandOver" ? "P-U12" : "P-U11";
        else if (notificationType == NotificationTypes.BookingNoShowReported && audience == NotificationAudience.PetParent)
            code = Value(NotificationDataKeys.NoShowReason) == "OtpNotShared" ? "P-U10" : "P-U9";
        else if (notificationType == NotificationTypes.BookingNoShowAutoSettled)
            code = Value(NotificationDataKeys.AbsentParty) == "the customer"
                ? (audience == NotificationAudience.Provider ? "V-S11" : "P-S12")
                : (audience == NotificationAudience.Provider ? "V-S9" : "P-S8");
        else if (!AudienceCodes.TryGetValue((notificationType, audience), out code))
            SharedCodes.TryGetValue(notificationType, out code);

        return code is not null ? Cards[code] : Legacy.GetValueOrDefault(notificationType);
    }

    public static IReadOnlyCollection<string> RegisteredTypes => AudienceCodes.Keys.Select(k => k.Type)
        .Concat(SharedCodes.Keys).Concat(Legacy.Keys).Distinct(StringComparer.Ordinal).ToArray();
}
