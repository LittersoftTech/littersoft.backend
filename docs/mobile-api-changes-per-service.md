# Mobile API changes — per-service catalog, closures, bookings, profile

**Audience:** Pawfront mobile developers
**Status:** Shipped on backend; mobile must adopt to keep working.
**Scope:** Breaking changes to closures + bookings + slot queries; one new
catalog endpoint; one new profile endpoint; one optional query param on
the bookings list.

---

## 1. Why this change?

### The old model was provider-keyed
Closures, bookings, and slot queries all hung off **`providerId`** and were
backed by per-provider rows. That model worked while every provider
effectively offered one bookable thing.

### PetSitter broke that assumption
A PetSitter provider can offer **DayCare** *and* **NightStay** at the same
time. Under the old model:

- A closure created on a PetSitter provider blocked **both** DayCare and
  NightStay (no way to close just one).
- Capacity was a single bucket shared across both — a fully-booked DayCare
  morning would refuse a NightStay booking that evening.
- The slot endpoint returned one grid; the client couldn't ask "what
  DayCare slots are available on Friday?" vs. "what NightStay slots are
  available on Friday?".

### The new model is service-keyed
Each service the provider offers (DayCare, NightStay, GroomingSession,
TrainingSession, VetAppointment) has its own **`serviceId` (GUID)**.
Closures, bookings, capacity, and slot queries are all scoped by that id.
DayCare and NightStay on the same provider are completely independent —
own closures, own capacity, own slot grids.

The backend creates the matching `serviceId`s automatically the moment a
provider saves an offering. The mobile app just needs to **read them
back** before doing anything service-scoped.

### What happened to `POST/GET /providers/{providerId}/services`?
Those two endpoints were **legacy in-memory placeholders** from the very
first scaffold — they took an ad-hoc `{ name, description, basePrice,
durationMinutes }` body, weren't persisted to SQL, didn't relate to the
real category/sub-category/offering model, and weren't referenced by
bookings or closures. **They were never the right primitive.** They have
been **removed** (404 if you call them now).

The same route, `GET /providers/{providerId}/services`, has been **reused**
for the real per-service catalog described below.

---

## 2. NEW: `GET /providers/{providerId}/services` — service catalog

Read the list of `serviceId`s the provider currently offers. Call this:

- **After** any successful offering save (`POST .../offering` on any
  category) — the backend will have created/updated catalog rows; you
  need their ids to drive bookings, closures, and slots.
- **On app launch** (or on session resume) for a logged-in provider — so
  the home screen, calendar, and closure UI know which services exist.
- Whenever the user toggles a sub-offering (e.g. PetSitter enables/
  disables NightStay).

### Request

```
GET /api/v1/providers/{providerId}/services
GET /api/v1/providers/{providerId}/services?includeInactive=true
```

