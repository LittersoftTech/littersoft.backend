# Pawfront E2E Scenario Suite — how to run

Files:

- [`Pawfront.E2E-Scenarios.postman_collection.json`](Pawfront.E2E-Scenarios.postman_collection.json) — the test suite (assertions + variable chaining)
- [`Pawfront.E2E.postman_environment.json`](Pawfront.E2E.postman_environment.json) — environment template (URLs, Firebase keys, test-account credentials)

This suite is different from the three *catalog* collections (`postman_collection_provider.json`,
`Pawfront.PetParentApi.postman_collection.json`, `Pawfront.ChatApi.postman_collection.json`): those
are one-request-per-endpoint references, while this one plays **real-life scenarios end-to-end
across both hosts** and asserts on every step. The chat host has no scenario coverage here yet —
its REST surface is scriptable (see its own collection), but proving the hub and push-suppression
behaviour needs a real SignalR client and two Firebase tokens.

## What it covers

| Folder | Scenario | Key assertions |
|---|---|---|
| 0. Setup | Sign in both Firebase accounts, upsert auth identities, recover/complete profiles | tokens acquired, `providerId`/`petParentId` resolved |
| A | Provider sets up shop (PetSitter PetHotel: offering with capacity **2**, DayCare min 2 h, NightStay min 12 h; weekly hours 08:00–20:00; policies) | ServiceIds captured for DayCare + NightStay |
| B | Parent adds a pet + medical info | medical snapshot persisted |
| C | **Happy path**: slots → book → provider accepts → parent gets start-OTP → provider starts with OTP → completes | statuses `CREATED→CONFIRMED→JOB_STARTED→COMPLETED`, `PF-XXXXXX` job id, payment math (`fee = % of total`), location block |
| D | **Reschedule**: parent proposes new date/time, provider accepts | `pendingModification` visible to counterparty, new schedule applied to the same booking id, staging cleared |
| E | **Capacity**: 2 bookings fill a slot, 3rd → `409 CapacityExceeded`; the full slot disappears from `/availability/slots` and returns after a cancel | slot list truthfulness |
| F | **Night-stay**: 2 stays fill the nights, availability reports `remainingCapacity: 0` / `isAvailable: false` for the night, 3rd stay → 409; cancel frees a unit | date-granular `nights[]` response (needs the 2026-07-11 `GetNightStayOccupancy` deploy) |
| G | **Closures**: sick day → zero slots + `409 ServiceClosed`; closing over an active booking → discriminated `BookingsExist` | closure/booking interlock |
| H | **Private job** (custom walk-in) | starts `CONFIRMED`, `pawfrontFee = 0`, `feePercentage = 0`, total = stored rate × hours |
| I | **Event lifecycle**: paid physical event (capacity 3) → 2 tickets → payment webhook → sold-out on next 2 → engagement counters → organiser metrics → cancel frees seats | `EventSoldOut`, `isBookable` flips, `earnings`/`confirmedAttendees` |
| J | Provider looks up the customer card + full pet profile (the two provider-facing lookup APIs) | `rating: null`, pets list, age `{years, months}` |
| K | Negatives: no token → 401, foreign `petParentId` → 403, unknown pet → 404, missing `locationType` → 400, unknown `serviceId` → 400 | error envelope codes |

## Prerequisites

1. **Deploy the database**: re-run `database/Pawfront.Database/Deployment/DeployAll.sql`.
   Scenario F specifically requires the 2026-07-11 `Booking.GetBookingsForDate` change.
2. **Run both hosts** (defaults match `launchSettings.json`):
   ```powershell
   dotnet run --project .\src\Pawfront.Api\Pawfront.Api.csproj --configfile .\NuGet.Config            # http://localhost:5051
   dotnet run --project .\src\Pawfront.PetParentApi\Pawfront.PetParentApi.csproj --configfile .\NuGet.Config  # http://localhost:5052
   ```
3. **Dedicated test accounts** (email+password sign-in enabled):
   - A **provider** account in the `littersoftprovider` Firebase project that is *unregistered* or already
     registered as **PetSitter** — any other category makes Scenario A fail with `409 ServiceCategoryConflict`.
   - A **parent** account in the `littersoftpetparent` Firebase project. `parentEmail` in the environment
     **must** be exactly that account's email — the event `isBookable`/cancel assertions match on the JWT email claim.
4. Fill the environment: both base URLs, both Firebase **Web API keys**, both credential pairs.

## Running

**Postman UI**: import both files → select the *Pawfront E2E (local dev)* environment → open the collection →
**Run** → keep the default top-to-bottom order → Start Run. Requests are order-dependent (Setup must run first).

**newman (CLI)**:
```powershell
npx newman run .\docs\Pawfront.E2E-Scenarios.postman_collection.json `
  -e .\docs\Pawfront.E2E.postman_environment.json
```

## Design notes / gotchas

- **Re-runs are safe.** Booking dates are randomised 1–10 months into the future on every run, and each
  scenario cancels what it created (Scenario C intentionally ends in `COMPLETED`, which keeps holding its
  slot — the random dates make that harmless). Events and the Scenario C booking do accumulate as test data.
- **All dates/times are UTC** — project-wide rule; the date seeds use `toISOString()`.
- The "complete profile" and "add pet" requests **skip themselves** when the account already has a
  profile/pet (uses `pm.execution.skipRequest()`, Postman v10.15+ / current newman).
- Scenario A **overwrites** the provider's weekly availability (7 days open 08:00–20:00, no break) and sets
  the PetHotel offering capacity to **2** — the capacity numbers in Scenarios E/F depend on that. Don't point
  the suite at a provider account whose data you care about.
- Mobile-OTP verification is *not* exercised: the OTP code is never returned by the API (sent via the
  SMS sender, which is a no-op in dev), so there is nothing to chain. The booking **start-OTP** flow *is*
  covered — that code is returned on the parent's booking-detail read.
- If you run the hosts over HTTPS with the dev certificate, disable SSL certificate verification in
  Postman settings (or newman `--insecure`).
