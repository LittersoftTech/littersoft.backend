# Booking rules

**Audience:** Pawfront backend + mobile developers, QA.
**Scope:** The rules governing a **service booking** (`Booking.Bookings`) and a
**night stay** (`Booking.NightStayBookings`) — creation, scheduling, the job
lifecycle, modification, cancellation and settlement. Event ticket bookings are a
separate model and are not covered here.

Each rule has a stable id (`BR-nn`). Ids are never reused; a retired rule keeps
its number and is marked **Retired**. Reference them in code comments, tickets and
conversations ("this is BR-07").

**Where a rule is enforced matters** and is stated per rule:
- **Application** — C# validation, before SQL is touched.
- **SQL** — inside the stored procedure, under `UPDLOCK + HOLDLOCK` where the
  check has to be race-safe.
- **Job** — the scheduled external job (an Azure timer Function). Nothing in the
  database changes a booking's status on a clock any more.

Times are **UTC** throughout, per the codebase convention. `serviceStart` means
`BookingDate + StartTime` for a single-day booking and `CheckInDate + DropOffTime`
for a night stay.

---

## A. Timing and scheduling

**BR-01 — A booking must start at least 2 hours from now.**
A parent browsing at 11:00 sees 13:00 as the first bookable slot. Measured against
`serviceStart`. Slots inside the window are not shown at all (not shown as full),
and the same test hides today's already-elapsed slots.
*Enforced:* Application — the shared slot service (so both hosts' slot endpoints,
the `/providers` window checker and all five `/providers/search/*` cards inherit
it) plus every App-booking create path.
*Violation:* 409 `BookingLeadTimeTooShort`.
*Exempt:* Custom walk-ins (BR-09).

**BR-02 — The booked window must fit inside the provider's weekly working hours,
and must not overlap their break.**
*Enforced:* Application. *Violation:* 400 `InvalidBookingTime`.

**BR-03 — The booked window must not overlap a closure on that ServiceId.**
Closures are per-service: a NightStay closure does not block DayCare. A full-day
closure blocks the whole date; a partial-day closure carves the working window.
*Enforced:* Application. *Violation:* 409 `ServiceClosed`.

**BR-04 — The booking's duration must satisfy the offering's duration rule.**
`TrainingSession`, `VetAppointment` and each grooming menu item are **fixed**
duration; `DayCare` has a **minimum**. `NightStay` takes no duration — it is
date-granular.
*Enforced:* Application. *Violation:* 400 `InvalidBookingDuration`.

**BR-05 — A night stay spans `[CheckInDate, CheckOutDate)`; the checkout day is
not a stayed night.** At least one night, at most 30.
*Enforced:* Application. *Violation:* 400 `InvalidNightStayDates`.

**BR-06 — Night-stay availability is per night, not per time window.**
Capacity, closures and occupancy are evaluated for each stayed night; the weekly
time grid is deliberately not consulted.
*Enforced:* Application + SQL.

---

## B. Creation

**BR-07 — A parent-created booking starts at `CREATED`.** It holds its capacity
slot from that moment, before the provider has answered.

**BR-08 — A provider-recorded Custom walk-in starts at `CONFIRMED`.** There is no
counterparty to accept it.

**BR-09 — A Custom walk-in is exempt from the booking lead time (BR-01).**
The provider is recording a job happening now; requiring two hours' notice would
make the feature unusable. All other scheduling gates (BR-02, BR-03) still apply.

**BR-10 — A booking must name an active service owned by the provider.**
*Enforced:* Application + SQL (race-safe re-check).
*Violation:* 400 `InvalidServiceId`.

**BR-11 — A provider whose master Active switch is off accepts no new bookings.**
Existing bookings are unaffected.
*Enforced:* SQL. *Violation:* 409 `ProviderInactive`.

**BR-12 — A PetGroomer booking must name a `serviceItemCode` from the provider's
own active menu.** The booked duration must equal that item's duration.
*Enforced:* Application. *Violations:* 400 `ServiceItemCodeRequired`, 400
`ServiceItemNotOffered`, 409 `ServiceItemInactive`, 400 `InvalidBookingTime`.

**BR-13 — A pet cannot hold two overlapping bookings on the same service.**
Only applies when the create names a `PetId`, so Custom walk-ins are unaffected.
*Enforced:* SQL, under the same lock as the capacity count.
*Violation:* 409 `PetAlreadyBooked`.

**BR-14 — A NightStay service cannot be booked through the single-day endpoint.**
*Enforced:* Application. *Violation:* 400 `UseNightStayEndpoint`.

