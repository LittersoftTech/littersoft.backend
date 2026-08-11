# Push notifications — mobile integration contract

Status: **engine built; every booking trigger in the Notifications V3 spec is
wired.** `MESSAGE_RECEIVED` was wired on 2026-08-09 with the chat module (see
`docs/chat.md`). The only types with no trigger are the three whose product
modules still do not exist — invoicing, disputes and promotional (see §3.4). This
document is the contract the two mobile apps code against.

## The `data` object

Every notification carries the same **canonical id block**, so a tap handler can
read a field without first checking whether this particular type carries it:

| key | meaning |
|---|---|
| `category` | `BOOKING` · `EVENT` · `MESSAGING` · `PROMOTIONAL` — the coarse bucket |
| `bookingId` | the service booking, when `category` is `BOOKING` |
| `eventId` | the event, when `category` is `EVENT` |
| `parentId` | the pet parent this concerns |
| `providerId` | the provider this concerns |
| `petId` | the pet, when the booking names one |
| `isNightStay` | `"true"` / `"false"` — which booking table `bookingId` points at |
| `payoutId` | the payout reference, on payment-related notifications |

**A field that does not apply is sent as an empty string, never omitted.** "Absent
key" and "key the server forgot to set" are indistinguishable to a client, so an
always-present key makes the contract self-describing: a blank is a definite *not
applicable*.

**`isNightStay` matters.** `Booking.Bookings` and `Booking.NightStayBookings` are
separate tables sharing no id space, so `bookingId` alone does not tell you which
detail screen to open.

`category` vs `type`: **`category` is the bucket, `type` is the exact event.**
Branch on `type` when you handle a notification specifically; fall back to
`category` when you don't recognise the type. That fallback is what makes adding
a new `type` a non-breaking change — see rule 4 in §1.

## 0. Worked example — "New Service Booking"

Fires to the **provider** when a parent creates a booking on the pet-parent host.
Shown in full because its accept-by deadline is the one piece of copy the app
must not recompute for itself.

| | Single-day | Night-stay |
|---|---|---|
| `type` | `BOOKING_REQUESTED` | `NIGHT_STAY_BOOKING_REQUESTED` |
| Title | `New Service Booking` | `New Service Booking` |
| Body | `John has requested Bath & Dry for Bruno on 5 Aug 2026. Respond within 24 hours.` | `John has requested Night Stay (3 nights) for Bruno on 5 Aug 2026. Respond within 24 hours.` |
| `entityType` | `Booking` | `NightStayBooking` |
| `bookingType` | `SingleDay` | `NightStay` |
| Date shown | the service date | the check-in date |

### The response window

The body quotes **how long the provider has**, not the deadline instant — that is
the one number they act on, and it is **not always 24 hours**:

- `Respond within 24 hours.` — the ordinary case.
- `Respond within 2 hours 15 minutes.` — a booking made close to its own service
  time, where the window is cut short.

**Why it varies.** Two rules expire an unaccepted booking and the deadline is
whichever fires **first**:

- **BR-17** — 24 hours after the booking was created.
- **BR-53** — when the service is less than 2 hours away, however recently it was
  made.

So a booking made at 09:45 for a service at 14:00 **the same day** must be
accepted by **12:00 that day**, not 09:45 tomorrow — a window of 2 hours 15
minutes. A flat "24 hours" would be a promise the server does not keep.

`respondWithin` is measured from the moment the booking was **created**, which is
what makes the ordinary case read as a clean "24 hours" rather than "23 hours 59
minutes"; the push leaves within about a minute of the booking. **For an exact
live countdown use `acceptByUtc`**, which carries the deadline itself as an
instant — the rendered strings are frozen at send time.

### `data` keys on these two

`parentName`, `petName`, `serviceName`, `respondWithin`, `acceptBy`,
`acceptByUtc`, `createdAtUtc`, `serviceStartUtc`, plus `serviceDate` +
`serviceDateWithYear` + `startTime` (single-day) or those three plus `checkInDate`
+ `dropOffTime` + `checkOutDate` + `checkOutUtc` (night-stay).

`serviceDateWithYear` (`5 Aug 2026`) is what the body uses here; `serviceDate`
(`5 Aug`) carries the same date in the short form the rest of the copy uses. Both
are sent.

`serviceName` is the specific grooming menu item when the booking names one
(`Nails Clipping`, `Bath & Dry`), otherwise the bookable service (`Day Care`,
`Vet Appointment`, `Training Session`, `Night Stay (3 nights)`) — for a stay the
number of nights is folded in, since the copy shows only the check-in date.

