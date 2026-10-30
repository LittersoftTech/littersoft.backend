# Push notifications — mobile integration contract

Status: **Services V4 and Events V3 implemented.** Exact copy, scenario selection,
intentional delays and rollout details are documented in
[Services notifications V4](services-notifications-v4.md). Invoice and service
support-resolution triggers are wired; promotional messages remain caller-supplied.
Stable machine types are retained, with `notificationCode` identifying the selected card.

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

## 0. Worked example — "New Job Request Received"

Fires to the **provider** when a parent creates a booking on the pet-parent host.
Both booking kinds use card V-S1. Their exact response deadlines remain in the
payload for the app to display; the shortened notification no longer quotes them.

| | Single-day | Night-stay |
|---|---|---|
| `type` | `BOOKING_REQUESTED` | `NIGHT_STAY_BOOKING_REQUESTED` |
| Title | `New Job Request Received` | `New Job Request Received` |
| Body | `John has requested Bath & Dry on 5 Aug. Tap to review!` | `John has requested Night Stay (3 nights) on 5 Aug. Tap to review!` |
| `entityType` | `Booking` | `NightStayBooking` |
| `bookingType` | `SingleDay` | `NightStay` |
| Date shown | the service date | the check-in date |

### The response window

`acceptByUtc`, `createdAtUtc`, and derived `respondWithin` remain available. Use
the deadline from the backend for a live countdown. V4's visible message says
“Tap to review!” for both ordinary and short-notice bookings. Notification copy
changes do not change the booking acceptance rules in [booking rules](booking-rules.md).

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

