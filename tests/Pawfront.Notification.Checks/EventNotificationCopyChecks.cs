using Pawfront.Application.Notifications;

internal static class EventNotificationCopyChecks
{
    public static int Run()
    {
        var data = new Dictionary<string, string>
        {
            ["eventTitle"] = "Puppy Picnic", ["hostName"] = "Alex", ["parentName"] = "Sam",
            ["amount"] = "40", ["totalAmount"] = "120", ["seats"] = "12",
            ["attendeeCount"] = "3", ["serviceStartUtc"] = "2026-10-24T14:00:00"
        };
        (string Type, string Title, string Body, string Route)[] approved =
        [
            (NotificationTypes.EventCancelled, "Event Cancelled by Host", "Alex cancelled Puppy Picnic scheduled for 24 Oct.", NotificationRoutes.EventDetail),
            (NotificationTypes.EventBookingCashReceived, "Thank You for Your Payment", "Alex has confirmed receiving CHF 40 for Puppy Picnic.", NotificationRoutes.EventDetail),
            (NotificationTypes.EventBookingPaymentPending, "Event Payment Pending", "Alex marked your CHF 40 payment for Puppy Picnic as pending. Please check with the host.", NotificationRoutes.EventDetail),
            (NotificationTypes.EventReminderDayBefore, "Be There For The Event Tomorrow", "Don’t forget to be there on time for Puppy Picnic tomorrow.", NotificationRoutes.EventMyTickets),
            (NotificationTypes.EventReminderStartingSoon, "Event Starts Soon", "Puppy Picnic starts in 1 hour. Please be there on time.", NotificationRoutes.EventMyTickets),
            (NotificationTypes.EventSeatsAvailable, "A Spot Just Opened Up", "A spot opened up for Puppy Picnic. Book now before it’s gone!", NotificationRoutes.EventDetail),
            (NotificationTypes.EventAttended, "Thanks for Attending!", "We hope you and your pet enjoyed Puppy Picnic. See you at the next one!", NotificationRoutes.EventDetail),
            (NotificationTypes.EventMissed, "You Missed the Event", "Your attendance at Puppy Picnic wasn’t recorded, so you’re marked as a no-show. This counts towards a red flag.", NotificationRoutes.EventDetail),
            (NotificationTypes.EventProfileFlagged, "Your Events Profile Is Flagged", "Your last two event bookings were cancelled late or missed, so your Events profile has a red flag for 30 days. Tap to see how to remove it.", NotificationRoutes.EventAccountFlagged),
            (NotificationTypes.EventProfileFlagRemoved, "Red Flag Removed", "Congratulations! The red flag is removed from your Events profile. You can again book up to 5 events at a time and host your own events.", NotificationRoutes.Events),
            (NotificationTypes.EventIssueResolved, "Issue Resolved", "Your ticket for Puppy Picnic has been resolved. Tap to see the outcome.", NotificationRoutes.TicketDetail),
            (NotificationTypes.EventFirstBooking, "First Booking For Your Event!", "Congratulations! Sam booked the first ticket for Puppy Picnic. Your event details can no longer be changed.", NotificationRoutes.EventMyEvent),
            (NotificationTypes.EventEditableAgain, "Your Event Is Editable Again", "All bookings for Puppy Picnic were cancelled. You can edit your event details again.", NotificationRoutes.EventMyEvent),
            (NotificationTypes.EventSoldOut, "Your Event Is Sold Out!", "Congratulations! All 12 seats for Puppy Picnic are booked.", NotificationRoutes.EventAttendees),
            (NotificationTypes.EventHostReminderDayBefore, "Get Ready For Your Event Tomorrow", "3 attendees are booked for Puppy Picnic. Good luck!", NotificationRoutes.EventPawPrints),
            (NotificationTypes.EventHostReminderStartingSoon, "Your Event Starts Soon!", "3 attendees are coming. Bookings are now closed.", NotificationRoutes.EventAttendees),
            (NotificationTypes.EventLive, "Your Event Is Live", "Record attendance and cash received in the Attendees tab as guests arrive.", NotificationRoutes.EventAttendees),
            (NotificationTypes.EventLiveFree, "Your Event Is Live", "Puppy Picnic has started. Check who’s coming in the Attendees tab.", NotificationRoutes.EventAttendees),
            (NotificationTypes.EventRecordRemaining, "Record Remaining Attendance and Cash", "Please record the remaining 3 attendees by midnight. Unrecorded attendees are marked as no-show.", NotificationRoutes.EventAttendees),
            (NotificationTypes.EventHosted, "Thank You for Hosting!", "3 attendees joined your event. You received CHF 120. Congratulations!", NotificationRoutes.EventPawPrints),
            (NotificationTypes.EventHostedFree, "Thank You for Hosting!", "3 attendees joined your event. Congratulations!", NotificationRoutes.EventPawPrints)
        ];
        var checks = 0;
        foreach (var (type, title, body, route) in approved)
        {
            var rendered = NotificationRenderer.Render(type, NotificationAudience.PetParent, data)!;
            if (rendered.Title != title || rendered.Body != body || rendered.Route != route || rendered.Category != "EVENT")
                throw new Exception($"Events V3 approved copy and destination: {type}: {rendered}");
            checks++;
        }
        data["attendeeCount"] = "1";
        var singular = NotificationRenderer.Render(NotificationTypes.EventHostReminderDayBefore, NotificationAudience.PetParent, data)!;
        if (singular.Body != "1 attendee is booked for Puppy Picnic. Good luck!" || singular.Data["attendees"] != "1 attendee")
            throw new Exception("Singular attendee wording and payload agree");
        return checks + 1;
    }
}
