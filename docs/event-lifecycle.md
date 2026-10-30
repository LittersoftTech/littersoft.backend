# Event lifecycle API

All routes below have the `/api/v1` prefix and require authentication. Host IDs must match the authenticated parent. Booking creation takes its account ID and email from the authenticated caller; the body cannot choose another account for the booking limits or reliability rules.

| Method and route | Behaviour |
| --- | --- |
| `POST /pet-parents/{petParentId}/events/{eventId}/cancel` | Cancels a future event, changes confirmed bookings to `EventCancelled`, and queues attendee notifications atomically. Repeating the call is safe. |
| `POST /providers/{providerId}/events/{eventId}/cancel` | Provider API: validates the authenticated provider and event ownership, then applies the same atomic cancellation, booking updates, and notifications as the parent route. Returns `eventStatus: "Cancelled"` and `isCancelledByHost: true`. Repeating the call is safe. |
| `DELETE /pet-parents/{petParentId}/events/{eventId}` | Hides a cancelled event from its host's list; retains event and booking history. |
| `DELETE /event-bookings/{bookingId}` | Releases all tickets; retains `CancelledByParent`, `cancelledAtUtc` and its `cancelledAt` alias. A new booking for the same event is allowed. |
| `GET /me/reliability` | Returns `isRedFlagged`, `redFlagExpiresAt`, `consecutivePaidEventsAttended`, `activeBookingCount`, and `consecutiveNegativeMarks`. |
| `PATCH /event-bookings/{bookingId}/tickets/{ticketNumber}` | Host-only body: `{ "paymentStatus": "CashReceived" }` or `{ "paymentStatus": "Pending" }`. |
| `POST /events/{eventId}/waitlist` | Joins the sold-out event's waitlist. Repeating the call is safe. |
| `DELETE /events/{eventId}/waitlist` | Leaves the waitlist. |
| `POST /pet-parents/{petParentId}/support-tickets/report-event` | Existing route now accepts `eventId` and `bookingId`, plus `comment`, `category`, and/or `reason`. The booking must belong to the reporter and event. |

Event list/detail responses include `eventStatus`, `isCancelledByHost`, `attendedCount`, and `earnedAmount`. Parent catalog/detail/trending responses include the caller's `isOnWaitlist`. The organizer includes `eventsCancelledCount`, counting only host cancellations with at least one historical booking, even if all bookings had subsequently been cancelled by attendees. Removing an event does not change this count.

On the host's event detail, attendee entries include `bookingId`, `ticketNumber`, `paymentStatus`, `amount` (the booked per-ticket price), and `profileImageUrl`. Booking IDs and payment details are withheld from public attendee lists. Names and profile images remain public. Provider profile images are currently unavailable and return null.

Booking detail includes per-ticket payment state/amount, `consecutiveNegativeMarks` (0 or 1) and `hasReported`. `hasReported` remains true after the support ticket is closed. A linked report cannot be filed twice for the same booking; the existing `TicketAlreadyOpen` response identifies the earlier ticket even if closed. General event reports without `bookingId` retain the existing one-open-report-per-event/reporter behaviour.

## Rules