**BR-15 — A booking freezes the provider's terms at creation.**
Price (unit rate), cancellation policy, and the selected-location address are
snapshotted onto the row, so a later provider edit never re-prices, re-rules or
re-addresses an existing booking. Only the *rate* is frozen — the total is always
rate × quantity.

---

## C. Acceptance

**BR-16 — Only the provider may accept or decline, and only from `CREATED`.**
Accept → `CONFIRMED`; decline → `PROVIDER_DECLINED` (terminal, frees the slot).

**BR-17 — A booking left in `CREATED` for 24+ hours can no longer be accepted.**
*Enforced:* SQL rejects the transition; **Job** writes the `EXPIRED` status —
sproc `Booking.ExpireStaleCreatedBookings`, called by `BookingSweepFunction`
(`Pawfront.Functions`) every 5 minutes.
*Violation:* 409 `BookingExpired`.
*Consequence:* between job runs a row can still read `CREATED` while the API
already refuses to act on it. Stored status and effective status diverge here by
design.

---

## D. Running the job

**BR-18 — A job is started from a confirmed-equivalent state only.**
Those are `CONFIRMED` plus the four post-modification resting states
(`PROVIDER_ACCEPTED_MODIFICATION`, `PROVIDER_DECLINED_MODIFICATION`,
`PARENT_ACCEPTED_MODIFICATION`, `PARENT_DECLINED_MODIFICATION`) — all five behave
identically.
*Violation:* 409 `BookingNotStartable`.

**BR-19 — A job can only be started on its service date.**
Today must equal `BookingDate` (night stay: `CheckInDate`). The time of day within
the booked window is deliberately **not** checked, so a provider running early or
late can still start.
*Enforced:* SQL. *Violation:* 409 `BookingNotOnServiceDate`.

**BR-20 — A job can only be started while the provider is inside their own weekly
working hours.** The break window is not consulted, and a provider who has never
saved working hours is not gated.
*Enforced:* SQL. *Violation:* 409 `OutsideWorkingHours`.

**BR-21 — Starting a job issues a start-OTP to the parent; the provider enters it.**
`START_JOB` → provider enters the code → `IN_PROGRESS`. One OTP, 6 digits, 10-minute
TTL, reused while valid. The parent reads it from their booking detail.
*Violations:* 400 `InvalidStartOtp`, 409 `StartOtpExpired`.

**BR-22 — Six wrong start-codes cancel the job.**
The 6th failure sets `OTP_MAX_ATTEMPTS_EXCEEDED` — terminal, frees the slot. A
dedicated status rather than a plain cancellation so both apps can label it
precisely.
*Enforced:* SQL. *Violation:* 409 `OtpAttemptsExceeded`.

**BR-23 — Completion is provider-only, from `IN_PROGRESS`, and needs no OTP.**
*Violation:* 409 `BookingNotCompletable`.

**BR-24 — Evidence photos are optional and never gate completion.**

**BR-25 — Only a Vet booking's provider may record a prescription, and only while
`IN_PROGRESS` or `COMPLETED`.**
*Enforced:* SQL. *Violations:* 400 `PrescriptionNotVetBooking`, 409
`PrescriptionNotAllowed`, 403 `Forbidden`.

---

## E. Modification

**BR-26 — Either party may propose a schedule change from a confirmed-equivalent
state; only the counterparty may answer it.**
Accept applies the new schedule to the **same booking id**; decline keeps the old
one. Either way the staging row is deleted and the booking returns to a
confirmed-equivalent resting state.
*Violations:* 409 `BookingNotModifiable`, 409 `NoPendingModification`.

**BR-27 — Only one proposal may be open per booking at a time.**
*Enforced:* SQL (UNIQUE). *Violation:* 409 `ModificationAlreadyPending`.

**BR-28 — Only date and time may be changed.** The service, the service item and
the pet are not editable through a modification.

**BR-29 — The modification window closes 2 hours before `serviceStart`, for
EITHER party's proposal** (widened 2026-08-02 to the provider side — previously
parent-only, see the retired BR-31 below).
Same cutoff instant as BR-01, so the two rules bracket a booking's life
symmetrically: creatable up to `serviceStart − 2h`, changeable up to the same point.
*Enforced:* SQL. *Violation:* 409 `ModificationWindowClosed`.