`parentName` is the customer's name; `A customer` is substituted if it cannot be
resolved, and `your pet` for a missing `petName`.

**Not fired** for provider-created bookings or Custom walk-ins — a provider
shouldn't be notified about their own action, and a walk-in has no parent.

> ⚠️ **Every date and time in the copy is SWISS local time** (`Europe/Zurich`),
> converted from UTC at send time — including across DST, so 14:00 UTC reads
> `16:00` in August and `15:00` in January. Until 2026-08-06 these strings were
> raw UTC and were therefore an hour or two wrong for every user.
>
> Alongside each display string the payload also carries the underlying **UTC
> instant** — `serviceStartUtc`, `checkOutUtc`, `newServiceStartUtc`,
> `newCheckOutUtc`, `closingAtUtc`, `acceptByUtc` — in
> `yyyy-MM-ddTHH:mm:ss` (UTC, no offset suffix) or ISO 8601 with an explicit
> offset. **Prefer the instant** whenever the app formats a time itself: the
> display strings are frozen at send time in the recipient's zone, whereas the
> instant lets the device render in whatever zone it is actually in.
>
> Every user is in Switzerland today, so the server applies one zone to everybody.
> When a per-user timezone lands on the profile, these strings will follow it
> automatically and the wire contract will not change.

---

## 1. What the app receives

Every push is a **hybrid `notification` + `data`** message.

- The **`notification`** block is what the OS displays. It arrives and renders
  even when the app is killed — you do not need a running handler for the user to
  see it.
- The **`data`** block is the routing contract. Read it in your tap handler to
  decide which screen to open.

```jsonc
{
  "notification": {
    "title": "Booking confirmed",
    "body": "Anna accepted your booking for Max on 5 Aug at 14:00."
  },
  "data": {
    // --- envelope: present on EVERY notification ---
    "v": "1",                                   // payload contract version
    "type": "BOOKING_ACCEPTED",                 // branch on this
    "route": "/bookings/detail",                // where a tap goes
    "entityType": "Booking",
    "entityId": "8f3c1d2e-....",
    "notificationId": "b71a-....",              // also the inbox row id
    "sentAtUtc": "2026-08-02T09:14:00.0000000+00:00",

    // --- canonical id block: present on EVERY notification ---
    "category": "BOOKING",
    "bookingId": "8f3c1d2e-....",
    "eventId": "",                              // empty = not applicable
    "parentId": "a11b2c3d-....",
    "providerId": "c92d4e5f-....",
    "petId": "7e45a8b9-....",
    "isNightStay": "false",
    "payoutId": "",

    // --- per-type parameters (vary by `type`) ---
    "bookingType": "SingleDay",                 // legacy twin of isNightStay
    "providerName": "Anna",
    "petName": "Max",
    "serviceStartUtc": "2026-08-05T12:00:00",   // UTC instant — format it yourself
    "serviceDate": "5 Aug",                     // already Swiss local
    "startTime": "14:00"                        // already Swiss local
  },
  "android": {
    "notification": {
      "channel_id": "pawfront_default",
      "icon": "ic_notification",
      "color": "#FF7A00"
    }
  },
  "apns": { "payload": { "aps": { "sound": "default", "content-available": 1 } } }
}
```

### Rules worth knowing before you write the handler

1. **Every `data` value is a string.** FCM cannot carry numbers, booleans or
   nested objects. `"v": "1"` is a string; so is any count. Parse accordingly.
2. **Branch on `type`, navigate with `route`.** `type` is a stable machine key
   and will never be reworded. Copy in `title`/`body` **can** change without
   notice — never parse it.
3. **`route` is a path only.** Ids are separate `data` keys, so you never have to
   string-parse a path to get an id.
4. **Unknown `type` must be survivable.** New types will be added. Fall back to
   `category` — open the booking, the event, or the inbox — rather than crashing
   or ignoring the tap.
5. **The same `type` can be worded differently in the two apps.** A notification
   mirrored to both parties (a reminder, a no-show, a modification expiry) is ONE
   event with ONE `type`, rendered per audience: the parent is told to bring
   cash, the provider to ask for the OTP. Do not assume identical copy on both
   sides, and do not treat the two as different events.
6. **Display times are already Swiss local; the `*Utc` keys are not.** Show
   `startTime` / `serviceDate` as-is, but if you reformat a time yourself, read
   `serviceStartUtc` (and friends) and convert on the device — do not convert a
   display string, and do not treat one as UTC.
