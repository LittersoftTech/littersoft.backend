# Services notifications V4

Implements the supplied `Pawfront-Notifications-Comparison.html` (Services V4 / Events V3).
All 72 target cards are in `NotificationTemplateCatalog`: 49 Services, 21 Events,
and 2 Identification. Stable `data.type` values are retained; `notificationCode`
identifies the role/scenario card. Promotional messages are unchanged.

## Completion and no-show requests

Provider completion endpoints for both single-day and night-stay bookings accept
an optional `customerArrivedForCollection` boolean. Omitted/false selects P-U11
(pay and collect); true selects P-U12 (payment at hand-over) and requires at least
one photo already uploaded through that booking's evidence endpoint. Existing
body-less calls remain valid. The two branches do not automatically send both cards.

Provider `/no-show` requests accept optional `noShowReason`: `NotArrived` selects
P-U9; `OtpNotShared` selects P-U10. Location remains required. Invalid reasons or
reasons supplied for another actor/status are rejected. When omitted, a booking
in START_JOB without a Wait for Customer history entry uses the OTP reason; other
cases use NotArrived. This fallback preserves old clients. Parent-reported provider
absence retains its existing provider-facing message because the comparison gives
no replacement for that direction.

```json
{"location":{"latitude":47,"longitude":8},"noShowReason":"OtpNotShared"}
```

Automatic no-shows select P-S8/V-S9 when the provider is absent, or P-S12/V-S11
when the customer is absent. Each outcome sends only its corresponding audience pair.
Chat retains its immediate delivery and active-conversation suppression.

## Merges, removal and support

- BOOKING_REQUESTED / NIGHT_STAY_BOOKING_REQUESTED share V-S1 wording and keep
  booking-kind identifiers. The comparison explicitly infers the night-stay mapping.
- Both modification-expiry machine types share P-S2/V-S3. Their existing business
  deadlines are unchanged by the copy update.
- Prescription-added notices are rejected on enqueue; pending ones are retired
  before dispatch. Prescriptions and historical notifications are preserved.
- The six retired event booking/payment/per-booking host notices remain suppressed
  by the existing Events V3 logic. The 21 live event texts and both ID texts are unchanged.
- P-U15 requires an open provider Cash Not Received booking ticket. Creating that
  ticket for an unpaid completed job notifies the parent; completing a job that
  already has that report selects P-U15 instead of an ordinary collection/payment
  message. The ticket id deduplicates repeated ticket/status calls. The legacy
  unpaid-status endpoint still records state; it does not notify before a ticket exists.
- Closing a booking ticket sends P-S15/V-S13 only to its raiser, carrying ticketId.
  A repeated close does not create another notice. Event-ticket resolution is unchanged.

## Timing and invoice availability

`Notification.ServiceNotificationDelivery` contains all 49 card delays. The shared
SQL enqueuer attaches the code and applies the delay to the earliest dispatch time.
Re-deploying preserves operations' configured values. Chat's cards have zero delay.

V-S12 becomes due at accepted working-hours end minus ten minutes, while still in
progress and past scheduled end (late collection). V-S15 becomes due at scheduled
end plus ten minutes if the OTP was consumed and the job remains in progress.
For night stays with a pickup time, V-S15 uses checkout plus that pickup time.
The reminders are independent and idempotent. Current state is rechecked at dispatch
so a completed job does not receive a pending incomplete-job or pickup nudge.
The optional internal `SendBookingReminders.@AsOfUtc` enables deterministic boundary
checks; scheduled callers omit it and use the current clock.

Both invoice cards open the invoice and are enqueued after the recipient's own PDF
has been generated, then delayed 60 seconds. This intentionally preserves the
availability safeguard beyond the comparison's completion/cash-confirmation trigger:
a fixed wait cannot guarantee that an asynchronous PDF is ready. The provider no
longer receives the old mark-paid invoice-summary notice. Payment receipts remain
immediate. Booking currency strings are normalized for P-U12/P-U13 to prevent
`CHF CHF` in either copy or the rendered payload.

## Mobile and rollout

Deploy `database/Pawfront.Database/Deployment/DeployAll.sql` before the API hosts
and Functions application. No production deployment is performed by this source change.
The mobile app can retain existing type handlers and add handling/fallback for the
new `BOOKING_COMPLETION_OVERDUE` type. Use `notificationCode` for card identity,
not parsed title/body. Submit the new optional completion/no-show fields to select
explicit scenarios. Invoice cards now use `/invoices/detail` for both audiences.
Historical inbox rows and promotional content are not rewritten.

## Exact Services copy and policy

