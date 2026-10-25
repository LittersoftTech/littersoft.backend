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

- Reporting opens when the job ends. Completion history or an actual end marker
  permits an early-finished job to be reported before its booked end. Payment
  evidence is also sufficient when older records have no completion history.
- If paid, the last permitted instant is `paidAtUtc + 24 hours`, regardless of
  the booked end. Exactly 24 hours is permitted; later requests are refused.
- If no payment is recorded, the deadline is the booked end plus 24 hours.
  For night stays, booked end means check-out date plus pick-up time.
- A job still in progress cannot be reported merely because its booked end passed.

Rejected creations return HTTP 409 using the normal API error envelope:

| Code | Meaning |
|---|---|
| `TicketWindowNotOpen` | The job has not ended yet. |
| `TicketWindowExpired` | The 24-hour reporting window has passed. |

The check runs in `Support.CreateTicket`. It locks the booking while reading its
payment, serializing with payment confirmation. This protects older app versions
and direct API callers. Existing tickets remain readable and actionable; other
ticket kinds keep their existing rules.

Deploy the updated database procedures and both APIs. `Deployment/DeployAll.sql`
contains the changes. Run `dotnet run --project tests/Pawfront.ServicesModule.Checks -- --sql`
against the disposable `PawfrontServicesModuleChecks` database after deployment to
verify timestamps, list mappings, early/late payment windows, unpaid fallback,
unfinished jobs, and HTTP error mappings.
