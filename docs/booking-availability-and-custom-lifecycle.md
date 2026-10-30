# Booking availability and Custom lifecycle

## Booking dates

Both hosts validate parent-created hourly bookings and Night Stay bookings when
they are created, when a modification is proposed, and again when it is accepted.
Night Stay checks include check-in, every intermediate date, and checkout.
A missing weekday row is treated as closed. Any active service time off makes
that date unavailable, including partial-day time off.

Failures return HTTP 400 using the existing response envelope:

| Code | Meaning |
| --- | --- |
| `BookingLeadTimeTooShort` | The requested start is in the past or less than two hours away. |
| `ServiceClosed` | Service time off, or a closed weekday in a Night Stay range. |
| `InvalidBookingTime` | An hourly booking falls on a closed weekday, outside working hours, or across a break. |
| `InvalidNightStayDates` | Invalid Night Stay date order or a stay longer than 30 nights. |

Declining a modification does not require its proposed dates to remain bookable.
Capacity remains enforced by the existing transactional SQL procedures.

## Night Stay searches and calendars

`GET /providers/search/night-stay` matches the requested dates and ignores the
provider's configured pickup and drop-off times. The nightly availability and
agenda reads use the same date-only cutoff: today and future dates can appear
as available; past dates remain unavailable. Search still checks open weekdays
and service time off through the pickup date, and capacity for each stayed
night in `[startDate, pickupDate)`.

Creating or modifying a booking continues to validate its scheduled handover
time and the existing two-hour lead time.

## Private bookings

Use the existing provider route:

```http
POST /providers/{providerId}/bookings/{bookingId}/status
Content-Type: application/json

{"status":"COMPLETED","note":"Work finished"}
```

For `source: "Custom"`, the owning provider may send `COMPLETED` or
`PROVIDER_CANCELLED` directly from a nonterminal state, including `CONFIRMED`.
Other status targets are rejected on this route for Custom bookings. Terminal
bookings cannot transition again. The change persists the status, update timestamp,
cancellation timestamp when applicable, and audit history. It does not change
payment or payout fields, generate an invoice, or send a parent notification.
App booking transition rules continue to apply to App bookings.

## Weekly local hours

`POST /providers/{providerId}/availability/` accepts `timeZone` alongside the
seven weekday rows. GET and POST responses return the same timezone and local
wall-clock times. Use a timezone such as `Asia/Kolkata` or `Europe/Zurich`.
Invalid timezone identifiers return HTTP 400 `InvalidRequest`.

Example: Monday 00:00–08:00 in India, all other days closed:

```json
{
  "timeZone": "Asia/Kolkata",
  "days": [
    { "dayOfWeek": 0, "isOpen": false },
    { "dayOfWeek": 1, "isOpen": true, "startTime": "00:00:00", "endTime": "08:00:00" },
    { "dayOfWeek": 2, "isOpen": false },
    { "dayOfWeek": 3, "isOpen": false },
    { "dayOfWeek": 4, "isOpen": false },
    { "dayOfWeek": 5, "isOpen": false },
    { "dayOfWeek": 6, "isOpen": false }
  ]
}
```

The client must send the times as entered, without converting them to UTC first.
`00:00:00`–`23:59:00` is also supported. Break times use the same local timezone.
Each row still describes a same-day local window with start earlier than end.

Hourly booking, slot, agenda, and time-off date/time contracts remain UTC. The
server projects the weekly local windows onto the requested UTC date. In the
example, Monday opening appears on Sunday at 18:30 UTC, and Monday's remaining
hours end at 02:30 UTC. The existing hourly booking contract still requires each
booking to fit within one UTC date. Night Stay retains its date-based range
contract and checks weekday labels for every date in that range.

Switzerland's offset is calculated for the actual service date, rather than the
date the hours were saved. SQL start-job checks, reminders, and no-show settlement
also use the saved timezone. At a daylight-saving gap, a boundary moves forward
by the gap; an ambiguous fall-back boundary uses its first occurrence.

### Rollout

Deploy the database changes before the API. `Deployment/DeployAll.sql` adds
timezone columns with `UTC` defaults and installs the shared SQL timezone reader
and updated procedures. Existing rows and clients that omit `timeZone` retain
UTC semantics. No existing schedule is guessed or automatically reinterpreted.
To adopt local hours, the client resaves all seven rows with their timezone.

### Verification

Run `dotnet run --project tests/Pawfront.Availability.Checks` for the application,
timezone, lifecycle, and HTTP error-mapping checks. For SQL checks, create an
isolated database, apply `Deployment/DeployAll.sql`, and run
`tests/Pawfront.Availability.Checks/DatabaseChecks.sql` using `sqlcmd -I -b`.
The SQL checks insert test fixtures and must not run against production.
