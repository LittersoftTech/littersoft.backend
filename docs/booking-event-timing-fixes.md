# Booking and event timing fixes — 30 October 2026

New service and night-stay requests require three hours before start. Modification
requests from either party require three hours before the earlier of the original
and proposed start. Night stays use check-in date plus drop-off time; a missing
legacy handover time means midnight UTC. Sending at exactly three hours is allowed.

Acceptance remains separate: requests expire after 24 hours or two hours before
the applicable start, whichever comes first. The same deadline is used by response
guards, sweep jobs, list/detail responses, Agenda and notifications. Accepting a
modification between two and three hours before start remains permitted. Parent
proposals adopting different handover terms use those proposed times, including
midnight when an acknowledged handover time is cleared. Availability and calendars
apply the three-hour sending limit too.

Booking tickets open at confirmation and remain available through starting and
running states, including legacy ENDING. Never-confirmed requests remain ineligible.
The closing deadline is cash confirmation plus 24 hours, falling back to the booked
end plus 24 hours when no payment is recorded.

Accepted working hours are snapshotted at confirmation and preserved when weekly
availability changes. Start Job and Wait for Customer use that opening and closing
window on the booking day, including before the booked slot. Completion keeps its
existing status checks; automatic no-show settlement uses the stored closing time.
Legacy bookings whose hours were already overwritten before snapshot rollout need
verified historical hours; the current weekly schedule cannot reconstruct them.

Event attendance, booking indicators, cancellation warnings, booking reads and
cancellation are scoped by the authenticated account ID and role as well as email.
An account in the other app with the same email no longer inherits those bookings.
Hosting remains determined by the event's original organizer.

The provider host now resolves its authenticated account for event views. Unique
views remain per account and role, exclude the host, and feed both event PawPrints
and organizer metrics. Paid NoRefund and unrecognized cancellation policies have
no free window and record a Negative outcome when cancelled before event start.
Two consecutive negatives within 30 days flag the account; another late cancellation
extends an active flag to 30 days after cancellation. Free-window cancellations
preserve the deadline. Reliability reads stop showing the flag once it expires.

## Deployed database inspection

Read-only inspection of the configured deployed SQL database confirmed:

- Cancellation already rejects at or after `EventStartUtc(StartDate,StartTime)`.
- Creation already rejects from one hour before start, matching `bookingsCloseAtUtc`.
- `EventStartUtc` already uses the Zurich Windows timezone and its DST conversion.
- The deployed cancellation procedure still requires `@Hours > 0` to record a
  negative, reproducing the NoRefund gap. The flag-extension logic already exists
  in `RecordReliabilityOutcome`; the skipped cancellation outcome prevents it running.
- The reported event `fe93f9ad-7ed3-f111-bf5b-6045bd2b4c34` is paid and uses NoRefund.
  At inspection its stored start was 30 October 2026 at 12:53 Zurich, converted
  to 11:53 UTC, and its view count was 1.
- No parent or provider profile matched the supplied `abc@xyz.com` email. The
  tester's actual email is needed to inspect that account's marks and event policies.

This was a database inspection, not an authenticated end-to-end test of the deployed
HTTP API. No deployed application or database was changed.

## Verification and rollout

The consolidated deployment script applied successfully to the disposable
`PawfrontServicesModuleChecks` and `PawfrontEventLifecycleChecks` databases.
599 service SQL/HTTP checks, 159 event lifecycle integration checks, 104 availability
checks and the modification suite passed. Coverage includes shared-email accounts in both
hosting directions, two NoRefund cancellations of the same event, flag restart and
expiry, post-start refusal, concurrent provider views, payment-based ticket expiry,
honoured hours and the separate sending/acceptance windows.

Apply `database/Pawfront.Database/Deployment/DeployAll.sql` before deploying both
API hosts and the Functions app. The checkout also contains earlier uncommitted
working-hours and notification changes; review the combined deployment before rollout.