`includeInactive=true` includes services the provider previously offered
but later removed — only needed for historical screens (e.g. showing a
past booking's service name).

### Response

```json
{
  "success": true,
  "data": {
    "providerId": "11111111-1111-1111-1111-111111111111",
    "services": [
      {
        "serviceId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        "providerId": "11111111-1111-1111-1111-111111111111",
        "serviceCategory": "PetSitter",
        "subCategory": "PetHotel",
        "serviceType": "DayCare",
        "isActive": true,
        "createdAtUtc": "2026-05-21T09:14:23+00:00",
        "updatedAtUtc": "2026-05-21T09:14:23+00:00"
      },
      {
        "serviceId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
        "providerId": "11111111-1111-1111-1111-111111111111",
        "serviceCategory": "PetSitter",
        "subCategory": "PetHotel",
        "serviceType": "NightStay",
        "isActive": true,
        "createdAtUtc": "2026-05-21T09:14:23+00:00",
        "updatedAtUtc": "2026-05-21T09:14:23+00:00"
      }
    ]
  },
  "error": null
}
```

### `serviceType` enum (server-canonical strings)

| Category              | Allowed `serviceType`(s)               |
|-----------------------|----------------------------------------|
| `PetSitter`           | `DayCare`, `NightStay` (either or both)|
| `PetGroomer`          | `GroomingSession`                      |
| `PetTrainer`          | `TrainingSession`                      |
| `Vet`                 | `VetAppointment`                       |
| `PetAdoptionAndSale`  | *(no offering → no row in catalog)*    |

**Mobile guidance:** persist these `serviceId`s in your local store keyed
by `serviceType`. The home screen "Close Day Care" button only needs to
know which `serviceId` to send.

---

## 3. CHANGED: Closure APIs are per-service

### 3.1 `POST /providers/{providerId}/closures`

**The body now takes an array of `serviceIds`.** Mobile must send at
least one. One closure row is persisted **per service id** in a single
all-or-nothing transaction.

#### Request body

Close DayCare for one full day:

```json
{
  "serviceIds": ["aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"],
  "startDate": "2026-06-01",
  "endDate":   "2026-06-01",
  "startTime": null,
  "endTime":   null,
  "reason":    "Sick leave"
}
```

Close DayCare **and** NightStay together for a week-long vacation:

```json
{
  "serviceIds": [
    "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
  ],
  "startDate": "2026-07-10",
  "endDate":   "2026-07-15",
  "startTime": null,
  "endTime":   null,
  "reason":    "Family vacation"
}
```

Partial-day block (e.g. vet leaving at 2 PM for an appointment):

```json
{
  "serviceIds": ["cccccccc-cccc-cccc-cccc-cccccccccccc"],
  "startDate": "2026-06-05",
  "endDate":   "2026-06-05",
  "startTime": "14:00:00",
  "endTime":   "17:00:00",
  "reason":    "Personal appointment"
}
```

Rules:
- `serviceIds` is **required and non-empty**.
- Every id must belong to the provider and be active (else
  `400 InvalidServiceId`).
- Both `startTime` and `endTime` set together OR both `null`. Both `null`
  = full-day across the date range.
- Partial-day windows require `startDate == endDate`.
- `reason` is optional, max 500 chars.

#### Response — success path (status `Created`)

```json
{
  "success": true,
  "data": {
    "status": "Created",
    "closures": [
      {
        "closureId":  "dddddddd-dddd-dddd-dddd-dddddddddddd",
        "providerId": "11111111-1111-1111-1111-111111111111",
        "serviceId":  "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        "startDate":  "2026-07-10",
        "endDate":    "2026-07-15",
        "startTime":  null,
        "endTime":    null,
        "reason":     "Family vacation",
        "createdAtUtc": "2026-05-21T09:30:00+00:00"
      },
      {
        "closureId":  "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
        "providerId": "11111111-1111-1111-1111-111111111111",
        "serviceId":  "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
        "startDate":  "2026-07-10",
        "endDate":    "2026-07-15",
        "startTime":  null,
        "endTime":    null,
        "reason":     "Family vacation",
        "createdAtUtc": "2026-05-21T09:30:00+00:00"
      }
    ],
    "conflictingBookings": null,
    "warningMessage": null
  },
  "error": null
}
```

> **Diff vs. old:** the old response had a singular `closure` object;
> the new one has an array of `closures` (one entry per requested
> `serviceId`).

#### Response — booking conflicts (status `BookingsExist`, HTTP still 200)

If ANY of the targeted services has a confirmed booking inside the
requested window, the **entire batch** is rejected — no rows inserted.
Mobile must show the warning, let the user move/cancel the listed
bookings, and retry.

```json
{
  "success": true,
  "data": {
    "status": "BookingsExist",
    "closures": null,
    "conflictingBookings": [
      {
        "serviceId":   "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        "bookingId":   "ffffffff-ffff-ffff-ffff-ffffffffffff",
        "petParentId": "22222222-2222-2222-2222-222222222222",
        "bookingDate": "2026-06-01",
        "startTime":   "10:00:00",
        "endTime":     "12:00:00"
      }
    ],
    "warningMessage": "1 existing booking(s) inside the requested closure window across the targeted service(s). Please move or cancel these bookings before closing the service(s)."
  },
  "error": null
}
```

Each conflict now carries `serviceId` so the UI can group conflicts by
service.

#### Error responses

| HTTP | Error code             | When |
|------|------------------------|------|
| 400  | `InvalidRequest`       | `serviceIds` missing/empty, malformed dates, partial-day across multiple days, duplicate ids, reason too long. |
| 400  | `InvalidServiceId`     | One or more `serviceIds` don't belong to the provider or are inactive. |
| 404  | `ProviderProfileNotFound` | Provider row doesn't exist. |

### 3.2 `GET /providers/{providerId}/closures`

Same route as before, with one **new optional query param**:

- `serviceId` — narrows to closures on one specific service.

Existing `from` and `to` filters still work and remain optional.

```
GET /api/v1/providers/{providerId}/closures?from=2026-01-01&to=2026-12-31
GET /api/v1/providers/{providerId}/closures?serviceId={daycareId}&from=2026-01-01&to=2026-12-31
```

Response — each row now includes `serviceId`:

```json
{
  "success": true,
  "data": {
    "providerId": "11111111-1111-1111-1111-111111111111",
    "closures": [
      {
        "closureId":  "dddddddd-dddd-dddd-dddd-dddddddddddd",
        "providerId": "11111111-1111-1111-1111-111111111111",
        "serviceId":  "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
        "startDate":  "2026-07-10",
        "endDate":    "2026-07-15",
        "startTime":  null,
        "endTime":    null,
        "reason":     "Family vacation",
        "createdAtUtc": "2026-05-21T09:30:00+00:00"
      }
    ]
  },
  "error": null
}
```

### 3.3 `DELETE /providers/{providerId}/closures/{closureId}`

Unchanged signature. **One important behavioural note:** if a provider
closed DayCare + NightStay together (one POST → two closure rows), the
mobile UI needs to DELETE **each** `closureId` to reopen both. There's no
"undo batch" endpoint.

---

## 4. CHANGED: Booking APIs carry `serviceId`

Because closures and capacity are per-service, bookings must also know
which service they belong to. Capacity is checked per-service: DayCare
bookings count separately from NightStay bookings.

### 4.1 `POST /providers/{providerId}/bookings`

#### Request body

**New required field: `serviceId`.**

```json
{
  "petParentId": "22222222-2222-2222-2222-222222222222",
  "serviceId":   "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "bookingDate": "2026-05-20",
  "startTime":   "10:00:00",
  "endTime":     "12:00:00"
}
```

#### Response

```json
{
  "success": true,
  "data": {
    "bookingId":       "ffffffff-ffff-ffff-ffff-ffffffffffff",
    "providerId":      "11111111-1111-1111-1111-111111111111",
    "petParentId":     "22222222-2222-2222-2222-222222222222",
    "serviceId":       "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    "serviceCategory": "PetSitter",
    "subCategory":     "PetHotel",
    "bookingDate":     "2026-05-20",
    "startTime":       "10:00:00",
    "endTime":         "12:00:00",
    "status":          "Confirmed",
    "createdAtUtc":    "2026-05-21T09:31:00+00:00",
    "updatedAtUtc":    "2026-05-21T09:31:00+00:00",
    "cancelledAtUtc":  null
  },
  "error": null
}
```

#### New error responses

| HTTP | Error code           | When |
|------|----------------------|------|
| 400  | `InvalidServiceId`   | `serviceId` doesn't belong to provider or is inactive. |
| 409  | `ServiceClosed`      | A closure on **this** `serviceId` covers the requested window. (Old code was `ProviderClosed`; renamed because closures are per-service now.) |
| 409  | `CapacityExceeded`   | Same code; the underlying check is now per-service. |

### 4.2 `GET /providers/{providerId}/availability/slots` — slot grid

Slot computation is per-service now. **New required query param: `serviceId`**.

```
GET /api/v1/providers/{providerId}/availability/slots
    ?serviceId={daycareId}
    &date=2026-05-20
    &durationHours=2
    &granularityMinutes=30
```

`serviceId` is required. Capacity, duration rule, and closure exclusions
are all scoped by it — a DayCare query is independent of a NightStay
query on the same day.

#### Response

```json
{
  "success": true,
  "data": {
    "providerId":         "11111111-1111-1111-1111-111111111111",
    "serviceId":          "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
    "date":               "2026-05-20",
    "serviceCategory":    "PetSitter",
    "subCategory":        "PetHotel",
    "serviceType":        "DayCare",
    "durationHours":      2,
    "capacity":           4,
    "granularityMinutes": 30,
    "slots": [
      { "startTime": "09:00:00", "endTime": "11:00:00" },
      { "startTime": "09:30:00", "endTime": "11:30:00" },
      { "startTime": "10:00:00", "endTime": "12:00:00" }
    ]
  },
  "error": null
}
```

### 4.3 `GET /providers/{providerId}/bookings` — date filter added

**Optional `date` query param** narrows results to one calendar day —
purpose-built for the provider day-view UI.

```
GET /api/v1/providers/{providerId}/bookings                    # full history (unchanged)
GET /api/v1/providers/{providerId}/bookings?date=2026-05-20    # just that day
```

Response is the same array shape, each item now includes `serviceId`
(as a side effect of the per-service work):

```json
{
  "success": true,
  "data": [
    {
      "bookingId":       "ffffffff-ffff-ffff-ffff-ffffffffffff",
      "providerId":      "11111111-1111-1111-1111-111111111111",
      "petParentId":     "22222222-2222-2222-2222-222222222222",
      "serviceId":       "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
      "serviceCategory": "PetSitter",
      "subCategory":     "PetHotel",
      "bookingDate":     "2026-05-20",
      "startTime":       "10:00:00",
      "endTime":         "12:00:00",
      "status":          "Confirmed",
      "createdAtUtc":    "2026-05-21T09:31:00+00:00",
      "updatedAtUtc":    "2026-05-21T09:31:00+00:00",
      "cancelledAtUtc":  null
    }
  ],
  "error": null
}
```

Date format is `YYYY-MM-DD`. Omitting the param returns the full history
exactly as before.

---

## 5. NEW: `GET /providers/{providerId}/profile` — read provider personal info

Returns the persisted personal information for the signed-in provider.
Useful for the Account/Profile screen and any place that needs to display
name, mobile, gender, DOB, etc. without re-running the onboarding POST.

### Request

```
GET /api/v1/providers/{providerId}/profile
```

### Response

```json
{
  "success": true,
  "data": {
    "providerId":             "11111111-1111-1111-1111-111111111111",
    "providerAuthIdentityId": "33333333-3333-3333-3333-333333333333",
    "firstName":              "Ayur",
    "lastName":               "Shitarale",
    "gender":                 "Male",
    "mobileCountryCode":      "+91",
    "mobileNumber":           "9876543210",
    "dateOfBirth":            "1995-08-22",
    "mobileVerifiedAtUtc":    "2026-04-12T08:00:00+00:00",
    "onboardingStatus":       "MobileVerified",
    "createdAtUtc":           "2026-04-12T07:58:00+00:00",
    "updatedAtUtc":           "2026-04-12T08:00:00+00:00"
  },
  "error": null
}
```

### Error responses

| HTTP | Error code                  | When |
|------|------------------------------|------|
| 404  | `ProviderProfileNotFound`   | The `providerId` doesn't exist. |

Notes:
- Response shape is identical to what `POST /provider-onboarding/profile`
  already returns (same `ProviderProfileResponse` record), so any client
  parsing you already have is reusable.
- `mobileVerifiedAtUtc` is `null` until the OTP has been validated.
- `onboardingStatus` is one of `MobileVerificationPending` or `MobileVerified`.

---

## 6. Recommended mobile sequence (after server changes)

For a brand-new provider:

1. `POST /provider-onboarding/firebase-auth`
2. `POST /provider-onboarding/profile`
3. Mobile OTP: `POST /providers/{id}/mobile-verification/otp` → `POST .../verify`
4. Category register: `POST /providers/{id}/services/<category>/<sub>` + offering save
5. **`GET /providers/{id}/services` ← NEW step.** Store the returned `serviceId`s
   keyed by `serviceType` in local state.
6. Policy, weekly availability, etc. (unchanged).
7. Now anywhere you previously sent "this providerId is closing/booking/
   querying slots", attach the appropriate `serviceId`.

For a returning provider (app launch / session resume):

1. `GET /providers/{id}/onboarding-status` (existing).
2. `GET /providers/{id}/profile` if you need the personal info on screen.
3. `GET /providers/{id}/services` to refresh the catalog cache.
4. Carry on.

---

## 7. Quick migration checklist for mobile

- [ ] Drop any code that calls `POST /providers/{id}/services` or `GET /providers/{id}/services` expecting the old `{ name, basePrice, durationMinutes }` shape — those routes are 404 in the old form.
- [ ] Add a model + local cache for `ProviderServiceSummary`
      (`serviceId`, `serviceType`, `isActive`, etc.).
- [ ] Call `GET /providers/{id}/services` after offering save and on launch.
- [ ] Replace closure POST body builder to use `serviceIds: [Guid]` (array).
- [ ] Handle the new `closures: []` array in closure POST/GET responses.
- [ ] Surface `conflictingBookings[].serviceId` in the conflict warning UI.
- [ ] Pass `serviceId` query param on the slot endpoint.
- [ ] Pass `serviceId` in the booking POST body.
- [ ] Read `serviceId` field on booking responses (also new for list/get/cancel).
- [ ] (Optional) Wire the day-view list to call
      `GET /providers/{id}/bookings?date=YYYY-MM-DD`.
- [ ] (Optional) Use `GET /providers/{id}/profile` to load the
      Account/Profile screen instead of caching the onboarding-POST response.

If anything reads `data.closure` (singular) on the closure POST response,
or reads `data.providerId` on a slot response without the new
`serviceType`/`serviceId` fields, those code paths break — they're the
ones to update first.
