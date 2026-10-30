# Booking ticket window and payment timestamp

Single-day and night-stay booking details, provider lists, parent lists, and provider
Agenda jobs expose `paymentDetails.paidAtUtc`. It is an ISO-8601 UTC timestamp from
`Booking.BookingPayments.PaidAtUtc`: the server-recorded moment the provider confirmed
payment. It is null when no payment has been recorded. Existing detail payment
fields and the Agenda's top-level `paidAtUtc` remain available.

```json
{
  "paymentDetails": {
    "paidAtUtc": "2026-10-24T10:30:00+00:00"
  }
}
```

The server enforces the window whenever a `BookingIncident` ticket is created,
including JSON and photo-report flows on either API:

- Reporting opens at confirmation, including before the scheduled start and while
  the job runs. Never-confirmed requests and declined requests cannot be reported.
  Confirmation history preserves eligibility through cancellation or a pending
  modification. Confirmed and later job states also support legacy records.
- The deadline is cash confirmation plus 24 hours when payment was recorded;
  otherwise it is the booked end plus 24 hours. Exactly 24 hours is permitted.
  Night stays use check-out date plus pick-up time for the unpaid fallback.

Rejected creations return HTTP 409 using the normal API error envelope:

| Code | Meaning |
|---|---|
| `TicketWindowNotOpen` | The job has not been confirmed. |
| `TicketWindowExpired` | The 24-hour reporting window has passed. |

The check runs in `Support.CreateTicket`. It locks the booking while reading its
status and schedule, serializing with booking updates. This protects older app versions
and direct API callers. Existing tickets remain readable and actionable; other
ticket kinds keep their existing rules.

Deploy the updated database procedures and both APIs. `Deployment/DeployAll.sql`
contains the changes. Run `dotnet run --project tests/Pawfront.ServicesModule.Checks -- --sql`
against the disposable `PawfrontServicesModuleChecks` database after deployment to
verify timestamps, list mappings, confirmation eligibility, reporting before and
during a job, payment-independent deadlines, photo uploads, and HTTP error mappings.
