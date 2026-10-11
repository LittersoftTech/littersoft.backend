# Payment not received

A Provider can move an App booking from `COMPLETED` to `PAYMENT_NOT_RECEIVED`.
This is a job lifecycle status, recorded in the usual status history. The Provider
can later use the existing `/paid` API to move it to `PAID` and create the payment
ledger row and invoices.

## Provider APIs

Both existing routes now change the job status:

- `POST /api/v1/providers/{providerId}/bookings/{bookingId}/cash-not-received`
- `POST /api/v1/providers/{providerId}/night-stay-bookings/{bookingId}/cash-not-received`

The request retains its required location:

```json
{
  "location": {
    "latitude": 47.3769,
    "longitude": 8.5417,
    "accuracyMetres": 5
  }
}
```

The `200` response now contains the updated booking in the standard response
envelope, with `data.status = "PAYMENT_NOT_RECEIVED"`. Previously these routes
returned a standalone location event. Mobile clients must read the booking
response. Status, Provider location (`CashNotReceived`), history and the Parent
notification commit in one transaction.

The Parent receives `BOOKING_CASH_NOT_RECEIVED` through the same outbox, push
and Firestore delivery path as `BOOKING_PAID`. Its envelope has
`data.entityId = bookingId`, `data.bookingId = bookingId`, the usual routing/id
fields and `data.isNightStay` for the booking kind. The Firestore entry is at
`users/{uid}/notifications/{notificationId}`, with the same `type`, `data`,
`title`, `body`, `createdAt` and `isRead` shape as the paid notification.
Firestore delivery is attempted even if the Parent has no active push token.

## Dispute display

Both hosts' single-day and night-stay booking lists, details and status responses expose
`isDisputeRaised`. On Parent list cards it is inside `booking`; on booking
details it is at the response root. It is true once a `BookingIncident` ticket
exists on that booking from either party, including a closed ticket. It is
false for an unreported booking. Apps should give this flag display priority
and show **Dispute Raised** as the job status when it is true. The lifecycle
`status` still tracks the job and payment, so a later payment remains recordable.

The existing `isTicketRaisedByMe`, `ticketId` and `ticketRef` fields continue to
describe the caller's own open ticket. A counterparty's ticket sets the shared
dispute marker without filling those fields. Chat, event and app-issue tickets
do not set a booking's dispute marker.

The single-day generic `/status` route also accepts `PAYMENT_NOT_RECEIVED`,
with the same location requirement and transition rules.

Only the booking's Provider may mark it unpaid, and only from `COMPLETED`.
The Parent cannot set this status. An unfinished, paid, cancelled or already
unpaid job is rejected with the existing lifecycle errors. Custom walk-ins keep
their existing off-platform payment rules.

## Reads and lifecycle behavior

- Exact status filters accept `PAYMENT_NOT_RECEIVED`; the `Completed` group
  includes it, while `Upcoming` and `Cancelled` exclude it.
- It remains a finished, reviewable job and contributes to completed-job counts.
- Its amount remains earned but awaiting receipt in earnings summaries. Statements
  group it under `Unpaid`. It has no payment ledger entry until payment is recorded.
- Payout fields and the actual finish time/date are preserved. It cannot be
  cancelled, restarted or modified, and blocking a participant does not cancel it.
- Parent and pet deletion treat it like a completed job and preserve its history.

## Deployment and checks

Run the updated `database/Pawfront.Database/Deployment/DeployAll.sql` before
shipping the APIs. It upgrades both status constraints idempotently and updates
the transition, payment and read procedures.

Focused checks:

```powershell
dotnet run --project tests/Pawfront.PaymentStatus.Checks -p:BuildInParallel=false
```
