# Mobile API changes — the 2-hour modification window

**Audience:** Pawfront mobile developers (parent app + provider app)
**Status:** Shipped on backend (pending DB deploy).
**Scope:** Behavioral + **two new 409 error codes**. No request or response shape
changes anywhere — nothing existing breaks.

---

## 1. What changed, in one line

A pet parent can no longer reschedule a job once the service is **less than 2 hours
away**, and a reschedule request they already sent **expires automatically** at that
same moment if the provider hasn't answered it.

---

## 2. Why

A job that's about to start needs a settled schedule. Two problems today:

1. A parent could move a booking minutes before it started, with the provider
   already on their way.
2. Worse — a request the provider never got round to answering left the booking
   parked in `MODIFICATION_REQUEST_BY_PARENT`. **The provider cannot start a job
   from that status**, so an unanswered request silently made the job impossible to
   start, with nothing to clear it.

Both are now closed by a single cutoff.

---

## 3. The cutoff — one timestamp, and you compute it yourselves

```
modificationCutoff = serviceStart − 2 hours
```

| Booking kind | `serviceStart` is |
|---|---|
| Single-day service booking | `bookingDate` + `startTime` |
| Night stay (boarding) | `checkInDate` + `dropOffTime` |

**All times are UTC**, like everything else in this API.

> **The server does not send you a `modificationCutoffUtc` field.** Derive it from
> the fields already present on every booking read. If you'd prefer the server to
> publish it explicitly, say so and we'll add it — it's a small change.

**Worked example** (matches the spec you sent):

- Service starts **10:00**. Cutoff is **08:00**.
- Parent sends a reschedule request at **21:00 the previous evening**.
- Provider can accept or decline **up to 07:59**.
- At **08:00** the request expires by itself and the job goes back to `CONFIRMED`.

---

## 4. Rule A — parent can't modify inside the window

**Parent app:** hide the "Modify Job" button from the cutoff onward. Up to 07:59 it
shows; from 08:00 it's gone.

The server enforces this too, so if a stale screen submits anyway:

```
POST /api/v1/pet-parents/{petParentId}/bookings/{bookingId}/modifications
POST /api/v1/pet-parents/{petParentId}/night-stay-bookings/{bookingId}/modifications
```

```jsonc
// 409 Conflict
{
  "success": false,
  "data": null,
  "error": {
    "code": "ModificationWindowClosed",
    "message": "Booking '...' can no longer be modified within 2 hours of the service start time."
  }
}
```

Treat it as "too late" — refresh the booking and drop the modify affordance.

---

## 5. Rule B — an unanswered request expires

When the cutoff arrives with the provider's answer still outstanding:

- the proposal is **discarded** (exactly as if the provider had declined it),
- the booking status goes back to **`CONFIRMED`**,
- `pendingModification` on the booking read becomes **`null`**.

### 5a. Parent app

A booking sitting in `MODIFICATION_REQUEST_BY_PARENT` can silently return to
`CONFIRMED` without anybody acting. Don't assume the status only changes in response
to a user action — re-read the booking when the screen becomes visible.

The **original** date and time stay in force. Nothing about the booking changes
except that the pending proposal is gone.

### 5b. Provider app — read this bit

Accept and decline can now fail:

```
POST /api/v1/providers/{providerId}/bookings/{bookingId}/modifications/accept
POST /api/v1/providers/{providerId}/bookings/{bookingId}/modifications/decline
POST /api/v1/providers/{providerId}/night-stay-bookings/{bookingId}/modifications/accept
POST /api/v1/providers/{providerId}/night-stay-bookings/{bookingId}/modifications/decline
```

```jsonc
// 409 Conflict
{
  "success": false,
  "data": null,
  "error": {
    "code": "ModificationRequestExpired",
    "message": "The modification request for booking '...' expired before it was answered; the booking has reverted to CONFIRMED."
  }
}
```

On this error the booking **is already back to `CONFIRMED`** — the revert happens as
part of the same call. Just refresh and show the job as confirmed; there's no
recovery step and nothing was lost.

> ### ⚠️ The status can lag by up to ~10 minutes — don't trust it alone
>
> A background job sweeps expired requests every 10 minutes. Between the cutoff and
> the next sweep, a booking may **still read as `MODIFICATION_REQUEST_BY_PARENT`**
> even though accepting is no longer possible.
>
> So the provider app should **derive the cutoff itself** (same formula as §3) and
> hide or disable the Accept / Decline buttons from that moment — rather than
> waiting for the status to change. Otherwise a provider can tap Accept on a
> request that looks live and get a 409.
>
> The 409 is the safety net, not the primary UX.

---

## 6. New error codes — summary

| HTTP | Code | When | What to do |
|---|---|---|---|
| 409 | `ModificationWindowClosed` | Parent submits a reschedule inside the 2-hour window | Refresh; hide "Modify Job" |
| 409 | `ModificationRequestExpired` | Provider accepts/declines after the cutoff | Refresh; job is already `CONFIRMED` |

Both follow the standard envelope. No other status codes changed.

---

## 7. What is **not** affected

- **Provider-initiated reschedules are unchanged.** A provider can still propose a
  change inside the 2-hour window, and their proposal does not expire. Only the
  parent → provider direction is time-boxed for now.
- **No request or response shapes changed.** No new fields, no removed fields, no
  renames.
- **Cancelling is unaffected** — the existing cancellation rules are untouched.
- **Other booking statuses are unaffected.** No new status value was added; the
  expiry reuses the existing `CONFIRMED`.

---

## 8. Action required

| Area | App | Action |
|---|---|---|
| Hide "Modify Job" from `serviceStart − 2h` | Parent | **Required** |
| Handle 409 `ModificationWindowClosed` | Parent | **Required** (fallback for stale screens) |
| Re-read booking on screen focus — status can revert on its own | Parent | **Recommended** |
| Hide/disable Accept + Decline from `serviceStart − 2h` | Provider | **Required** — status lags up to 10 min |
| Handle 409 `ModificationRequestExpired` | Provider | **Required** |

---

## 9. Testing

Note the booking's `bookingDate` + `startTime` (or `checkInDate` + `dropOffTime`)
and work in **UTC**.

1. **Block:** on a confirmed booking starting in under 2 hours, submit a
   modification as the parent → expect **409 `ModificationWindowClosed`**.
2. **Still allowed:** same booking, more than 2 hours out → the request succeeds and
   the status becomes `MODIFICATION_REQUEST_BY_PARENT`.
3. **Expiry (lazy):** open a request comfortably before the cutoff, wait until under
   2 hours before the start, then accept as the provider → expect **409
   `ModificationRequestExpired`**, and re-reading the booking shows `CONFIRMED` with
   `pendingModification: null`.
4. **Expiry (sweep):** same setup, but instead of accepting, just wait. Within ~10
   minutes of the cutoff the booking flips to `CONFIRMED` on its own.
5. **Provider side unaffected:** as the provider, open a modification on a booking
   starting in under 2 hours → still succeeds.
