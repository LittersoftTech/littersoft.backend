# Events notifications V3

Implements the approved copy and delivery rules in `Pawfront-Event-Notifications-V3 (1).html`.
All 21 live cards use `NotificationTemplateCatalog` for both push and inbox text.
Attendee and host are roles on an event. Parent-organised events deliver both roles
to the Parent app; existing provider events retain their audience for compatibility.

| Code | Type | Trigger / delivery | Route |
| --- | --- | --- | --- |
| A-U1 | `EVENT_CANCELLED` | Host cancellation; every active booking; immediate | `/events/detail` |
| A-U2 | `EVENT_BOOKING_CASH_RECEIVED` | Whole booking changes to cash received; immediate | `/events/detail` |
| A-U3 | `EVENT_BOOKING_PAYMENT_PENDING` | Host reverses whole-booking cash confirmation; immediate | `/events/detail` |
| A-S1 | `EVENT_REMINDER_DAY_BEFORE` | Start minus 24 hours, then 60-second delay; exclude later bookings | `/events/my-tickets` |
| A-S2 | `EVENT_REMINDER_STARTING_SOON` | Start minus one hour | `/events/my-tickets` |
| A-S4 | `EVENT_SEATS_AVAILABLE` | Opted-in seat-release reminder; before booking cutoff | `/events/detail` |
| A-S5 | `EVENT_ATTENDED` | Recorded paid booking, or still-booked free booking; end plus 24 hours | `/events/detail` |
| A-S6 | `EVENT_MISSED` | Paid no-show at attendance close; held to 09:00 | `/events/detail` |
| A-S7 | `EVENT_PROFILE_FLAGGED` | System no-show creates a flag; replaces A-S6; held to 09:00 | `/events/account-flagged` |
| A-S8 | `EVENT_PROFILE_FLAG_REMOVED` | Expiry or two consecutive paid attendances; immediate | `/events` |
| A-S9 | `EVENT_ISSUE_RESOLVED` | Support closes the event ticket; 60-second delay | `/helpline/tickets/detail` |
| H-U1 | `EVENT_FIRST_BOOKING` | First active booking locks the event; immediate | `/events/my-event` |
| H-U4 | `EVENT_EDITABLE_AGAIN` | Last active booking cancelled before start | `/events/my-event` |
| H-S1 | `EVENT_SOLD_OUT` | Last seat booked; repeats after a cancellation and another sell-out | `/events/attendees` |
| H-S2 | `EVENT_HOST_REMINDER_DAY_BEFORE` | Start minus 24 hours; only with attendees | `/events/pawprints` |
| H-S3 | `EVENT_HOST_REMINDER_STARTING_SOON` | Start minus one hour; only with attendees | `/events/attendees` |
| H-S4 | `EVENT_LIVE` | Paid event start | `/events/attendees` |
| H-S4-Free | `EVENT_LIVE_FREE` | Free event start | `/events/attendees` |
| H-S5 | `EVENT_RECORD_REMAINING` | Paid event end; only while tickets remain unrecorded | `/events/attendees` |
| H-S6 | `EVENT_HOSTED` | Attendance closes; recorded paid attendees and total cash; held to 09:00 | `/events/pawprints` |
| H-S6-Free | `EVENT_HOSTED_FREE` | Attendance closes; still-booked free attendees; held to 09:00 | `/events/pawprints` |

## State and payload

- Attendance closes at midnight after the final event day in **Europe/Zurich**.
  Quiet-hour holds end at 09:00 on that next calendar day, including DST changes.
- Free events have no cash/attendance recording, no no-show notification, no
  remaining-recording prompt, and no red-flag outcome.
- Cash is recorded through the existing per-ticket endpoint. A receipt is sent
  when every ticket in the booking has been received; an undo notice is sent
  when that fully received booking becomes pending again. `amount` is the
  booking total, without a currency prefix. Partial cash remains visible in the
  dashboard. Bookings stay `Confirmed` (Attending) until midnight settlement.
- Host counts use tickets, including recorded tickets in partially paid bookings.
  `attendeeCount` renders as `1 attendee` or `3 attendees`; `totalAmount` is actual
  cash recorded before fees, without a currency prefix. Zero-attendance events
  do not generate a host thank-you.
- Every event notification carries `notificationCode`, `eventId`, `eventTitle`,
  host identity and scheduled start. Booking messages also carry
  `eventBookingId`/`bookingId`; support resolution carries `ticketId`. IDs select
  the specific event or ticket even when the route opens a tab or My Tickets.
- User actions never notify their actor. No booking/cancellation receipts,
  late-cancellation flag notices, report-submission notices, or host notices
  about posting, editing or recording cash are newly queued.
- `EVENT_TICKET_SOLD` and `EVENT_TICKET_CANCELLED` are retired. Their constants and
  templates remain readable for compatibility, while unattempted legacy outbox
  rows are retired before dispatch.
- Before first dispatch, stale event notifications are retired with a diagnostic
  `Failed` status and no rendered title. They never appear in the inbox. This
  covers quick cancellation of a first booking, cancelled events, refilled seats,
  obsolete reminders, and flags already removed.

## Configuration and rollout

Deploy `database/Pawfront.Database/Deployment/DeployAll.sql` before the API hosts
and Functions app. Standalone SQL sources mirror this consolidated script.
No production deployment is performed by this change.

`Notification.EventNotificationDelivery` stores each card's `DelaySeconds` and
optional `HoldUntilHour`. Re-deployment inserts missing codes and preserves
customised values. The HTML's executable `DELIVERY` map sets H-U1 to **0 seconds**;
its prose elsewhere mentions 15 seconds. The shipped default follows the map;
set H-U1's delay to 15 if that debounce is desired. Relevance is checked at dispatch
in either case.

`ActivatedAtUtc` prevents historical scheduled notifications from flooding the
inbox on first rollout. New bookings/events and subsequent due times are covered.
`EventNotificationFunction` settles attendance and stages notifications every
15 seconds; `NotificationDispatchFunction` drains eligible entries every
15 seconds. “Immediate” means no intentional delay; timer and transport latency
still apply. Quiet-hour and other scheduled entries are never claimed early.

The new routes are isolated in `NotificationRoutes`, whose values remain
provisional pending the mobile route table. The mobile app must handle the new
types and routes before the corresponding messages are enabled in production.

Validation: `tests/Pawfront.Notification.Checks` covers all approved copy and
destinations; `tests/Pawfront.EventLifecycle.Checks` exercises real SQL transitions,
role recipients, quiet hours, timed reminders, free/paid outcomes, repeat booking
cycles, support resolution and stale-delivery suppression.