> ⚠️ **Times in the copy are the SAME readings the API returns — show them as-is,
> and do NOT convert them** (corrected 2026-08-11).
>
> A booking's schedule is a wall-clock reading, not an instant: `bookingDate` +
> `startTime` are exactly what `/availability/slots`, the agenda and every booking
> read hand you, and what the provider typed into their availability screen. So
> `startTime` in a notification is the same string as `startTime` on the booking
> detail, always.
>
> Between 2026-08-06 and 2026-08-11 the server shifted these into Swiss local on
> the theory that the stored values were UTC. They are not, so a 14:00 job was
> announced as `16:00` — right date, wrong time, and the error changed size with
> DST. **If your app added an offset of its own to compensate, remove it**: doing
> both is how you land two hours out in the other direction.
>
> Alongside each display string the payload still carries the same reading in
> `yyyy-MM-ddTHH:mm:ss` form — `serviceStartUtc`, `checkOutUtc`,
> `newServiceStartUtc`, `newCheckOutUtc`, `closingAtUtc`, `acceptByUtc`,
> `reviewByUtc`, `requestedAtUtc`. Use those when you need to compute with a value
> (sort, diff, count down) rather than print it; treat them as the same clock the
> rest of the API is on. The `Utc` in the key names is historical.
>
> Every user is in Switzerland today, so one clock serves everybody. If a per-user
> timezone ever lands on the profile, the server will convert into it and the wire
> contract will not change.

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
    "title": "Booking Confirmed",
    "body": "Anna accepted your Grooming booking. Tap to view!"
  },
  "data": {
    // --- envelope: present on EVERY notification ---
    "v": "1",                                   // payload contract version
    "type": "BOOKING_ACCEPTED",                 // branch on this
    "notificationCode": "P-U1",                  // selected V4 card
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
    "serviceStartUtc": "2026-08-05T14:00:00",   // same reading, computable form
    "serviceDate": "5 Aug",                     // print as-is
    "startTime": "14:00"                        // print as-is — matches the booking detail
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
   event with ONE `type`, rendered per audience: each receives the card
   for their role and the reason the event occurred. Do not assume identical copy on both
   sides, and do not treat the two as different events.
6. **Never convert a time.** `startTime` / `serviceDate` are the same readings the
   booking endpoints return — print them. The `*Utc` twins carry the same reading
   in a sortable form for when you need to compute rather than print. Applying a
   timezone offset to either one is what puts a notification an hour or two out of
   step with the screen it links to.
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
**wired to a real trigger**. The `V4 card` column identifies the visible Services V4 card. Old wire types
remain supported when two causes now share the same card.

### 3.1 To the pet parent — the provider acted

| `type` | When | V4 card |
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
| `BOOKING_CASH_NOT_RECEIVED` | Unpaid completed job with a provider Cash Not Received ticket | P-U15 |
| `BOOKING_PRESCRIPTION_RECORDED` | Retired: no new delivery | — |

### 3.2 To the provider — the parent acted

| `type` | When | V4 card |
|---|---|---|
| `BOOKING_REQUESTED` | Parent booked a single-day service | V-S1 |
| `NIGHT_STAY_BOOKING_REQUESTED` | Parent booked a multi-night stay | V-S1 |
| `BOOKING_CANCELLED_BY_PARENT` | Parent cancelled | V-U1 |
| `BOOKING_MODIFICATION_REQUESTED_BY_PARENT` | Parent proposed a new time — carries `reviewWithin`, see §3.6 | V-U2 |
| `BOOKING_MODIFICATION_ACCEPTED_BY_PARENT` | Your proposal was accepted | V-U3 |
| `BOOKING_MODIFICATION_DECLINED_BY_PARENT` | Your proposal was declined | V-U4 |
| `BOOKING_START_OTP_NOT_ENTERED` | You still haven't entered the customer's code | V-S10 |
| `BOOKING_NOT_MARKED_COMPLETE` | Ten minutes before accepted closing, still in progress and late for collection | V-S12 |
| `BOOKING_COMPLETION_OVERDUE` | Scheduled end plus ten minutes, OTP-started and still in progress | V-S15 |

### 3.3 System-driven

Timer- or state-derived. **Copy differs per app** where the card exists in both —
one `type`, two renderings (see rule 5 in §1).

| `type` | Audience | When | V4 card |
|---|---|---|---|
| `BOOKING_EXPIRED_FOR_PARENT` | Parent | Nobody accepted in time (24h, or <2h to service) | P-S1 |
| `BOOKING_EXPIRED_FOR_PROVIDER` | Provider | Ran out of time to accept — job lost | V-S2 |
| `BOOKING_MODIFICATION_TIMED_OUT` | Both | Proposal unanswered for 24 hours | P-S2 / V-S3 |
| `BOOKING_MODIFICATION_EXPIRED` | Both | Proposal hit the pre-service cutoff; same card as timeout | P-S2 / V-S3 |
| `BOOKING_REMINDER_DAY_BEFORE` | Both | The service is on **tomorrow's calendar day**, from T-24h | P-S4 / V-S5 |
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

**Modification expiry is one visible card per audience.** The existing timeout
and cut-off machine types both render P-S2 / V-S3. Exactly one cause fires per proposal.

### 3.4 Chat, invoices, support and promotional messages

- `MESSAGE_RECEIVED` renders P-U14 / V-U5 with the actual sender's name and
  “Sent you a message.” Chat's immediate delivery and active-thread suppression
  are unchanged; see [chat](chat.md).
- `DISPUTE_RESOLVED` renders P-S15 / V-S13 when `Support.CloseTicket` closes a
  booking ticket. Only the raiser receives it, with `ticketId` and a 60-second delay.
- `INVOICE_ISSUED` renders P-S16 / V-S14. Both recipients are notified from
  `Billing.CompleteInvoiceGeneration` after their own PDF is durable, followed
  by a 60-second intentional delay. Both open `/invoices/detail`; the provider's
  old mark-paid/booking-summary notification is replaced. `invoiceId` is the
  document number, while booking identity and `isNightStay` select the existing
  invoice-download endpoint. A failed render produces no invoice-ready notice.
- `PROMOTIONAL_MESSAGE` stays a caller-supplied title/body contract; no campaign
  module is introduced by the notification changes.

### 3.5 Events V3

All 21 live Events V3 notifications are implemented. See
[event-notifications-v3.md](event-notifications-v3.md) for the code/type mapping,
recipients, exact timing, routes, payload fields, configuration and rollout.

The host gets first booking, sold out and event unlocked notices instead of a
notification for every booking/cancellation. Attendees get host cancellation,
cash status, reminders, attendance outcomes, flag changes and support resolution.
Free and paid events have separate live/completion copy where required.
User actions do not send self-notifications. The retired legacy event types stay
in the catalog for compatibility; new event notifications use the V3 types.


---

### 3.6 The modification review window — `reviewWithin`

V4 modification prompts say “Tap to review!” rather than quoting a duration.
`reviewByUtc`, `requestedAtUtc`, and derived `reviewWithin` remain in the payload.
Use `reviewByUtc` for the live response deadline. Existing acceptance/modification
rules are preserved; see [booking rules](booking-rules.md).

## 4. In-app inbox

The mobile apps can read their notification lists directly from **Firestore** or
through `GET /api/v1/notifications` on their respective API host. See the
[notification list API](notification-list-api.md) for authentication and pagination.
Mark-read updates use Firestore directly. Each Firebase project contains its own
`users/{firebaseUid}/notifications/{notificationId}` collection. The shared
background push sender copies the rendered notification and complete push data
map, with a server creation timestamp and `isRead: false`. The app updates
`isRead` directly under owner-only security rules.

Firestore writes are best effort and create-only, preserving read state on
retries. They mirror attempted pushes, including failed FCM attempts. Notifications
skipped before sending (for example, no active devices) remain in SQL but do not
create Firestore documents. Existing SQL inbox procedures remain available and
their read state is independent of Firestore.

See [Firestore notification setup](firestore-notifications.md) for deployment
order, permissions, rules, failure behavior, and verification.

---

## 5. Delivery timing

`NotificationDispatchFunction` drains eligible outbox rows every 15 seconds.
“Immediate” means no intentional hold; timer and transport latency still apply.
`BookingReminderFunction` stages due reminders every minute, while the booking
settlement sweep retains its existing cadence. Chat uses its immediate sender.

Services V4's 0/15/60-second intentional waits are configured in
`Notification.ServiceNotificationDelivery`. Enqueueing sets the earliest delivery
instant, and SQL inbox text and Firestore documents are not created before dispatch.
Re-deployments insert missing policy rows without overwriting configured delays.
Events retain their separate [Events V3 policy](event-notifications-v3.md), including
09:00 holds and the attendance thank-you at scheduled event end plus 24 hours.

V-S12 triggers ten minutes before accepted closing when the job is still in progress
and its scheduled end has passed. V-S15 triggers ten minutes after scheduled end
only for an OTP-started, unfinished job (night stays use their scheduled pickup).
They use distinct dedupe keys and can both be due. Pending completion/pickup reminders
are rechecked against booking state before dispatch; completed jobs retire them.

Failed sends retain the existing retry policy. No historical inbox deletion is
part of this change. Deploy the consolidated database script before updated hosts.