- The sixth upcoming booking is refused with `UpcomingBookingLimitReached`. A flagged account cannot add a paid booking when it already holds an upcoming paid booking (`RedFlagBookingLimitReached`) or create an event (`RedFlagCannotCreateEvent`). Existing reservations are not automatically cancelled when a flag is applied.
- Duplicate live bookings return `EventAlreadyBooked`; cancelled events return `EventNotBookable`; purchases at or after the ticket cutoff return `BookingsClosed`.
- The event edit procedures reject edits while active bookings exist (`EventLocked`). Cancelling the last active booking unlocks edits before the start time and notifies the host; started events stay locked. Parent PATCH accepts `startDate`, `endDate`, `startTime`, `endTime`, `bannerImageUrl`, `description`, `maximumCapacity`, and `amenities`. Schedule changes are checked against the same host's other non-cancelled events and return `HostEventOverlap` on conflict. A top-level capacity edit preserves the venue. PUT cannot change other event fields or the venue.
- The two 24/48-hour policies use event start minus their advertised hours. The pre-existing 2/4-hour policies are also respected. Cancellation at the exact boundary is on time; later cancellation records one negative outcome. Null/`NoRefund` policies have no specified reliability cutoff, so they do not generate a late-cancellation mark.
- Two consecutive negatives recorded within 30 days flag the account for 30 days from the second mark. A new negative while flagged restarts the 30 days from that mark and resets consecutive paid attendance to zero. The reliability read reflects the new expiry immediately. Successful attendance resets the negative streak. Free attendance does not count toward the two consecutive paid attendances required for early flag removal.
- Every clock boundary uses **Europe/Zurich**, including daylight-saving time. SQL uses the corresponding `W. Europe Standard Time` zone. Cash recording is available from midnight on the first event day until midnight after the final event day; after that it returns `AttendanceWindowClosed`.
- The Events V3 timer settles attendance every 15 seconds after that cutoff (the five-minute booking sweep remains an idempotent fallback). Pending tickets become `NoShow`; a booking with any unpaid ticket becomes `NoShow` and receives at most one negative mark. All tickets received means `Attended`. Paid-attendance recovery is finalized only at settlement, so undoing a cash receipt cannot leave an earned reliability reward behind.
- `attendedCount` counts tickets marked `CashReceived`. `earnedAmount` sums their original per-ticket prices, including partially collected bookings. Both exclude cancelled bookings. Receiving cash never frees capacity.
- Each waitlist registration receives `EVENT_SEATS_AVAILABLE` once when seats are released, and is removed when the parent books or the event starts. A host cancellation queues `EVENT_CANCELLED` once per active booking. A permitted capacity increase also notifies the waitlist; the first-booking edit lock still takes precedence.

## Storage and rollout

Apply `database/Pawfront.Database/Deployment/DeployAll.sql` (`sqlcmd -I -b`) **before deploying the API hosts and Functions app**. This single file includes the preflight checks, idempotent schema upgrade, optional verified booking-owner mappings, timezone function, and all updated procedures. It commits the database changes together at the end and refuses unresolved active-booking identities. Fill the mapping table near the top of a private copy when needed. The earlier category migration still requires coordinating retired category changes with Cosmos before deployment; see `database/Pawfront.Database/Deployment/EventLifecycleRollout.md`.

SQL is authoritative for event status, booking limits, reliability outcomes, cash receipts, and capacity. Existing events without SQL capacity use their Cosmos capacity until their next edit. Venue details continue to live in Cosmos. Mutations and the sweep share a transaction-owned `EventLifecycle` application lock; this deliberately serializes lifecycle writes across both API hosts. Notifications use the existing durable outbox and dispatcher.

Legacy booking account IDs are backfilled only where the email uniquely identifies a profile across the two authentication tables. Before rollout, resolve any active legacy bookings with `BookerAccountId IS NULL` to verified accounts: these cannot receive account-addressed push notifications or reliability outcomes. Do not guess an identity when an email matches both roles. New bookings require an authenticated profile.

The first upgrade marks already-ended historical bookings as reliability-settled without inventing attendance or cash records. This prevents retroactive no-show marks for a feature that did not exist. Rerunning deployment does not skip settlement of newly completed events.

No production database, Functions app, API, or push service is deployed by these source changes.

## Verification

`tests/Pawfront.EventLifecycle.Checks` contains executable LocalDB integration checks. It uses only the disposable `PawfrontEventLifecycleChecks` database on `(localdb)\MSSQLLocalDB`; it inserts uniquely identified test fixtures and never connects to a configured production database.

```powershell
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "CREATE DATABASE PawfrontEventLifecycleChecks"
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d PawfrontEventLifecycleChecks -I -b -i database/Pawfront.Database/Deployment/DeployAll.sql
dotnet run --project tests/Pawfront.EventLifecycle.Checks/Pawfront.EventLifecycle.Checks.csproj
```

The checks cover concurrent limits, cancellation/rebooking, host authorization and counts, outbox deduplication, support links, receipt undo, settlement idempotency, paid attendance recovery, expired marks, Zurich time conversion, and capacity-only PATCH binding/venue preservation.

## Prepared targeted rollout