**BR-30 — An unanswered proposal — from either party — expires at that same
cutoff and the booking reverts to `CONFIRMED`** (widened 2026-08-02; previously
parent-only).
*Enforced:* SQL rejects a late response; **Job** performs the revert — sproc
`Booking.RevertExpiredModificationRequests`, called by
`BookingSweepFunction` (`Pawfront.Functions`) every 5 minutes, **before** BR-38's
sproc in the same tick. The sproc captures each reverted booking's actual
`FromStatus` (`MODIFICATION_REQUEST_BY_PARENT` or `..._BY_PROVIDER`) for the
audit row, rather than assuming which one applies.
*Violation:* 409 `ModificationRequestExpired`.
*Consequence:* between job runs, a booking past the cutoff sits in whichever
`MODIFICATION_REQUEST_BY_*` status it was in — which is **not** startable.

**BR-31 — Retired (2026-08-02).** Formerly: "the provider's proposals are not
time-boxed and never auto-expire." Superseded — BR-29 and BR-30 now apply
identically to both parties. Kept as a numbered entry per this file's own
retirement convention (ids are never reused).

**BR-32 — Accepting a proposal after the provider's terms have drifted requires an
explicit acknowledgement.** The requester is shown the drift
(`GET .../terms-changes`) and must set `acknowledgeTermsChanges`; the values they
were shown are staged and applied verbatim on accept, never re-read live.
*Enforced:* Application. *Violation:* 409 `BookingTermsChanged`.

---

## F. Cancellation

**BR-33 — Either party may cancel a live booking**, setting
`PROVIDER_CANCELLED` / `PARENT_CANCELLED`. Both are terminal and free the slot.

**BR-34 — A job that is underway cannot be cancelled.**
Once `IN_PROGRESS`, it runs through to `COMPLETED`.
*Enforced:* SQL. *Violation:* 409 `BookingInProgress`.

---

## G. No-shows

**BR-35 — A no-show always names the absent party.** The provider reports
`PARENT_NO_SHOW`; the parent reports `PROVIDER_NO_SHOW`. Both terminal, both free
the slot.

**BR-36 — A no-show is reportable from a confirmed-equivalent state or from
`START_JOB` — never once `IN_PROGRESS`.** By then both parties demonstrably met.
*Violation:* 409 `BookingNotStartable`.

**BR-37 — A no-show may only be reported once the counterparty is actually late.**
Single-day: **30+ minutes** after `serviceStart`. Night stay: **2+ hours** — a
boarding hand-over is a slower affair, so a 09:00 check-in is reportable from 11:00.
*Enforced:* SQL. *Violation:* 409 `NoShowTooEarly`.

**BR-38 — An accepted job that is still unstarted at the end of the provider's
working day settles itself as a no-show, and blame is read from evidence rather
than guessed.**
From `START_JOB` → `PARENT_NO_SHOW` (the code was issued, the parent never handed
it back); from confirmed-equivalent → `PROVIDER_NO_SHOW` (Start was never tapped).
Both terminal, both free the slot.
*Enforced:* **Job** — sproc `Booking.SettleUnstartedJobsAsNoShow`, called by
`BookingSweepFunction` (`Pawfront.Functions`) every 5 minutes, after BR-30's
sproc in the same tick. Reporting manually (BR-35) stays optional.

The cutoff is the **end of the provider's working day**, not the end of the booked
window (revised 2026-08-02):

- **Single-day** — the later of (a) the provider's closing time on the booking
  date and (b) the booking's own `EndTime`.
  - Keying off the provider's closing time rather than the booking's end is the
    point of the rule: a provider running badly late has not no-showed at 11:00
    just because a 10:00–11:00 slot came and went. They still have the rest of
    their day to serve it. Only when they close is the job definitively not
    happening.
  - Taking the **later** of the two matters when the provider has narrowed their
    hours since the booking was made — a 10:00–18:00 day care against 09:00–17:00
    hours must not be settled at 17:00 while its own window is still running. A
    no-show is never recorded over a booking that is still live.
  - Closing time comes from `Provider.ProviderWeeklyAvailability` for that date's
    weekday. **Fallback:** if the provider has no row for that weekday, or it is
    marked closed, use midnight UTC at the end of the booking date — the calendar
    day is the only "day" we can know about. The break window is not consulted.
- **Night stay** — unchanged: the check-in day ends (midnight UTC). A stay is
  date-granular and the weekly time grid deliberately never governs it (BR-06), so
  there is no "working day" to key off; midnight already is the end of the day.

*New data dependency:* the Job must join `Provider.ProviderWeeklyAvailability`
(weekday `0 = Sunday`, matching `(int)DateTime.DayOfWeek`) to resolve the cutoff.
The retired in-database sweep needed only the booking row.