7. **`v` will only change on a breaking payload change.** Guard on it if you
   want; today it is always `"1"`.

---

## 2. What we need FROM the mobile team

These are the blocking inputs. The backend is built and waiting on them.

### 2.1 The route table ⛔ blocking

`src/Pawfront.Application/Notifications/NotificationRoutes.cs` currently holds
**placeholders**. Send us the real deep-link string your router expects for each,
in whatever form you use (`/bookings/detail`, `pawfront://booking`, a named route
— we'll send verbatim whatever you give us).

| Placeholder today | What it must open |
|---|---|
| `/bookings/detail` | Single-day booking detail |
| `/night-stay-bookings/detail` | Night-stay (multi-night boarding) detail |
| `/bookings/requests` | Provider's incoming requests list (accept/decline) |
| `/bookings/modification` | Pending reschedule proposal review |
| `/bookings/start-code` | Parent's screen showing the start OTP |
| `/bookings/prescription` | Vet prescription on a completed booking |
| `/events/detail` | Public event detail |
| `/event-bookings/detail` | A ticket booking's detail |
| `/events/dashboard` | Organiser's attendee/metrics dashboard |

### 2.2 Android channels ⛔ blocking

**Android 8+ silently drops a notification whose `channel_id` the app has not
created.** No error, nothing in the tray — the single most common cause of "the
server says sent but nothing arrived."

Tell us either:
- one channel id to use for everything (currently defaulted to
  `pawfront_default`), or
- a channel per category (e.g. `pawfront_bookings`, `pawfront_events`) — say
  which types map to which and we'll wire it per template.

Also confirm the **drawable resource name** for the small icon (currently
`ic_notification`) and the accent **colour** (currently `#FF7A00`). The icon must
be a monochrome drawable already bundled in the app — the server can only name a
resource, it cannot send an icon image.

### 2.3 iOS — APNs auth key ⛔ blocking (ops, not code)

Upload an APNs authentication key to **both** Firebase projects
(`littersoftprovider` and `pawfrontparent-89296`), under
Project Settings → Cloud Messaging. Without it iOS delivery fails silently — FCM
reports success and nothing arrives.

If you want rich images on iOS you also need a **Notification Service
Extension** in the app. Tell us if you plan to; images are optional and off by
default.

### 2.4 Token registration ✅ built

`POST /device-tokens` (register/refresh) and `POST /device-tokens/deactivate`
(sign-out) exist on both hosts. **FCM tokens rotate**, so call the register
endpoint on every app launch AND from Firebase's `onTokenRefresh` — a rotated
token that is never reported means the device silently stops receiving anything.

---

## 3. Notification types

Every type below is defined, renderable and — except where §3.4 says otherwise —
**wired to a real trigger**. The `V3 card` column cites the card in the
Notifications V3 spec, so the two documents can be checked against each other.

### 3.1 To the pet parent — the provider acted

| `type` | When | V3 card |
|---|---|---|
| `BOOKING_ACCEPTED` | Provider accepted | P-U1 |
| `BOOKING_DECLINED` | Provider rejected | P-U2 |
| `BOOKING_CANCELLED_BY_PROVIDER` | Provider cancelled a confirmed booking | P-U3 |
| `BOOKING_MODIFICATION_REQUESTED_BY_PROVIDER` | Provider proposed a new time | P-U4 |
| `BOOKING_MODIFICATION_ACCEPTED_BY_PROVIDER` | Your proposal was accepted | P-U5 |
| `BOOKING_MODIFICATION_DECLINED_BY_PROVIDER` | Your proposal was declined | P-U6 |
| `BOOKING_START_OTP_ISSUED` | Provider tapped Start — show the start code | P-U7 |
| `BOOKING_IN_PROGRESS` | Provider entered the code; the job is under way | P-U8 |
| `BOOKING_COMPLETED` | Job done — collect the pet and pay | P-U11 / P-U12 |
| `BOOKING_PAID` | Provider confirmed receiving the cash | P-U13 |
| `BOOKING_PRESCRIPTION_RECORDED` | Vet added a prescription | *(not in V3)* |

### 3.2 To the provider — the parent acted

| `type` | When | V3 card |
|---|---|---|
| `BOOKING_REQUESTED` | Parent booked a single-day service | V-S1 |
| `NIGHT_STAY_BOOKING_REQUESTED` | Parent booked a multi-night stay | V-S1 |
| `BOOKING_CANCELLED_BY_PARENT` | Parent cancelled | V-U1 |
| `BOOKING_MODIFICATION_REQUESTED_BY_PARENT` | Parent proposed a new time | V-U2 |
| `BOOKING_MODIFICATION_ACCEPTED_BY_PARENT` | Your proposal was accepted | V-U3 |
| `BOOKING_MODIFICATION_DECLINED_BY_PARENT` | Your proposal was declined | V-U4 |
| `BOOKING_START_OTP_NOT_ENTERED` | You still haven't entered the customer's code | V-S10 |
| `BOOKING_NOT_MARKED_COMPLETE` | Past closing time, job still not completed | V-S12 |

### 3.3 System-driven

Timer- or state-derived. **Copy differs per app** where the card exists in both —
one `type`, two renderings (see rule 5 in §1).

| `type` | Audience | When | V3 card |
|---|---|---|---|
| `BOOKING_EXPIRED_FOR_PARENT` | Parent | Nobody accepted in time (24h, or <2h to service) | P-S1 |
| `BOOKING_EXPIRED_FOR_PROVIDER` | Provider | Ran out of time to accept — job lost | V-S2 |
| `BOOKING_MODIFICATION_TIMED_OUT` | Both | Proposal unanswered for 24 hours | P-S2 / V-S3 |
| `BOOKING_MODIFICATION_EXPIRED` | Both | Proposal hit the 2h-before-service cutoff | P-S3 / V-S4 |
| `BOOKING_REMINDER_DAY_BEFORE` | Both | T-24h before the service | P-S4 / V-S5 |
| `BOOKING_REMINDER_STARTING_SOON` | Both | T-5min before the service | P-S5 / V-S6 |
| `BOOKING_NOT_STARTED_HALFWAY` | Both | Half-way through the window, not started | P-S6 / V-S7 |
| `BOOKING_NOT_STARTED_WINDOW_ENDED` | Both | Whole window elapsed, not started | P-S7 / V-S8 |
| `BOOKING_START_OTP_NOT_SEEN` | Parent | You haven't opened your start code yet | P-S9 |
| `BOOKING_START_OTP_NOT_SHARED` | Parent | You have the code; it still hasn't been entered | P-S10 |
| `BOOKING_OTP_ATTEMPTS_EXCEEDED` | Parent | Six wrong start codes cancelled the job | P-S11 |
| `BOOKING_NO_SHOW_REPORTED` | Counterparty | A party reported the other absent | P-U9 / P-U10 |
| `BOOKING_NO_SHOW_AUTO_SETTLED` | Both | The system derived the no-show | P-S8 / V-S9 · P-S12 / V-S11 |
| `BOOKING_COMPLETION_TIME_PASSED` | Parent | Job's end time passed, still running | P-S13 |
| `BOOKING_PICKUP_OVERDUE` | Parent | Provider is closing, pet still there | P-S14 |

**Two no-show types, on purpose.** `_REPORTED` goes to the counterparty ONLY —
the party who tapped it already saw the result. `_AUTO_SETTLED` goes to BOTH,
because nobody tapped anything. Both carry `absentParty` naming who was missing.

**Two modification-expiry types, on purpose.** Exactly one fires per proposal,
whichever deadline arrives first. They read differently to a user ("nobody
replied in a day" vs "modifications close 2 hours before the service"), so they
are separate keys rather than one with a reason code.

### 3.4 Defined but NOT wired — module does not exist

These have a type, copy, a route and the full `data` contract, so you can build
against them now. **Nothing enqueues them**, because the backend has no such
feature yet.

> `MESSAGE_RECEIVED` used to be listed here. It is now wired — enqueued by
> `Chat.CommitMessageAppend`, and only when the recipient has no live connection viewing
> that thread, so an open chat never buzzes. It is also the one type normally sent
> by an API host rather than the scheduled dispatcher: its outbox row is written
> pre-claimed so the timer skips it, and the dispatcher only takes over if the
> chat host dies mid-send. Its `data` carries `conversationId` (now part of the
> canonical id block) and `senderName`. See `docs/chat.md`.

| `type` | `category` | Blocked on | V3 card |
|---|---|---|---|
| `INVOICE_ISSUED` | `BOOKING` | No invoicing | P-S16 / V-S14 |
| `DISPUTE_RESOLVED` | `BOOKING` | No Helpline / ticket module | P-S15 / V-S13 |
| `PROMOTIONAL_MESSAGE` | `PROMOTIONAL` | No campaign module | *(not in V3)* |

`INVOICE_ISSUED` carries `issuedBy`, which is what differs between the two apps:
the provider's name on the parent's copy, "Littersoft / Pawfront" on the
provider's. `PROMOTIONAL_MESSAGE` takes its `title`/`body` from the caller rather
than a template, since campaign wording is the point of a campaign.

### 3.5 Event tickets

Not covered by the V3 services spec. **The two organiser-side types are wired**;
the four buyer-side ones are not, for the two distinct reasons below.

| `type` | Audience | When | Status |
|---|---|---|---|
| `EVENT_TICKET_SOLD` | Organiser | Somebody bought tickets | **wired** |
| `EVENT_TICKET_CANCELLED` | Organiser | An attendee cancelled, seats freed | **wired** |
| `EVENT_BOOKING_CONFIRMED` | Buyer | Tickets booked | not sent — own action |
| `EVENT_BOOKING_CANCELLED` | Buyer | Tickets cancelled | not sent — own action |
| `EVENT_BOOKING_PAYMENT_SUCCEEDED` | Buyer | Gateway confirmed payment | blocked — see below |
| `EVENT_BOOKING_PAYMENT_FAILED` | Buyer | Payment failed | blocked — see below |

**Why the organiser side works and the buyer side doesn't.** `Event.Events`
carries exactly one of `ProviderId` / `PetParentId`, so the organiser is a real id
and that choice also *is* the audience — no ambiguity. `Event.EventBookings`, by
contrast, identifies its booker only by free-text `BookerEmail` with **no FK to
any user table**, so there is no id to push to. The payment webhook has no JWT
either. Addressing the buyer needs a persisted booker id on the booking row
first; until then those two types stay defined but unsent.

**Why the two "own action" ones are not sent.** Booking and cancelling tickets are
things the buyer just did on screen — the same relevance rule that drops "Booking
Request Sent" in the V3 spec. The types remain in the catalog in case a receipt is
wanted later; nothing enqueues them today.

The `data` object for the wired pair carries `category: "EVENT"`, a populated
`eventId`, the organiser in `parentId` **or** `providerId` (whichever they are),
plus `eventBookingId`, `eventTitle`, `parentName` (the buyer's name, free text) and
`ticketCount`. `bookingId`, `petId`, `isNightStay` and `payoutId` are empty
strings — this is not a service booking.

---

## 4. In-app inbox

Every notification is also an inbox row — the outbox table is the read model, so
nothing is stored twice and the bell list always matches what was pushed.

Endpoints land in phase 7 (`GET /notifications`,
`POST /notifications/{id}/read`). The SQL behind them
(`Notification.ListNotifications`, `Notification.MarkNotificationsRead`) is
already deployed-ready.

Two behaviours to expect:

- A notification whose recipient had **no active device** is still in the inbox
  (status `NoDevice`). It happened; it just had nowhere to be pushed. Do not
  treat the inbox as "things that were delivered."
- A row appears in the inbox only once the dispatcher has rendered it — up to
  ~1 minute after the event. It is never shown blank.

---

## 5. Delivery timing

Notifications are queued in the same database transaction as the change that
caused them, then dispatched by a timer job that runs **every minute**. So
expect **up to ~60 seconds** from event to device.

Time-driven notifications have a second delay on top of that — how long until the
job that *notices* them runs:

| Producer | Cadence | What it raises |
|---|---|---|
| API hosts (booking sprocs) | immediate | everything in §3.1 / §3.2 |
| `BookingReminderFunction` | **1 minute** | the reminders and nudges in §3.3 |
| `BookingSweepFunction` | 5 minutes | expiry, no-show settle, modification revert |

The reminder job runs every minute specifically so `BOOKING_REMINDER_STARTING_SOON`
means what it says; on the 5-minute sweep it could have landed anywhere from 0 to
5 minutes before the service.

**Reminders can arrive a little late but never twice.** Re-firing is prevented by
a unique key on the outbox row rather than by a flag on the booking, so a missed
tick is self-healing and a duplicate tick is a no-op.

This is deliberate. Booking statuses are changed by three separate processes —
the provider API, the parent API, and a scheduled sweep — and only a durable
queue can carry all three with retries. If a specific notification needs to be
near-instant (the start-OTP is the likeliest candidate), say so and we'll add an
inline dispatch nudge for that path, keeping the timer as the retry backstop.

Failed sends retry with exponential backoff (2/4/8/16/32 minutes) up to five
attempts before being abandoned.
