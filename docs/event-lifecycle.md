# Event lifecycle API

All routes below have the `/api/v1` prefix and require authentication. Host IDs must match the authenticated parent. Booking creation takes its account ID and email from the authenticated caller; the body cannot choose another account for the booking limits or reliability rules.

| Method and route | Behaviour |
| --- | --- |
| `POST /pet-parents/{petParentId}/events/{eventId}/cancel` | Cancels a future event, changes confirmed bookings to `EventCancelled`, and queues attendee notifications atomically. Repeating the call is safe. |
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

- The sixth upcoming booking is refused with `UpcomingBookingLimitReached`. A flagged account cannot add a booking when it already holds one (`RedFlagBookingLimitReached`) or create an event (`RedFlagCannotCreateEvent`). Existing reservations are not automatically cancelled when a flag is applied.
- Duplicate live bookings return `EventAlreadyBooked`; cancelled/started events return `EventNotBookable`.
- The event edit procedures reject every edit after any booking has existed, including a subsequently cancelled booking (`EventLocked`). Parent PATCH accepts only `bannerImageUrl`, `description`, `maximumCapacity`, and `amenities`. A top-level capacity edit preserves the venue. PUT cannot change other event fields or the venue.
- The two 24/48-hour policies use event start minus their advertised hours. The pre-existing 2/4-hour policies are also respected. Cancellation at the exact boundary is on time; later cancellation records one negative outcome. Null/`NoRefund` policies have no specified reliability cutoff, so they do not generate a late-cancellation mark.
- Two consecutive negatives recorded within 30 days flag the account for 30 days from the second mark. A new negative does not extend an active flag. Successful attendance resets the negative streak. Free attendance does not count toward the two consecutive paid attendances required for early flag removal.
- Every clock boundary uses **Europe/Zurich**, including daylight-saving time. SQL uses the corresponding `W. Europe Standard Time` zone. Cash recording is available from midnight on the first event day until midnight after the final event day; after that it returns `AttendanceWindowClosed`.
- The existing five-minute booking sweep settles attendance after that cutoff. Pending tickets become `NoShow`; a booking with any unpaid ticket becomes `NoShow` and receives at most one negative mark. All tickets received means `Attended`. Paid-attendance recovery is finalized only at settlement, so undoing a cash receipt cannot leave an earned reliability reward behind.
- `attendedCount` counts tickets marked `CashReceived`. `earnedAmount` sums their original per-ticket prices, including partially collected bookings. Both exclude cancelled bookings. Receiving cash never frees capacity.
- Waitlisted parents receive `EVENT_SEATS_AVAILABLE` when seats are released, and are removed from the waitlist when they book. A host cancellation queues `EVENT_CANCELLED` once per active booking. A permitted capacity increase also notifies the waitlist; the first-booking edit lock still takes precedence.

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