*Consequence:* a job now settles later than it used to — up to the provider's
closing time rather than the booking's end time. A manual report (BR-35, legal
from 30 minutes after the start) therefore remains the fast path, and the Job is
the backstop for when neither party bothers.

---

## H. Payment

**BR-39 — Only the provider may mark a booking paid, and only from `COMPLETED`.**
Writes a `Booking.BookingPayments` ledger row and sets `PAID`.
*Violations:* 409 `BookingNotPayable`, 409 `BookingAlreadyPaid`.

**BR-40 — A Custom walk-in cannot be marked paid.** It is off-platform.
*Violation:* 400 `PaymentNotAppBooking`.

**BR-41 — The paid amount comes from the booking's price-locked total, never a
client-supplied figure.** Amount and Pawfront fee are computed server-side from
the frozen rate (BR-15).

**BR-42 — A private (Custom) job carries no Pawfront fee.** Commission is 0% for
off-platform work; App bookings use `Payments:PawfrontFeePercentage`.

---

## I. Capacity

**BR-43 — Capacity is per ServiceId, not per provider.** DayCare and NightStay
each get their own bucket. Grooming is the exception: capacity is shop-wide across
all menu items, so one groomer is one slot.
*Enforced:* SQL, race-safe. *Violation:* 409 `CapacityExceeded`.

**BR-44 — A booking holds its slot in every status except the capacity-freeing
set.** That set is: `PROVIDER_CANCELLED`, `PARENT_CANCELLED`, `PROVIDER_DECLINED`,
`PARENT_NO_SHOW`, `PROVIDER_NO_SHOW`, `EXPIRED`, `JOB_EXPIRED`,
`OTP_MAX_ATTEMPTS_EXCEEDED`. Note `COMPLETED` and `PAID` are terminal but **still
hold** the slot — a finished job consumed it.

**BR-45 — What the availability surfaces show is exactly what create will admit.**
The slot service uses the same half-open overlap count as the race-safe create
sproc. Fully-booked slots are shown with `remainingCapacity: 0`; slots barred by
BR-01 are omitted entirely.

---

## J. Terminal guards

**BR-46 — A terminal booking accepts no further status change.**
Terminal: `COMPLETED`, `PAID`, `PROVIDER_DECLINED`, `PROVIDER_CANCELLED`,
`PARENT_CANCELLED`, `PARENT_NO_SHOW`, `PROVIDER_NO_SHOW`, `EXPIRED`, `JOB_EXPIRED`,
`OTP_MAX_ATTEMPTS_EXCEEDED`.
*Violation:* 409 `BookingStatusTerminal`.
*Exception:* `COMPLETED → PAID` (BR-39), which runs through its own endpoint and
bypasses the status engine — as `/start-job` and the modification flows do.

**BR-47 — Re-setting the status a booking already holds is rejected.**
*Violation:* 409 `BookingStatusUnchanged`.

**BR-48 — Only a party to the booking may act on it, and the actor is taken from
the authenticated route, never the request body.**
*Violation:* 403 `Forbidden`.

**BR-49 — Every status change writes an append-only audit row.**
`Booking.BookingStatusHistory` / `NightStayBookingStatusHistory`, seeded at
creation. Automated changes are attributed to `System`.

---

## K. Retired rules and legacy statuses

**BR-50 — Retired.** Evidence photos once gated `COMPLETED`. Removed; see BR-24.

**BR-51 — Retired.** Completion once required a second "end" OTP (`ENDING` state).
Removed; see BR-23.

**BR-52 — Retired.** An accepted-but-unstarted job whose window elapsed once
became `JOB_EXPIRED`. It is now a no-show; see BR-38. `JOB_EXPIRED` stays a valid
terminal status because existing rows carry it, but nothing produces it.

**Legacy statuses**, valid so old rows stay readable, never settable:
`ENDING`, `JOB_STARTED`, `APPROVAL_NEEDED`, `JOB_EXPIRED`.

---

## Open questions

1. **BR-01's 2 hours is a hardcoded constant**, not per-provider configuration. A
   vet may want 24 hours' notice where a groomer takes near-walk-ins.
2. **The single-instance guarantee behind BR-17/BR-30/BR-38 depends on exactly
   one Function App being deployed** for `BookingSweepFunction`. The Azure
   Functions host serialises a timer trigger's invocations across however many
   instances *that one app* scales to — it does not protect against the same
   trigger being deployed a second time under a different app (which is exactly
   how the retired in-process sweep double-fired, once per API host).

