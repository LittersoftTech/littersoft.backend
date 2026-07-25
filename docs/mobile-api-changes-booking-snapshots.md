# Mobile API changes — booking snapshots (price / cancellation policy / address)

**Audience:** Pawfront mobile developers
**Status:** Shipped on backend (pending DB deploy).
**Scope:** Mostly behavioral — booking detail is **fully backwards compatible**.
Two "my bookings" **list** endpoints gain two new optional sections.

---

## 1. What changed, in one line

A booking now **locks in** its price, cancellation policy, and service address at
the moment it's created. When a provider later edits those settings, bookings that
already exist no longer change.

---

## 2. Why

Previously these three values were looked up **live** every time a booking was
read. So if a provider raised their price, changed their cancellation policy, or
moved premises, that instantly rewrote the details of bookings customers had
**already made** — including confirmed ones. A customer could open a booking they
made last week and see a different price than they agreed to.

Now each booking carries its own frozen copy of:

1. **Price** — the service rate (per hour for most services, per night for night stays).
2. **Cancellation policy** — `minimumHoursBeforeCancellation` (`null | 24 | 48 | 72 | 96`).
3. **Address of the selected location** — whichever the customer chose via
   `locationType`: their own address (`ParentLocation`) or the provider's
   (`ProviderLocation`).

---

## 3. Booking **detail** endpoints — no changes required

```
GET /pet-parents/{petParentId}/bookings/{bookingId}
GET /pet-parents/{petParentId}/night-stay-bookings/{bookingId}
GET /bookings/{bookingId}                                      (provider host)
GET /providers/{providerId}/night-stay-bookings/{bookingId}    (provider host)
```

**Same fields, same shapes, same names.** Nothing to adopt.

- `paymentDetails.pricePerHour` / `pricePerNight`
- `cancellationPolicy.minimumHoursBeforeCancellation`
- `location` → `{ locationType, addressLine, city, zipCode, latitude, longitude }`

Only the *values* behave differently: they no longer drift when the provider edits
their settings.

> **Note — contact addresses are deliberately still live.** `parentDetails` and
> `providerDetails` carry each party's **current** contact address, and always
> will. Only the `location` block (where the service actually happens) is frozen.
> This is intentional: if someone moves house, you still want to be able to phone
> or reach them, but a booking already agreed shouldn't silently relocate.

---

## 4. "My bookings" **list** endpoints — two new sections (additive)

```
GET /pet-parents/{petParentId}/bookings
GET /pet-parents/{petParentId}/night-stay-bookings
```

Each card previously returned three sections. It now returns **five**:

```jsonc
{
  "booking":         { /* unchanged */ },
  "providerDetails": { /* unchanged */ },
  "serviceDetails":  { /* unchanged shape; see the fix below */ },

  // NEW
  "cancellationPolicy": { "minimumHoursBeforeCancellation": 48 },

  // NEW
  "location": {
    "locationType": "ProviderLocation",
    "addressLine": "12 Rue du Marché",
    "city": "Geneva",
    "zipCode": "1204",
    "latitude": 46.204391,
    "longitude": 6.143158
  }
}
```

Both use the **exact same shapes** as the corresponding sections on the booking
detail response, so any existing model/parsing code can be reused as-is.

**Why:** the "My Bookings" list can now show the price, the cancellation terms, and
the address directly on each card — no per-row follow-up call to the detail
endpoint.

**Existing fields are untouched**, so parsing won't break if you adopt this later.

### Bug fixed here

On the single-day list, `serviceDetails.pricePerHour` was being resolved from the
provider's **live** price on every read. A provider changing their rate retroactively
changed the price shown on bookings the customer had already made. It now shows the
locked-in price. The field name and type are unchanged.

### One behavioral caveat on lists

The list endpoints return `location` **only from the snapshot** — they do *not*
fall back to a live lookup. For bookings created *before* this feature ships, the
list's `location` address fields will be `null`.

- The **detail** endpoints still resolve those older bookings live, so they show a
  full address.
- Practically: if `location.addressLine` is `null` on a list card, either fall back
  to the detail call or just don't render the address line for that card.
- This only affects pre-existing bookings. Everything created from now on has a
  full snapshot.

(This is a deliberate performance trade-off — live-resolving the provider address
requires an external lookup per row, which would noticeably slow the list.)

---

## 5. What about older bookings?

Bookings that already exist when this deploys are **backfilled** with current
values, so they're frozen from that point forward too. Two gaps, both cosmetic and
both self-healing on the detail endpoint:

| Case | Behavior on older bookings |
|---|---|
| `ProviderLocation` street/city/zip | Not backfilled (lives in a separate store the migration can't reach). Detail resolves live; list shows `null`. |
| App-booking price on very old rows | Not backfilled. Detail falls back to the live rate. |

---

## 6. Action required

| Change | Action |
|---|---|
| Booking detail (all 4 endpoints) | **None.** Fully backwards compatible. |
| List: new `cancellationPolicy` + `location` sections | **Optional** — adopt when you want them on the cards. |
| List: `pricePerHour` now price-locked | **None** — same field, correct value. |
| List `location` null on pre-existing bookings | Handle `null` address fields (skip the line, or use the detail call). |

**Nothing breaks if you ship no changes.** The only reason to touch mobile code is
to *use* the two new list sections.

---

## 7. Testing

Both Postman collections are updated with the new sections and notes:

- `docs/Pawfront.PetParentApi.postman_collection.json`
- `docs/postman_collection_provider.json`

Good manual check: create a booking, note the price/policy/address, then change the
provider's price or cancellation policy, and re-read the booking. The values should
**not** move. A *newly* created booking should pick up the new values.