| Card | Existing/new wire type | Recipient | Title | Body | Delay seconds |
| --- | --- | --- | --- | --- | ---: |
| P-U1 | `BOOKING_ACCEPTED` | PetParent | Booking Confirmed | {providerName} accepted your {serviceName} booking. Tap to view! | 15 |
| P-U2 | `BOOKING_DECLINED` | PetParent | Booking Request Declined | {providerName} declined your {serviceName} booking. Try another slot or provider. | 15 |
| P-U3 | `BOOKING_CANCELLED_BY_PROVIDER` | PetParent | Booking Cancelled by Provider | {providerName} cancelled your {serviceName} booking. Try another slot or provider. | 15 |
| P-S1 | `BOOKING_EXPIRED_FOR_PARENT` | PetParent | Job Request Expired | {providerName} didn’t respond in time, so your {serviceName} request has expired. Please re-book. | 15 |
| P-U7 | `BOOKING_START_OTP_ISSUED` | PetParent | Share your OTP | {providerName} has started {serviceName}. Tap to view and share your OTP with the provider. | 0 |
| P-U8 | `BOOKING_IN_PROGRESS` | PetParent | Your Job Has Started | Thank you for the OTP! We’ll let you know once the service is complete. | 0 |
| P-U11 | `BOOKING_COMPLETED` | PetParent | Service Completed! Pay & Collect {petName} | {providerName} has completed {serviceName}. Please collect your pet and pay the provider. | 0 |
| P-U12 | `BOOKING_COMPLETED` | PetParent | Payment Requested | Please pay CHF {amount} in cash to {providerName}. | 0 |
| P-U13 | `BOOKING_PAID` | PetParent | Thank You for Your Payment | {providerName} has confirmed receiving CHF {amount} in cash for {serviceName}. | 0 |
| P-U15 | `BOOKING_CASH_NOT_RECEIVED` | PetParent | Payment Not Received | {providerName} has reported that you have not made the payment for {serviceName}. Please make the payment to avoid receiving a red flag. | 0 |
| P-U4 | `BOOKING_MODIFICATION_REQUESTED_BY_PROVIDER` | PetParent | Provider Requested a Modification | {providerName} has proposed a new timing for your {serviceName} booking. Tap to review! | 15 |
| P-U5 | `BOOKING_MODIFICATION_ACCEPTED_BY_PROVIDER` | PetParent | Modification Confirmed | {providerName} accepted your modification request for {serviceName}. Tap to view! | 0 |
| P-U6 | `BOOKING_MODIFICATION_DECLINED_BY_PROVIDER` | PetParent | Modification Declined | {providerName} declined your modification request for {serviceName}. Your booking stays as originally scheduled. | 0 |
| P-S11 | `BOOKING_OTP_ATTEMPTS_EXCEEDED` | PetParent | Booking Cancelled: OTP Not Verified | The OTP for your {serviceName} booking couldn’t be verified, so your booking is cancelled. | 0 |
| P-S9 | `BOOKING_START_OTP_NOT_SEEN` | PetParent | You’re Late for Your Appointment! | Tap to view and share your OTP with the provider. | 0 |
| P-S10 | `BOOKING_START_OTP_NOT_SHARED` | PetParent | Share Your OTP Now | Please share your OTP with {providerName} or you’ll be marked as a no-show. | 0 |
| V-S10 | `BOOKING_START_OTP_NOT_ENTERED` | Provider | Ask for the Customer’s OTP | Please enter {parentName}'s OTP to start the job. | 0 |
| P-S13 | `BOOKING_COMPLETION_TIME_PASSED` | PetParent | Service Completion Time Has Passed | Your {serviceName} appointment completion time has passed. Please chat with {providerName} to check whether you can pick up your pet. | 0 |
| P-S14 | `BOOKING_PICKUP_OVERDUE` | PetParent | You’re Late to Pick Up {petName} | {providerName}’s shop is closing. Please collect {petName}. | 0 |
| V-S12 | `BOOKING_NOT_MARKED_COMPLETE` | Provider | Please Complete The Job | {parentName}’s {serviceName} job still isn’t marked complete. Please mark the job complete or report an issue. | 0 |
| V-S15 | `BOOKING_COMPLETION_OVERDUE` | Provider | Please Complete The Job | {parentName}’s {serviceName} job still isn’t marked complete. Please mark the job complete. | 0 |
| V-S1 | `BOOKING_REQUESTED` | Provider | New Job Request Received | {parentName} has requested {serviceName} on {serviceDate}. Tap to review! | 0 |
| V-U1 | `BOOKING_CANCELLED_BY_PARENT` | Provider | Booking Cancelled by Customer | {parentName} has cancelled the {serviceName} appointment scheduled for {serviceDate}. | 15 |
| V-S2 | `BOOKING_EXPIRED_FOR_PROVIDER` | Provider | You Missed a Job Opportunity | {parentName}’s {serviceName} request expired because you didn't answer in time. | 15 |
| V-U2 | `BOOKING_MODIFICATION_REQUESTED_BY_PARENT` | Provider | Customer Requested a Modification | {parentName} has proposed a new timing for {serviceName} appointment. Tap to review! | 0 |
| V-U3 | `BOOKING_MODIFICATION_ACCEPTED_BY_PARENT` | Provider | Modification Confirmed | {parentName} accepted your modification request for {serviceName}. Your Agenda has been updated. | 0 |
| V-U4 | `BOOKING_MODIFICATION_DECLINED_BY_PARENT` | Provider | Modification Declined | {parentName} declined your modification request for {serviceName}. The job stays in your agenda as originally scheduled. | 0 |
| P-S2 | `BOOKING_MODIFICATION_TIMED_OUT` | PetParent | Modification Request Expired | Your modification request for {serviceName} has expired. Your booking stays as originally scheduled. | 0 |
| V-S3 | `BOOKING_MODIFICATION_TIMED_OUT` | Provider | Modification Request Expired | Your modification request for {parentName}'s {serviceName} has expired. The job stays in your agenda as originally scheduled. | 0 |
| P-S4 | `BOOKING_REMINDER_DAY_BEFORE` | PetParent | Service Tomorrow | Don't forget your {serviceName} booking with {providerName} tomorrow. Be there on time. | 60 |
| V-S5 | `BOOKING_REMINDER_DAY_BEFORE` | Provider | Job Tomorrow | Don't forget your {serviceName} booking with {parentName} tomorrow. Be there on time. | 60 |
| P-S5 | `BOOKING_REMINDER_STARTING_SOON` | PetParent | Service Starts Soon | Your {serviceName} booking with {providerName} starts soon. Please be there on time. | 0 |
| V-S6 | `BOOKING_REMINDER_STARTING_SOON` | Provider | Job Starts Soon | {parentName}’s {serviceName} starts soon. Please be there on time. | 0 |
| P-S6 | `BOOKING_NOT_STARTED_HALFWAY` | PetParent | Has the Service Started? | Your {serviceName} with {providerName} hasn’t started yet. Is everything OK? | 0 |
| V-S7 | `BOOKING_NOT_STARTED_HALFWAY` | Provider | You Haven’t Started the Job | {parentName}’s {serviceName} hasn’t started yet. Tap to start the job. | 0 |
| P-S7 | `BOOKING_NOT_STARTED_WINDOW_ENDED` | PetParent | Service Time Has Ended | Your {serviceName} time has ended but the job still hasn’t started. Is everything OK? | 0 |
| V-S8 | `BOOKING_NOT_STARTED_WINDOW_ENDED` | Provider | Service Time Has Ended | {parentName}’s {serviceName} time has ended but the job hasn’t started. Is everything OK? | 0 |
| P-U9 | `BOOKING_NO_SHOW_REPORTED` | PetParent | You're marked as No-Show | {providerName} marked you as a no-show for {serviceName} as you did not arrive on time. | 0 |
| P-U10 | `BOOKING_NO_SHOW_REPORTED` | PetParent | You're marked as No-Show | {providerName} marked you as a no-show for {serviceName} as you did not share the OTP with them. | 0 |
| P-S8 | `BOOKING_NO_SHOW_AUTO_SETTLED` | PetParent | Provider Didn’t Start the Service | {providerName} didn’t start your {serviceName} as per the booking time. Book another provider. | 15 |
| V-S9 | `BOOKING_NO_SHOW_AUTO_SETTLED` | Provider | You're marked as No-Show | You did not start {parentName}’s {serviceName} job within your working hours. You have been marked as no-show. | 60 |
| P-S12 | `BOOKING_NO_SHOW_AUTO_SETTLED` | PetParent | You're marked as No-Show | You were marked as a no-show for the {serviceName} booking with {providerName} as you did not arrive for the service. | 15 |
| V-S11 | `BOOKING_NO_SHOW_AUTO_SETTLED` | Provider | Customer No-Show | {parentName} has been marked as No Show as they did not arrive for the {serviceName} appointment today. | 15 |
| P-U14 | `MESSAGE_RECEIVED` | PetParent | {providerName} | Sent you a message. | 0 |
| V-U5 | `MESSAGE_RECEIVED` | Provider | {parentName} | Sent you a message. | 0 |
| P-S16 | `INVOICE_ISSUED` | PetParent | Invoice Ready | Your invoice issued by {providerName} for {serviceName} is ready. Tap to view. | 60 |
| V-S14 | `INVOICE_ISSUED` | Provider | Pawfront Invoice Issued | Pawfront has issued you an invoice for {parentName}'s service. Tap to view. | 60 |
| P-S15 | `DISPUTE_RESOLVED` | PetParent | Dispute Resolved | Your ticket for {serviceName} with {providerName} has been resolved. Tap to see the outcome. | 60 |
| V-S13 | `DISPUTE_RESOLVED` | Provider | Dispute Resolved | Your ticket for {serviceName} with {parentName} has been resolved. Tap to see the outcome. | 60 |

## Verification

- `Pawfront.Notification.Checks` validates all 72 exact target title/body pairs,
  rendered placeholders, selected card codes, currency normalization, chat sender,
  merged expiry/request cards, and push/inbox behavior.
- With `--sql`, it checks all 49 Services code/delay mappings, actual reminder
  boundaries and dedupe, evidence-gated completion, manual/automatic no-show causes,
  support recipients, unpaid tickets, generated invoices, and retired-message suppression.
  It uses only the dedicated `PawfrontNotificationV4Checks` LocalDB database.
- `Pawfront.EventLifecycle.Checks --database=PawfrontNotificationV4Checks` can run
  the existing event lifecycle regression suite against the same updated schema.