The event-only deployment and live aggregate preflight results are documented in [EventLifecycleRollout.md](../database/Pawfront.Database/Deployment/EventLifecycleRollout.md). The user approved three legacy category mappings and requested that deployment remain prepared but unapplied. The 21 ambiguous booking identities still require verification before any rollout.


## Cancellation cards and ticket booking cutoff

`GET /pet-parents/{id}/event-bookings` and the provider's `GET /event-bookings` retain all booking history, including after the host removes a cancelled event. Host-cancelled rows expose `status: "host_cancelled"` and `isCancelledByHost: true`; attendee-cancelled rows retain `CancelledByParent` (or the legacy `Cancelled`) and expose `eventCancelledByMe: true`. Booking detail exposes the same status and flags. A host cancellation does not mark an attendee as having cancelled it.

`GET /events/{eventId}` also returns `eventCancelledByMe`: true if the caller previously cancelled a booking, including after rebooking, or the caller is the parent host who cancelled the event. This is derived from the caller's own history and does not expose anyone else's cancellations.

Each booking summary includes `bookings: { "totalBookings": 12, "maxBookings": 50 }`. The total is the sum of tickets across all `Confirmed`/`Attended` bookings for the event, matching the catalogue; cancelled tickets are excluded. Physical capacity comes from SQL with a Cosmos fallback for legacy events; online capacity is null.

Event list/detail responses and booking summaries return `bookingsCloseAtUtc`. Bookings close exactly one elapsed hour before the first event start in Europe/Zurich (including daylight-saving offsets). At and after that instant, `isBookable` is false. Both booking POST hosts reject purchases with HTTP 409 and the exact body `{ "code": "BookingsClosed", "message": "Ticket bookings for this event are closed." }`. This error uses the explicitly requested body shape. The SQL transaction checks its own UTC clock and current event start after acquiring the lifecycle lock, so stale client data or concurrent edits cannot bypass the cutoff. Live and ended events also reject purchases.

Deploy the updated procedures before the API hosts. Both standalone procedure files and the consolidated `DeployAll.sql` contain these changes; no production deployment is performed by this work.

### Paid booking limits, cash changes, reminders, views and account deletion

- A community red flag permits one upcoming **paid** booking. Free bookings do not
  consume that slot and are not refused by the flag. The general five-booking
  limit still applies. `GET /me/reliability` includes `activePaidBookingCount`;
  account reliability's community block also includes it and calculates flagged
  available slots from that count.
- Cash recording is per ticket. The first whole-booking transition to all cash
  received queues `EVENT_BOOKING_CASH_RECEIVED`; reversing that confirmation
  queues `EVENT_BOOKING_PAYMENT_PENDING`. Repeating a value does not notify.
  `amount` is the full booking amount as an invariant string without `CHF`.
  A partial receipt updates dashboard totals without sending a full-payment
  receipt. Bookings remain `Confirmed` until midnight settlement, then become
  `Attended` or `NoShow`. Free events do not record attendance or cash.
- Waitlist registrations are notified once. Repeated seat releases do not notify
  already claimed rows; booking removes the winner's registration. The event
  attendance sweep also removes waitlists for started events; seat notices stop
  at the one-hour booking cutoff. Event
  detail continues to return the authenticated caller's `isOnWaitlist`, including
  after a reminder has been sent; started/cancelled events return false.
- Event views are unique per event and account, with host views excluded. Existing
  aggregate counts are retained because historical viewer identities are unknown.
- Successful account deletion cancels upcoming hosted events through the normal
  host cancellation path, notifying attendees. Its own upcoming bookings become
  `AccountDeleted`, releasing seats, notifying each host and waking unnotified
  waiters without a negative reliability mark. Its waitlist rows are deleted.
  Existing account-deletion refusal rules for unfinished services/open support
  tickets remain in force. Events that have already started remain historical.
- Deploy the consolidated SQL script before the API hosts and Functions. Apply
  migrations to production through the normal release process; local validation
  does not verify the currently deployed API's authenticated response.

See [Events notifications V3](event-notifications-v3.md) for all 21 approved
notifications, quiet hours, delay configuration, routes and rollout.
