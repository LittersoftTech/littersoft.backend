# Pawfront Backend — Project Memory

Working notes for Claude. Read this first when resuming work.

## What this is

Pawfront is a provider-facing mobile-app backend for pet-services businesses
(groomers, sitters, trainers, vets, adoption/sales). Built on **.NET 10**,
**Azure SQL** (structured data), **Azure Cosmos DB** (per-category service
details and event extension data), **Azure Blob Storage** (images), and
**Firebase Auth** (identity).

A parallel pet-parent (consumer) app is now under construction in its own
host (`Pawfront.PetParentApi`, separate Firebase project, `PetParentUser`
authorization policy). All pet-parent tables live in the **`[Parent]`**
schema (`Parent.PetParents`, `Parent.Pets`, `Parent.ParentAuthIdentities`,
`Parent.ParentDeviceTokens`). The legacy `[Customer]` schema has been
retired — `DeployAll.sql` transfers the old tables on first run and drops
the empty schema.

## Stack & solution layout

```
src/
├── Pawfront.Api                    minimal-API host, endpoints, auth, telemetry
├── Pawfront.ChatApi                chat host — SignalR hub + REST, BOTH Firebase projects
├── Pawfront.Application            use-case interfaces + orchestrators (pure C#)
├── Pawfront.Contracts              wire DTOs (records)
├── Pawfront.Domain                 entities + enums (POCO, no infra deps)
├── Pawfront.Infrastructure.Sql     ADO.NET + stored procs; in-memory dev fallbacks
├── Pawfront.Infrastructure.Cosmos  Cosmos client, container accessors, bootstrapper
└── Pawfront.Infrastructure.Azure   Key Vault secret provider, Blob storage
database/
└── Pawfront.Database               SQL files + idempotent Deployment/DeployAll.sql
docs/architecture.md                older but still mostly relevant
```

Solution file: `Pawfront.slnx`. NuGet config: `NuGet.Config`. Build target:
`net10.0`. Build command:
```
dotnet build Pawfront.slnx --configfile NuGet.Config
```

## Architectural conventions

- **Layering:** Domain ← Application ← Contracts. Infra implements
  Application interfaces. API composes Contracts ↔ Application.
- **Endpoints:** each feature has `Endpoints/XxxEndpoints.cs` exposing
  `MapXxxEndpoints(this IEndpointRouteBuilder)`. `Program.cs` is thin (~50
  lines), calls each module's map extension under `/api/v1`.
- **Response envelope:** every endpoint returns the wrapper from
  `Pawfront.Contracts.Common.ApiResponse<T>`:
  `{ "success": bool, "data": T | null, "error": { code, message } | null }`.
  Use `ApiResults.Ok/Created/NotFound/BadRequest/Conflict` helpers (in
  `Pawfront.Api.Endpoints.ApiResults`). **Never call `Results.*` directly in
  endpoint handlers.**
- **Unhandled exceptions** are wrapped by `GlobalExceptionHandler` →
  500 + `{ success: false, error: { "InternalServerError", "..." } }`.
- **Typed exceptions per feature** (e.g., `PetHotelNotRegisteredException`)
  map to `NotFound` / `BadRequest` etc. `ArgumentException` from validation
  → `BadRequest("InvalidRequest", ex.Message)`.
- **Per-category Cosmos registries** (`IPetSitterServiceRegistry` etc.) live
  in Application; impls in Infrastructure.Cosmos. They wrap reads/writes to
  the shared `ProviderServices` container.
- **Cross-cutting Application orchestrators** (e.g.,
  `ProviderOnboardingStatusService`, `EventService`) compose multiple
  Application interfaces; live in Application.
- **`Required(...)`, `Trim(...)`, `NormalizeSet(...)`, `NormalizeOne(...)`**
  are the repeated validation helpers used inside Cosmos registries. Reuse
  the same names + shapes when adding new ones.

## Authentication

- **Two auth schemes**, both producing the `FirebaseUser` authorization
  policy (`AuthServiceCollectionExtensions.FirebaseUserPolicy`):
  - **`JwtBearer`** validates Firebase ID tokens against
    `https://securetoken.google.com/{Firebase:ProjectId}` (public JWKS).
  - **Custom `GoogleIdToken` scheme** (`GoogleIdTokenAuthenticationHandler`)
    validates raw Google ID tokens via `GoogleJsonWebSignature` — only runs
    when the token's `iss` is `accounts.google.com` (cheap header peek,
    avoids interfering with Firebase tokens).
- The `Secrets/serviceadmin.json` Firebase Admin SDK file is **not used**.
  Token validation is JWKS-based. Bring it back only when wiring FCM push.
- `Firebase:ProjectId = "littersoftprovider"` (set in `appsettings.json`).

### Pet-parent host ownership enforcement

On `Pawfront.PetParentApi`, every `/pet-parents/{petParentId:guid}/*` and
`/pets/{petId:guid}/*` route runs through an ownership filter before its
handler resolves. The caller's `PetParentId` is derived **from the JWT**,
never from the route or body.

Resolution chain (cached once per request via the scoped
[`ICurrentPetParentContext`](src/Pawfront.PetParentApi/Auth/CurrentPetParentContext.cs)):

```
JWT sub / user_id  →  Parent.ParentAuthIdentities.FirebaseUserId  →  PetParentId
```

Backed by [`IPetParentOwnershipReader`](src/Pawfront.Application/ParentOnboarding/IPetParentOwnershipReader.cs)
(SQL impl `SqlPetParentOwnershipReader`) — two indexed point reads:
`ParentAuthIdentities` lookup hits the UNIQUE `FirebaseUserId` index, and
the per-pet check hits `PK_Pets`.

**Filters** (in [`src/Pawfront.PetParentApi/Auth/`](src/Pawfront.PetParentApi/Auth)):
- `OwnedPetParentFilter` — wired via `.RequireOwnedPetParent()` on the
  `/pet-parents/{petParentId:guid}` MapGroup. Compares the route's
  `petParentId` to the resolved id.
- `OwnedPetFilter` — wired via `.RequireOwnedPet()` on the
  `/pets/{petId:guid}` MapGroup. Looks up the pet's owning `PetParentId`
  and compares.

**Status codes the filters emit:**
- 403 `Forbidden` — authenticated but the route's id doesn't belong to the caller.
- 403 `ParentProfileNotCompleted` — JWT is valid but the parent hasn't
  finished `POST /parent-onboarding/profile` (PetParentId is unset).
- 404 `PetNotFound` — `/pets/{petId}/*` route, pet row doesn't exist
  (don't leak existence via 403).

**Not filtered** — by design, since the caller may not have a profile yet:
`/parent-onboarding/*`, `/providers/*`, `/events/*`, `/event-bookings/*`,
`/blob-images`, `/health`.

## Configuration

`appsettings.json` (committed) and `appsettings.Development.json` (committed)
both currently contain **real SQL/Cosmos/Blob credentials** — this is a
known leak and should be rotated + moved to user-secrets or Key Vault.

Key config sections:
- `Firebase:ProjectId`, `Firebase:GoogleClientIds`
- `AzureKeyVault:Enabled` — when `false`, the `LocalDevelopmentSecretProvider`
  reads secrets from config directly; when `true`, uses Azure Key Vault via
  `DefaultAzureCredential` (only in non-Development env or when explicitly
  enabled).
- `ConnectionStrings:SqlServer` — direct conn string (current dev approach)
- `Cosmos:Endpoint` accepts **either** a clean URL **or** a full connection
  string (`AccountEndpoint=...;AccountKey=...;`). The
  `CosmosClientFactory` detects the format. Current dev value is the
  connection-string format.
- `Cosmos:Containers.{ProviderServices,Events}` — auto-created by the
  `CosmosBootstrapper` at startup. Other containers listed but not active.
- `BlobStorage:Container = "provider-images"` (still named that for legacy
  reasons; also now hosts pet-parent images), with
  `Folders.{ProfilePhotos,ServicePhotos,EventBanners,PetParentProfilePhotos,PetPhotos,PetProfilePhotos,PetParentIdentities}`.
- `ApplicationInsights:ConnectionString` — empty by default; when set,
  enables Azure Monitor exporter. Otherwise dev gets console exporter.
- `Payments:PawfrontFeePercentage` — platform commission as a percentage of a
  booking's total amount (dev default `10`). Bound to `PawfrontFeeOptions`
  (both hosts) and used by `BookingService.GetDetailAsync` to compute the
  `pawfrontFee` on the booking-detail payment block. Missing section ⇒ 0%.

## SQL — deployment

Deploy via the **single idempotent script**:
```
database/Pawfront.Database/Deployment/DeployAll.sql
```
Run in SSMS / Azure Data Studio / sqlcmd. The project is _not_ an SDK-style
SQL project — DeployAll is the one source of truth that should be re-run on
every change. Re-runs are safe (uses `IF NOT EXISTS` for tables/indexes and
`CREATE OR ALTER` for sprocs).

A full **Mermaid ER diagram** + per-table descriptions live in
[`database/Pawfront.Database/README.md`](database/Pawfront.Database/README.md).
Keep that file in sync whenever a table or relationship changes.

### Schemas
- `Provider.*` — provider profile, auth identity, OTP, device tokens,
  policies, service-registration index, **per-service catalog
  (`ProviderServices`) and closures**
- `Parent.*` — `ParentAuthIdentities`, `ParentDeviceTokens` (pet-parent
  Firebase login + per-device FCM tokens), plus `PetParents`, `Pets`
  (profile/pet tables — still scaffolding until profile endpoint lands)
- `Event.*` — `Events`, `EventAmenities`, `EventPayoutMethods` (organiser
  payout Cash/Digital, paid events only), `EventBookings`,
  `EventBookingTickets`
- `Booking.*` — `Bookings` (real, race-safe via `Booking.CreateBooking` sproc
  with `UPDLOCK, HOLDLOCK` capacity check, scoped by `ServiceId`),
  `BookingStatusHistory`, plus the multi-night boarding pair
  `NightStayBookings` + `NightStayBookingStatusHistory` (PetSitter NightStay
  only; check-in/check-out date range, per-night capacity)

### Stored procedures live in `[Provider]`, `[Event]`, and `[Booking]`
See `database/Pawfront.Database/StoredProcedures/`. Naming pattern:
`Provider.SaveXxx`, `Provider.GetXxx`, `Event.CreateEvent`, etc.
Custom `THROW` codes used for typed errors:
- `51001` provider auth identity not found
- `51002` provider profile not found (OTP create)
- `51003` provider mobile OTP not found
- `51004` provider auth identity not found (device-token register) → API maps to
  **404 ProviderAuthIdentityNotFound**
- `51005` device token not found for this caller (device-token deactivate) → API
  maps to **404 DeviceTokenNotFound**. "Unknown" and "not yours" are deliberately
  the same case, so it can't probe whether a token is registered.
- `51010` provider profile not found (service registration)
- `51011` provider already registered under a different service category
  (one-service-per-provider rule) → API maps to **409 ServiceCategoryConflict**
- `51020/51021` provider profile not found (payout / cancellation policy)
- `51030` provider profile not found (event create)
- `51050` provider profile not found (weekly availability save)
- `51060` pet parent not found (booking create)
- `51061` provider not found (booking create)
- `51062` no remaining capacity for slot (booking create) — capacity is
  scoped by `ServiceId`
- `51063` booking not found (booking cancel)
- `51064` only the booker can cancel
- `51065` booking already cancelled
- `51066` ServiceId is unknown, inactive, or not owned by the provider
  (booking create)
- `51070` provider profile not found (closure create)
- `51071` provider closure not found (closure delete)
- `51072` one or more ServiceIds are unknown/inactive/not owned by the
  provider (closure batch create)
- `51075` empty ServiceId list (closure batch create)
- `51080` provider profile not found (provider service upsert)
- `51090` event not found (event booking create)
- `51091` event sold out / not enough remaining capacity → API maps to
  **409 EventSoldOut**
- `51092` event booking not found (payment confirmation)
- `51093` event booking payment already confirmed with a different result
- `51094` invalid event-booking request (empty attendee list, invalid
  PaymentStatus)
- `51095` event not found for the requesting provider (organiser-scoped
  dashboard reads — metrics / attendees)
- `51096` event not found (counter increment)
- `51097` invalid counter type (must be `View`, `Share`, or `Inquiry`)
- `51098` event not found (event payout-method save) → API maps to **404 EventNotFound**
- `51099` event is free; payout methods only apply to paid events → API maps to
  **400 FreeEventNoPayout**
- `51067` provider is currently inactive (master Active/Inactive switch is off)
  → API maps to **409 ProviderInactive**
- `51068` pet not found or not owned by the pet parent (booking create with petId)
- `51069` the pet already has an active booking on this service overlapping the
  requested slot (single-day booking create) → API maps to **409 PetAlreadyBooked**.
  Enforced under the same `UPDLOCK + HOLDLOCK` range as the capacity count, so a
  concurrent duplicate (two devices / a race) serialises and is rejected. Only fires
  when the create names a `PetId` (App bookings); Custom walk-ins have no pet and are
  unaffected. Scoped to the same `ServiceId` (same provider service).
- `51100` provider profile not found (active-status toggle)
- `51110` provider not found (provider photo add)
- `51111` provider photo not found (provider photo delete)
- `51112` provider not found (provider banner-image upload) → API maps to
  **404 ProviderNotFound**
- `51113` provider profile not found (provider profile update) → API maps to
  **404 ProviderProfileNotFound**
- `51114` provider profile not found (provider account delete) → API maps to
  **404 ProviderProfileNotFound**
- `51115` provider account has been deleted (anonymised + permanently disabled) —
  thrown by the profile-update and active-status sprocs, since an edit would undo
  the anonymisation and a reactivation would make the account bookable again →
  API maps to **409 ProviderAccountDeleted**
- `51120` booking not found (status update)
- `51121` caller is not a party to the booking (status update) → API maps to
  **403 Forbidden**
- `51122` status not permitted for this actor (status update) → API maps to
  **400 BookingStatusNotAllowed**
- `51123` booking is in a terminal state, no further changes (status update) →
  API maps to **409 BookingStatusTerminal**
- `51124` booking already in the requested status (status update) → API maps to
  **409 BookingStatusUnchanged**
- `51125` invalid actor or status value (status update) → defensive; API
  validates first
- `51126` transition not allowed from the current status (status update) → API
  maps to **409 BookingNotStartable / 400** (engine from-state guard)
- `51127` **retired** — evidence is no longer required before COMPLETED
  (the gate was removed; evidence photos are optional)
- `51128` no-show reported before the 30-minute grace window after the
  booking's scheduled start (`BookingDate + StartTime` UTC) has elapsed → API
  maps to **409 NoShowTooEarly**
- `51129` booking expired (2026-07-17): the booking sat in `CREATED` for 24+
  hours without the provider accepting, so the status engine rejects the
  attempted transition → API maps to **409 BookingExpired**. **Reject-only
  (2026-08-02)** — the sproc no longer writes the `EXPIRED` flip; the scheduled
  external job does, so the row can still read `CREATED` when this fires.
- `51153` booking expired (2026-08-02, BR-53): the booking is **still `CREATED`
  with under 2 hours to `BookingDate + StartTime`** — the provider is out of time
  to accept, so the status engine rejects the attempted transition → API maps to
  the same **409 BookingExpired** (only the message differs). Independent of
  51129: a booking made 90 minutes before the service hits this hours short of
  24. Same cutoff as the **booking lead time** (`BookingLeadTime.Minimum`), so an
  unaccepted booking dies exactly when a fresh one for that slot could no longer
  be created. **Reject-only**, same as 51129.
- **Job-lifecycle sprocs (single-day `Booking.Bookings`) — start-OTP flow:**
  - `51130` booking not found (issue start-OTP)
  - **`Booking.StartBooking`** (→ `START_JOB` + start-OTP): `51131/51132/51133` =
    not found / not the provider (→ **403**) / not in a startable state
    (→ **409 BookingNotStartable**); `51144` = today is not the booking's
    `BookingDate` (→ **409 BookingNotOnServiceDate**); `51137` = the provider is
    outside their own weekly working hours right now (→ **409 OutsideWorkingHours**)
  - **`Booking.VerifyBookingStartOtp`** (start-OTP → `IN_PROGRESS`): `51131/51132`
    = not found / not the provider (→ **403**); `51138` = not `START_JOB`
    (→ **409 BookingNotStartable**); `51134` OTP missing/incorrect (→ **400
    InvalidStartOtp**); `51135` expired (→ **409 StartOtpExpired**); `51136` 6th
    wrong attempt cancels the job (→ **409 OtpAttemptsExceeded**)
  - **`Booking.CompleteBooking`** (`IN_PROGRESS` → `COMPLETED`, no OTP):
    `51131/51132` = not found / not the provider (→ **403**); `51133` = not
    `IN_PROGRESS` (→ **409 BookingNotCompletable**; the retired `ENDING` is
    tolerated as a from-state for legacy rows)
  - **`Booking.MarkBookingPaid`** (`COMPLETED` → `PAID` + payment ledger row):
    `51160` not found (→ **404 BookingNotFound**); `51161` not the provider
    (→ **403 Forbidden**); `51162` not `COMPLETED` (→ **409 BookingNotPayable**);
    `51163` Custom walk-in — App bookings only (→ **400 PaymentNotAppBooking**);
    `51164` already paid (→ **409 BookingAlreadyPaid**)
  - `51139` **retired** (2026-07-23) with the `ENDING` stage — the "End Job" +
    end-OTP leg was removed (sprocs `EndBooking`/`CompleteBookingWithOtp` are
    dropped on re-deploy)
  - **`Booking.UpdateBookingStatus`** (engine): `51149` = cancel attempted while
    the job is underway (`IN_PROGRESS`; retired `ENDING` kept for legacy rows) →
    **409 BookingInProgress**
  - `51140/51141/51142/51143` request modification: not found / not a party
    (→ **403**) / not modifiable (→ **409 BookingNotModifiable**) / a proposal is
    already pending (→ **409 ModificationAlreadyPending**)
  - `51151` the **parent** tried to modify inside the 2-hour pre-service window
    (→ **409 ModificationWindowClosed**) — see the "modification window" note below
  - `51145/51146/51147/51148` respond modification: not found / not a party
    (→ **403**) / no proposal awaiting your response (→ **409 NoPendingModification**) /
    no capacity for the proposed window (→ **409 CapacityExceeded**)
  - `51152` the parent's proposal expired unanswered at the 2-hour cutoff; the
    revert to `CONFIRMED` is committed and the response rejected
    (→ **409 ModificationRequestExpired**)
  - **409 `BookingTermsChanged`** has **no THROW code** — the guard is in the
    Application layer (`BookingTermsChangedException`), raised when the provider's
    terms have drifted since the booking was created and the modification request
    didn't set `acknowledgeTermsChanges`. The sprocs only stage/apply what they're
    handed.
  - `51150` booking not found / not owned by provider (add evidence)
  - **Vet prescription upsert (`Booking.UpsertBookingPrescription`):** `51290`
    booking not found → **404 BookingNotFound**; `51291` caller is not the
    booking's provider → **403 Forbidden**; `51292` not a Vet booking → **400
    PrescriptionNotVetBooking**; `51293` job not underway/completed
    (`IN_PROGRESS`/`COMPLETED`; retired `ENDING` tolerated) → **409 PrescriptionNotAllowed**.
- **Job-lifecycle sprocs (night-stay):** `51246` (transition guard, mirror of
  51126; `51247` retired with the evidence gate); `51248` (no-show too early —
  night-stay has its **own** rule, no longer a mirror of 51128: gated on
  `CheckInDate + DropOffTime` + **2 HOURS** (single-day stays at 30 min), → **409
  NoShowTooEarly**); `51249` (booking expired, mirror of 51129 → **409
  BookingExpired**); `51273` (still `CREATED` with under 2 hours to
  `CheckInDate + DropOffTime`, mirror of 51153 → **409 BookingExpired**);
  `51269` (cancel while underway, mirror of 51149 → **409
  BookingInProgress**); `51250` (issue OTP not found);
  `51251-51253` + `51264` + `51257` (`StartNightStayBooking` → `START_JOB` +
  start-OTP, mirror of 51131-51133 + 51144 wrong-day, gated on `CheckInDate`, +
  51137 working hours); `51251/51252/51258` + `51254-51256`
  (`VerifyNightStayBookingStartOtp` → `IN_PROGRESS`, mirror of 51131/51132/51138 +
  51134-51136); `51251-51253` (`CompleteNightStayBooking` → `COMPLETED`, no OTP,
  mirror of 51131-51133 — `51253` = not `IN_PROGRESS` → **409 BookingNotCompletable**);
  `51259` **retired** with the `ENDING` stage (mirror of 51139); `51260-51263`
  (request modification, mirror of 51140-51143); `51271` (parent modifying inside
  the 2-hour window, mirror of 51151 → **409 ModificationWindowClosed**, measured
  from `CheckInDate + DropOffTime`); `51265-51268` (respond
  modification, mirror of 51145-51148); `51272` (proposal expired unanswered,
  mirror of 51152 → **409 ModificationRequestExpired**); `51270` (add evidence not found);
  `51280-51283` (`MarkNightStayBookingPaid` → `PAID`, mirror of 51160/51161/51162/51164
  — no Custom check since night-stay is App-only: `51280` not found → **404**,
  `51281` not the provider → **403**, `51282` not `COMPLETED` → **409
  BookingNotPayable**, `51283` already paid → **409 BookingAlreadyPaid**).
- `51200` parent auth identity not found (pet-parent profile completion)
- `51201` pet parent not found (profile-photo update)
- `51202` pet parent not found (pet add)
- `51203` pet not found (pet medical-info update)
- `51204` pet not found (pet photo add)
- `51205` pet not found (pet basic-info update)
- `51206` pet parent not found (identity upsert)
- `51207` pet parent not found (parent event create)
- `51208` pet parent not found (profile update)
- `51209` pet parent identity not found (identity delete)
- `51210` pet parent profile not found (mobile OTP create)
- `51211` pet parent mobile OTP not found (verify)
- `51212` pet parent not found (parent photo add)
- `51213` pet parent photo not found (parent photo delete)
- `51214` pet not found (pet delete). Since 2026-08-05 the "delete" is a soft
  delete — see the pet soft-delete note below.
- `51215` pet photo not found (pet photo delete)
- `51216` event not found / not owned by the provider (event edit) → API maps
  to **404 EventNotFound**
- `51217` event not found / not owned by the pet parent (event edit) → API maps
  to **404 EventNotFound**
- `51218` event booking not found for this booker (event-booking cancel) → API
  maps to **404 EventBookingNotFound**
- `51219` event booking already cancelled (event-booking cancel) → API maps to
  **409 EventBookingAlreadyCancelled**
- `51220` pet not found (pet profile-photo update) → API maps to **404 PetNotFound**
- `51221` pet not found (next-consultation upsert on booking complete) → API maps
  to **404 PetNotFound**
- `51222` mobile number already registered to another pet parent (profile
  complete) → API maps to **409 MobileNumberAlreadyExists**. The explicit
  pre-check inside `Parent.CompletePetParentProfile`; the UNIQUE index
  `UX_PetParents_MobileNumber` is the race-safe backstop behind it.
- `51223` pet parent not found (account delete)
- `51224` pet parent account has been deleted (anonymised + permanently disabled) —
  thrown by `Parent.UpdatePetParentProfile`, since an edit would undo the
  anonymisation → API maps to **409 ParentAccountDeleted**
- `51225` pet parent auth identity not found (device-token register) → API maps to
  **404 ParentAuthIdentityNotFound**
- `51226` device token not found for this caller (device-token deactivate) → API
  maps to **404 DeviceTokenNotFound** (mirror of 51005)
- **Booking reviews (`[Review]` schema, 2026-08-08).** One block for both directions
  and both booking kinds, since `Review.UpsertBookingReview` handles all four
  combinations:
  - `51300` booking not found → **404 BookingNotFound** / **404 NightStayBookingNotFound**
    (the store picks by `BookingType`)
  - `51301` caller is not the party the review direction requires → **403 Forbidden**
  - `51302` booking has not reached `COMPLETED` or `PAID` → **409 BookingNotReviewable**
  - `51303` Custom walk-in — no pet-parent record to author or receive a review →
    **400 ReviewNotAppBooking** (mirror of `51163`'s posture on mark-paid)
  - `51304` invalid bookingType / reviewerType / rating — defensive; the API validates first
  - `51305` review not found for this author (photo add) → **404 ReviewNotFound**.
    Unknown id and "not yours" are deliberately the same case, so it can't probe
    whether a review exists (same posture as `51005`)
  - `51306` the review is already at the 5-photo cap → **409 ReviewPhotoLimitReached**
  - `51307` review photo not found for this review + author → **404 ReviewPhotoNotFound**
- **Night-stay booking sprocs** (multi-night boarding — `Booking.NightStayBookings`):
  - `51230` provider not found (night-stay create) → **404 ProviderNotFound**
  - `51231` provider inactive (night-stay create) → **409 ProviderInactive**
  - `51232` pet parent not found (night-stay create) → **404 PetParentNotFound**
  - `51233` pet not found / not owned (night-stay create) → **400 InvalidPetId**
  - `51234` ServiceId is unknown/inactive/not owned/not a NightStay service
    (night-stay create) → **400 InvalidServiceId**
  - `51235` no remaining capacity on one or more nights (night-stay create) →
    **409 CapacityExceeded**
  - `51236` night-stay booking not found (cancel) → **404 NightStayBookingNotFound**
  - `51237` only the booker can cancel (night-stay cancel) → **400 BookingCancellationForbidden**
  - `51238` night-stay booking already cancelled (cancel) → **409 BookingAlreadyCancelled**
  - `51239` the pet already has an active stay on this service whose date range
    overlaps the requested one (night-stay create) → **409 PetAlreadyBooked**. Mirror
    of `51069`: a pet can't board in two places at once. Enforced under
    `UPDLOCK + HOLDLOCK`; only fires when the create names a `PetId`; scoped to the
    same `ServiceId`.
  - `51240` night-stay booking not found (status update) → **404 NightStayBookingNotFound**
  - `51241` caller is not a party to the booking (status update) → **403 Forbidden**
  - `51242` status not permitted for this actor (status update) → **400 BookingStatusNotAllowed**
  - `51243` booking in a terminal state (status update) → **409 BookingStatusTerminal**
  - `51244` booking already in the requested status (status update) → **409 BookingStatusUnchanged**
  - `51245` invalid actor/status value (status update) → defensive; API validates first

## Cosmos

- Database: `pawfront`
- Active container: **`ProviderServices`**, partition `/serviceCategory`,
  document id = providerId. Houses per-category details for all five
  service categories.
- Active container: **`Events`**, partition `/eventCategory`, document id =
  EventId. Houses physical-event details — `maximumCapacity` + venue
  `location` (houseNumber/street/city/zip/country + optional lat/long).
  Ticketing (`isPaid`/`price`) lives on the SQL `Event.Events` row, not
  Cosmos, so it's returned for every event type. Online events get **no**
  Cosmos doc today.
- Bootstrapper (`CosmosBootstrapper`, `IHostedService`) auto-creates DB +
  containers on startup. Add new specs in
  `CosmosBootstrapper.BuildSpecs(...)` as features land.
- Cosmos client uses **System.Text.Json** serializer (configured in
  `CosmosClientFactory`), so all docs respect `[JsonPropertyName]`
  attributes. **Do not revert** to default Newtonsoft.
- The `pet-profiles`, `visit-notes`, `provider-documents` containers are
  reserved but not yet used; their bootstrap entries are commented out.

## Blob

Container: `provider-images` (private; name is legacy — also stores
pet-parent and pet images now). Folders:
- `profile-photos/<providerId>/<guid>.<ext>`
- `service-photos/<providerId>/<guid>.<ext>`
- `events/<providerId>/<guid>.<ext>`
- `pet-parent-profile-photos/<petParentId>/<guid>.<ext>`
- `pet-photos/<petId>/<guid>.<ext>` (gallery)
- `pet-profile-photos/<petId>/<guid>.<ext>` (single primary photo)
- `pet-parent-identities/<petParentId>/<guid>.<ext>`
- `booking-evidence/<bookingId>/<guid>.<ext>` (job-completion evidence)
- `service-banners/<serviceId>/<guid>.<ext>` (per-service card banner)
- `provider-banners/<providerId>/<guid>.<ext>` (provider-level card banner)
- `review-photos/<bookingReviewId>/<guid>.<ext>` (photos on a parent's booking review)

`BlobUploadKind` enum: `ProfilePhoto`, `ServicePhoto`, `EventBanner`,
`PetParentProfilePhoto`, `PetPhoto`, `PetParentIdentity`, `PetParentPhoto`,
`ProviderPhoto`, `PetProfilePhoto`, `BookingEvidence`, `ServiceBanner`,
`ProviderBanner`, `ReviewPhoto`. The
`IPawfrontBlobStorage.UploadAsync` parameter is `ownerId` (generic) — it's a
ProviderId for provider kinds, a PetParentId for pet-parent kinds, a PetId for
pet kinds, a BookingId for `BookingEvidence`, a **BookingReviewId** for
`ReviewPhoto` (which is why review photos are uploaded only after the review row
exists — the path needs its id).

**Universal fetch endpoint:** the container is private, so the mobile client
can't `GET` the blob URL directly. Instead it POSTs to
`POST /api/v1/blob-images` with `{ blobUrl }`; the server streams the bytes
back using `IPawfrontBlobStorage.DownloadAsync(...)`. Wired in
`BlobImageEndpoints.cs` → `MapBlobImageEndpoints()`. Returns
`404 BlobNotFound` for an unknown URL; `400 InvalidRequest` for a URL the
storage adapter can't parse (different container, malformed, etc.).

## Telemetry

OpenTelemetry with **Azure Monitor exporter** (Application Insights). Wired
via `AddPawfrontTelemetry(builder.Configuration, builder.Environment)` in
`Program.cs`. Captures:
- Auto: ASP.NET Core requests, HttpClient, SQL Client, Azure SDK (Cosmos +
  Blob + Key Vault), runtime metrics
- Custom: `PawfrontTelemetry.ActivitySource` for domain spans (used
  sparingly; auto-instrumentation covers most)
- Logs: all `ILogger<T>` calls forwarded to AI as `traces`/`exceptions`
- Enrichment: `ProviderTelemetryEnrichmentMiddleware` tags the current
  request `Activity` with `pawfront.provider_id` from the route

Dev fallback when `ApplicationInsights:ConnectionString` is empty:
console exporter for traces/metrics/logs.

## Features built (current state)

### Provider onboarding flow
1. `POST /provider-onboarding/firebase-auth` — server reads identity claims
   from the Firebase JWT, upserts `Provider.ProviderAuthIdentities` and
   optional FCM token. Client only sends `{ fcmToken, deviceId, devicePlatform }`.
2. `POST /provider-onboarding/profile` — creates `Provider.Providers`,
   links it back to the auth identity.
3. `POST /providers/{id}/mobile-verification/otp` — generates SHA-256
   hashed 6-digit OTP, 10-min expiry. OTP sender is `NoOpProviderMobileOtpSender`.
4. `POST /providers/{id}/mobile-verification/otp/{otpId}/verify` — flips
   profile to `MobileVerified` on success. **Always returns 200**; client
   checks `data.isValidated`.
5. `GET /providers/{id}/profile` — read-back of the persisted personal info
   (`firstName`, `lastName`, `gender`, `mobileCountryCode`, `mobileNumber`,
   `dateOfBirth`, `mobileVerifiedAtUtc`, `onboardingStatus`, `bannerImageUrl`,
   timestamps).
   Backed by `Provider.GetProviderProfile` sproc against `Provider.Providers`.
   Returns 404 `ProviderProfileNotFound` if the row is missing. Same
   `ProviderProfileResponse` shape that step 2 returns.
6. `PATCH /providers/{id}/profile` (2026-07-25) — edits the personal details the
   app's "Edit Profile" screen owns: `firstName`, `lastName`, `gender`,
   `dateOfBirth`. Sproc `Provider.UpdateProviderProfile` (THROW 51113); returns
   the same `ProviderProfileResponse` as the GET, so the client can rebind from
   the response. **Deliberately NOT editable here:** the mobile number +
   country code (a change must go back through OTP verification, and the pair is
   UNIQUE), the banner image (own endpoint), `onboardingStatus` / `isActive`
   (own flows). 404 `ProviderProfileNotFound`; 400 `UnsupportedGender` /
   `InvalidRequest`. Mirror of the parent host's
   `PATCH /pet-parents/{id}/profile`.

### Provider account delete = anonymise + disable (2026-07-25)

`DELETE /providers/{providerId}` replaced the app-side "deactivate + drop the
Firebase login" workaround that left every personal field in place. It is
**deliberately NOT a row delete**: the `ProviderId` is kept so everything
referencing it keeps its meaning. Deleting the rows would destroy *both* parties'
history — a pet parent's past bookings reference this provider too.

**Retained, untouched:** `Booking.Bookings`, `Booking.NightStayBookings` and all
their audit / evidence / start-OTP / modification / prescription children;
`Event.Events` + amenities / payout methods / ticket bookings; the
`Booking.BookingPayments` ledger; and the `Provider.ProviderServices` rows
(bookings FK to `ServiceId`), which are **deactivated** rather than removed.

**Anonymised / cleared** by sproc `Provider.DeleteProvider` (one transaction,
`UPDLOCK + HOLDLOCK` on the provider row so a concurrent booking create or
active-status toggle serialises behind it):
1. `Provider.Providers` — name → `Deleted Provider`, gender →
   `PreferNotToSay`, DOB → `1900-01-01`, mobile → a placeholder derived from the
   ProviderId, `MobileVerifiedAtUtc` / `BannerImageUrl` → NULL, plus
   **`IsActive = 0`** and the new **`IsDeleted = 1`** + `DeletedAtUtc`.
   `IsActive = 0` is what blocks new bookings (`Booking.CreateBooking` THROWs
   51067 → **409 ProviderInactive**); `IsDeleted = 1` is permanent and
   additionally blocks reactivation and profile edits (THROW **51115** → **409
   ProviderAccountDeleted**, guarded in `Provider.SetProviderActiveStatus` and
   `Provider.UpdateProviderProfile`).
2. `Provider.ProviderAuthIdentities` — `FirebaseUserId` / `Email` → placeholders,
   display name / photo / phone / tenant → NULL. This severs the login. Because
   `FirebaseUserId` is UNIQUE (and `(MobileCountryCode, MobileNumber)` likewise),
   scrubbing both **frees the real Firebase uid and phone number**, so the person
   can sign up again and gets a brand-new identity + ProviderId.
3. Deleted outright — operational config and media only, none of it historical:
   device tokens (stops push), mobile OTPs, `ProviderPhotos`,
   `ProviderServiceBanners`, `ProviderWeeklyAvailability`, `ProviderClosures`,
   `ProviderCancellationPolicies`, `ProviderPayoutMethods`, and
   `ProviderServiceRegistrations`. Dropping the live cancellation policy is safe
   because every booking snapshotted its own at creation (2026-07-24 batch).

**Cosmos** — the provider's `ProviderServices` offering document is deleted via
the new category-agnostic `IProviderServiceCosmosStore`. That document is the
public service **listing**, and discovery (`GET /providers`) is Cosmos-only, so
removing it is what takes the provider out of browse results;
`GET /providers/{providerId}` then returns 404 `ProviderNotRegistered`. The
`Events` extension documents are **kept** — the events are kept.
**Blob** — best-effort delete of the provider banner, gallery photos and
per-service banners. Event banners and booking evidence are kept (their records
are kept).

SQL cannot reach the other two stores, so the sproc returns **three result sets**
— summary, Cosmos listing partition keys, blob URLs — captured before the scrub.
Cosmos/Blob cleanup is best-effort and log-only on failure: the SQL scrub has
committed, so failing the request would wrongly imply the account survived. A
failed Cosmos delete only leaves a stale listing in discovery; bookings are still
blocked, since that gate is the SQL `IsActive` flag.

**Idempotent** — placeholders are derived from the ProviderId, and a second call
returns the original `DeletedAtUtc` with `wasAlreadyDeleted: true` instead of
churning new values.

The parent-facing surfaces degrade correctly: `providerDetails` on a booking
detail and the "my bookings" card name both read from SQL
(`IProviderNameReader` → `Provider.Providers`), so they show **"Deleted
Provider"** rather than going blank.

Unlike the rest of this host — which trusts the route id — the delete resolves
the **caller's own** ProviderId from the JWT
(`ResolveProviderByFirebaseUidAsync`) and returns **403 Forbidden** unless it
matches the route.

### Pet-parent account delete = anonymise + disable (2026-07-27)

`DELETE /pet-parents/{petParentId}` on the **parent host** is the twin of the
provider delete above, and follows it decision-for-decision: **not a row delete**.
The `PetParentId` is kept so everything referencing it keeps its meaning — and
the point that settles it is that this history is not only the parent's. A
provider's completed jobs, their earnings, and their event attendee lists all
reference this parent; deleting the rows would erase the **provider's** records
too.

**Unfinished jobs REFUSE the delete (2026-08-05).** Before anything is scrubbed,
the sproc collects every booking of either kind whose status is not terminal — a
request the provider hasn't answered, a confirmed job still to come, one underway,
or one with an open modification proposal — and if there are any it returns them
instead of deleting, leaving the account untouched. The API answers **409
`PendingJobsExist`** with the list in the response `data` (`ApiResults.Conflict<T>`
keeps the standard envelope: `success: false`, `error` populated, `data` carrying
`{ petParentId, pendingJobs: [{ bookingId, bookingType, jobId, providerId,
providerName, providerProfilePhotoUrl, serviceCategory, subCategory, status,
serviceDate, checkOutDate, startTime, endTime, petName, serviceId,
serviceItemCode, price }] }`, soonest first — see the shared pending-job shape
below). **No force override**, matching the
provider-side deactivation and closure flows. The check lives in the sproc rather
than the app layer so it is race-safe for free: it runs inside the transaction
that already holds `UPDLOCK + HOLDLOCK` on the parent row, which is the row a
concurrent `Booking.CreateBooking` must read. Note a `CREATED` booking past its
expiry rule still reads `CREATED` until the sweep job flips it, so it can block
for up to one sweep interval — self-correcting, and blocking briefly is the safe
direction.

### The pending-job shape (2026-08-09) — shared by BOTH deletes

`DELETE /pets/{petId}` refuses on the same rule, scoped to that pet
(`Parent.DeletePetParentPet`, result set 2 — see the pet soft-delete note below),
and both refusals return the **identical job object**, so the app renders one
card. The two sprocs deliberately project the same columns in the same order and
`PendingJobReader` is the single C# reader — change one sproc and you must change
the other and it.

Two of the fields **cannot come from SQL** and are filled afterwards by the
Application orchestrator `IPendingJobEnricher` (`PendingJobEnricher`), which the
two endpoint handlers call on the 409 path:
- **`providerProfilePhotoUrl`** — the provider's photo lives in their **Cosmos**
  offering document (business image for a shop/hotel/clinic, the freelancer's own
  image otherwise); the `Provider.Providers` row has no photo column. Same source
  as `providerDetails.providerPhotoUrl` on a booking detail.
- **`price`** `{ pricePerUnit, priceUnit, totalAmount, pawfrontFee, feePercentage }`
  — the sproc hands over the **price-locked unit rate** it does have (plus
  `serviceId` / `serviceItemCode` / `checkOutDate` as the other inputs) and the
  enricher does the arithmetic, falling back to the live offering only for a legacy
  row that froze no rate. **The arithmetic mirrors `BookingService.GetDetailAsync`
  and `NightStayBookingService`** — rate × hours for PetSitter day care, rate ×
  nights for a stay, the flat snapshot otherwise. `priceUnit` ∈ `PerHour` |
  `PerNight` | `PerService` | `PerAppointment` | `PerSession` (the four single-day
  values are the same strings the search cards' `chargesUnit` uses) and is **always
  populated even when the amounts are null**, since it follows from the booking kind
  and category. The commission is never zero-rated the way a Custom walk-in is on a
  booking detail: a job that can block a delete was found by PetParentId, so it is
  an App booking by definition.

Both lookups are **best-effort**: this path has already decided to refuse, and
failing it over a missing offering document would replace a clear "settle these
first" with an opaque 500. Provider summaries and offerings are cached per call,
since a parent's unfinished jobs are often with the same provider.

**Retained, untouched:** `Booking.Bookings`, `Booking.NightStayBookings` and all
their audit / evidence / start-OTP / modification / prescription children; the
`Event.Events` the parent organised (+ amenities / payout methods / ticket
bookings); their own `Event.EventBookings` tickets (matched by free-text booker
e-mail, no FK); and the `Booking.BookingPayments` ledger.

**Anonymised / cleared** by sproc `Parent.DeletePetParent` (one transaction,
`UPDLOCK + HOLDLOCK` on the parent row so a concurrent booking create or profile
edit serialises behind it):
1. `Parent.PetParents` — name → `Deleted User`, gender → `PreferNotToSay`, DOB →
   `1900-01-01`, mobile → a placeholder derived from the PetParentId, address →
   `Deleted` / `00000` / lat-long `0`, `Description` → `''`, `ProfilePhotoUrl` /
   `MobileVerifiedAtUtc` → NULL, plus the new **`IsDeleted = 1`** + `DeletedAtUtc`.
   `IsDeleted` is permanent and blocks profile edits (THROW **51224** → **409
   ParentAccountDeleted**), since an edit would undo the anonymisation.
2. `Parent.ParentAuthIdentities` — `FirebaseUserId` / `Email` → placeholders,
   display name / photo / phone / tenant → NULL. This severs the login: the JWT
   stops resolving, so every ownership-filtered route answers **403
   ParentProfileNotCompleted**. Because `FirebaseUserId` is UNIQUE (and
   `(MobileCountryCode, MobileNumber)` on the profile row likewise), scrubbing
   both **frees the real Firebase uid and phone number** — the person can sign up
   again and gets a brand-new identity + PetParentId.
3. `Parent.Pets` — **anonymised in place, not deleted**, and marked
   `IsDeleted = 1` (the same flag the per-pet delete sets). Bookings FK to `PetId`
   and `Booking.GetBookingDetail` reads `petDetails` through that join, so
   deleting the pets would blank out the provider's own booking history — which is
   also why `DELETE /pets/{petId}` itself became a soft delete on 2026-08-05
   (see below); it used to detach bookings because it was a real delete.
   The pet's identity goes — name → `Deleted Pet`, `MicrochipId` →
   NULL (a real-world UNIQUE identifier; clearing it frees the chip for
   re-registration), photo + description + medical free-text → NULL — while the
   animal facts that keep a past booking meaningful stay (type, breed, gender,
   DOB, weight, vaccination / sterilization status, temperament).
4. Deleted outright — operational data and media only, none of it historical:
   `ParentDeviceTokens` (stops push), `ParentMobileOtps`, `ParentIdentities` (the
   identity document), `PetParentPhotos`, the pets' `PetPhotos`, and
   `PetNextConsultations` (forward-looking reminders, not history).

**No Cosmos leg** — a pet parent owns no Cosmos document. The events they
organised do, and those events are retained, so their venue/capacity docs stay.
**Blob** — best-effort delete of the profile photo, parent gallery, identity
document, and the pets' profile + gallery photos. Booking evidence and event
banners are kept (their records are kept).

SQL cannot reach Blob Storage, so the sproc returns **three result sets** —
summary (now carrying `BlockedByPendingJobs`), blob URLs, and the pending jobs
(empty except on the refusal path) — summary
and blob URLs, captured before the scrub. Blob cleanup is best-effort and
log-only on failure (`ParentAccountService`): the SQL scrub has committed, so
failing the request would wrongly imply the account survived, and the rows
pointing at any leftover blob are gone either way.

**Idempotent** — placeholders are derived from the PetParentId, and a second call
returns the original `DeletedAtUtc` with `wasAlreadyDeleted: true`.

The provider-facing surfaces degrade the same way the parent-facing ones do for a
deleted provider: `parentDetails` / `petDetails` on a booking detail and the
provider-facing customer card read live from `Parent.PetParents` / `Parent.Pets`,
so they show **"Deleted User"** and **"Deleted Pet"** rather than going blank.
Bookings that snapshotted the parent's address at creation keep the address they
were made with.

Unlike the provider host's delete, no explicit JWT check is needed: the route
lives in the `RequireOwnedPetParent()` group, which already resolves the caller's
PetParentId from the JWT and rejects any other id with **403 Forbidden**.

### Service categories (5 of them)

Each category has a **basic registration** + **offering** (where applicable):

| Category | Sub-categories | Offering built? | Cosmos doc shape |
|---|---|---|---|
| Pet Sitter | `PetHotel`, `FreelancePetSitter` | ✅ Day/Night branches | Both sub-types share `PetSitterLicense` + `PetSitterOffering` with `BoardingOffering` for `dayCare`/`nightStay` |
| Pet Groomer | `GroomerShop`, `FreelanceGroomer` | ✅ multi-item menu (18-service catalog; per-groomer price + duration; shop-wide capacity) | `PetGroomerLicense` + `PetGroomerOffering` with `GroomingOffering.Services[]` |
| Pet Trainer | `TrainingSchool`, `FreelanceTrainer` | ✅ single session, multi-location, free-form approach/experience | `PetTrainerLicense` + `PetTrainerOffering` with `TrainingSession` |
| Pet Adoption & Sale | `PetShelter`, `PetShop`, `Freelance` | ❌ basic registration only | (no offering yet) |
| Vet | `VetClinic`, `FreelanceVeterinarian` | ✅ single appointment, freelance pinned to 1 concurrent | `VetCertificate` + `VetOffering` with `VetAppointment` |

All registrations also write a row to **`Provider.ProviderServiceRegistrations`** (SQL
filter index with lat/lng) for fast geo + category filtering before
hitting Cosmos for details. **UNIQUE on `(ProviderId)` — a provider can
offer only ONE service category at a time.** Attempting to register a
second category returns `409 ServiceCategoryConflict`. The pre-check
`IProviderServiceLocationRegistry.EnsureCategoryAvailableAsync` runs
before the Cosmos write so no orphan Cosmos docs are created; the SQL
sproc also throws `51011` as a defense-in-depth race guard.

Allowed enum values vary per category — see each category's Cosmos
registry (`CosmosXxxServiceRegistry`) for the canonical lists. Common
ones: `AnimalsHandled`, `AddOns`, `DogTemperaments`, `ServiceLocation`.

### Provider policies
- `POST /providers/{id}/policy/payout-methods` — multi-select from
  `{Cash, Digital}`, stored in `Provider.ProviderPayoutMethods` (junction).
- `POST /providers/{id}/policy/cancellation` — single nullable value
  (`null | 24 | 48 | 72 | 96` hours), stored in
  `Provider.ProviderCancellationPolicies` (one row per provider).

### Provider profile/registration field rules (2026-07-25 QA batch)
- **Mobile duplicates are keyed on (country code + number).**
  `UX_Providers_MobileNumber` is UNIQUE on `(MobileCountryCode, MobileNumber)`,
  so `+41 791234567` and `+49 791234567` both register. Deployments created
  before the key became composite kept a `MobileNumber`-only index, and
  `DeployAll.sql`'s `IF NOT EXISTS`-by-name guard could never repair it — the
  script now **drops a mis-keyed index and recreates it** (widening a UNIQUE key
  can't fail on existing rows). `SqlProviderOnboardingService` also matches the
  index **name** before mapping SQL 2601/2627 to `409 MobileNumberAlreadyExists`,
  so an unrelated unique violation isn't reported as a duplicate number.
- **Gender accepts five values** — `Male`, `Female`, `NonBinary`, `Other`,
  `PreferNotToSay` (`NormalizeGender` + `CK_Providers_Gender`). Same repair
  problem as above: the CHECK on an already-created table was never updated, so
  `DeployAll.sql` now **drops and recreates `CK_Providers_Gender`** whenever its
  definition is out of date. Unknown value → `400 UnsupportedGender`.
- **Optional on every category's basic registration:** `telephoneCountryCode`,
  `telephoneNumber`, `website`, `description` / `aboutYou`. The "Add Your Info"
  screen dropped the telephone field after QA, so the pair is no longer
  `Required(...)` in the Cosmos registries — omitted telephone/description read
  back as `""`, omitted website as `null`. Still required: name, address, zip,
  city, email. (The historic `"0000000000"` placeholder numbers in the database
  are from the old server-side requirement.)
- **Provider-level banner image** — `Provider.Providers.BannerImageUrl`, set via
  `POST /providers/{id}/banner-image` (multipart, ≤5 MB, JPEG/PNG/WebP →
  `provider-banners/<providerId>/`). Category-agnostic and settable from the
  moment the profile row exists, so registration can capture it alongside the
  profile photo — unlike `Provider.ProviderServiceBanners`, which is keyed by
  `ServiceId` and only exists once an offering is saved. Returned as
  `bannerImageUrl` on `GET /providers/{id}/profile` (provider host) and
  `GET /providers/{providerId}` (parent host), and used as the **fallback** for
  the five search cards' `bannerImageUrl` when the service has no banner of its
  own. Application interface `IProviderBannerImageService`
  (`SaveAsync`/`GetAsync`/`GetByProviderIdsAsync`), SQL impl over sproc
  `Provider.UpdateProviderBannerImage` (THROW 51112). Not surfaced on the slim
  `GET /providers` discovery card, which still carries only `imageUrl`.

### Pet Groomer menu (18 services, per-groomer price + duration)
- Pet Groomer is the only category that uses a **per-item menu** under a single
  bookable service. The provider still gets exactly ONE `ProviderServices` row
  (ServiceType=`GroomingSession`), but inside the Cosmos
  `PetGroomerOffering.Session.Services` array they list which canonical grooming
  services they offer.
- **Canonical catalog (server-side constant)** in
  [`GroomingServiceCatalog.cs`](src/Pawfront.Infrastructure.Cosmos/Services/PetGroomer/GroomingServiceCatalog.cs):
  18 stable codes (`WireCoatHandStripping`, `PuppyFirstGroom`, `BreedSpecificStyling`,
  `Dematting`, `BathDryAndBrush`, `BathAndDry`, `DeShedding`, `MedicatedBath`,
  `TickAndFleaRemoval`, `CatGrooming`, `CoatDyeing`, `NailsClipping`,
  `EarCleaning`, `OralHygienePack`, `AnalGlandExpression`, `PawPadTrimming`,
  `SummerCoatPreparation`, `WinterCoatPreparation`) each with a display name.
  **Adding a new service code = code change + release.** The catalog is embedded
  in the `GET /providers/{id}/services/pet-groomer` response (`serviceCatalog`
  field) so the mobile picker can render it without a separate fetch.
- **Per-groomer offering item** in the offering doc:
  `{ code, description?, price, durationMinutes (5–480), isActive }`. Each provider
  sets their own price AND duration for each service they offer; isActive lets them
  temporarily disable a single service without dropping the whole offering.
- **`description` (2026-07-29) is optional, per-provider free text** (max 500 chars;
  blank/omitted stores as absent and reads back `null`) — the groomer's own words
  about that menu item, shown to the parent when they pick a service. Cosmos-only,
  no SQL/migration: documents saved before the field simply read back `null`. The
  offering save **replaces the whole `services[]` array**, so the client must resend
  descriptions with every save (same as price + duration). It is **NOT** snapshotted
  onto the booking and is **not** part of the `terms-changes` drift set — it's
  cosmetic copy, always read live, so a typo fix never re-rules an existing booking.
  Surfaced to the parent in three places: `GET /providers/{providerId}` (inside
  `offering.session.services[]` — the screen where they pick the item),
  `GET /providers/search/groomers` when a `serviceItemCode` narrows the search
  (`description` on the card), and the `serviceDetails.description` block on
  `GET /pet-parents/{petParentId}/bookings`.
- **PetTrainer's equivalent already existed** — the offering's
  `privateTrainingDescription`, which describes the trainer's single bookable
  session. No new trainer field; it is now surfaced on the same three parent
  surfaces (lifted to a top-level `serviceDescription` on
  `GET /providers/{providerId}`, `description` on the trainers search card, and
  `serviceDetails.description` on the bookings list). The other three categories
  have no per-service text, so those fields stay null there.
  **It is OPTIONAL on the offering save (2026-08-09)** — it used to be
  `Required(...)` in `CosmosPetTrainerServiceRegistry`, which returned 400 and
  blocked a trainer who simply hadn't written one, while the app's UI treats it
  as optional. Now `Trim(...) ?? string.Empty`, matching `description` /
  `aboutYou` on the basic registration: blank/omitted stores `""` and reads back
  `""` on the provider host's own GET. The two parent-facing surfaces already
  collapse blank to null (`ProviderPublicProfileService.ResolveServiceDescription`
  and `ProviderOfferingResolver`), so an unwritten description is simply absent
  there rather than an empty block. The request contract's parameter carries a
  C# default, so it also leaves the OpenAPI `required` list.
- **Capacity is shop-wide.** `maxPetsAtOneTime` on the parent offering governs
  how many simultaneous grooming bookings the groomer can take across ALL
  services. So Full Groom at 2pm and Nail Trim at 2pm share the same slot
  bucket — one groomer = one slot.
- **Booking flow.** `POST /providers/{id}/bookings` body for PetGroomer requires
  `serviceItemCode`. Server validates the code is on the provider's menu,
  `isActive=true`, and that `EndTime - StartTime` matches the item's
  `durationMinutes`. Errors:
  - 400 `ServiceItemCodeRequired` — code missing for a PetGroomer booking.
  - 400 `ServiceItemNotOffered` — code is not on this provider's menu.
  - 409 `ServiceItemInactive` — code is disabled by the provider.
  - 400 `InvalidBookingTime` — duration mismatch.
- **Slot flow.** `GET /providers/{id}/availability/slots` for a PetGroomer
  ServiceId requires `?serviceItemCode=` (durationHours is ignored for grooming).
  Server resolves duration from the menu item. Same error codes as booking.
- **Bookings table.** `Booking.Bookings.ServiceItemCode NVARCHAR(64) NULL` —
  populated only for PetGroomer bookings; surfaced on every booking read.

### Provider master Active/Inactive switch
- `Provider.Providers.IsActive` (BIT, default 1) is a single master switch
  above the per-service catalog. When 0, `Booking.CreateBooking` rejects
  every new booking with **51067 → 409 ProviderInactive**, regardless of
  which `ServiceId` is targeted. Existing confirmed bookings are not
  affected; the flag only gates *new* booking creation.
- `POST /providers/{id}/active-status` body
  `{ isActive: bool, acknowledgeExistingBookings?: bool }`. Always
  returns 200 + envelope; the response payload is **discriminated**:
  - `status: "Updated"` → flag flipped; `isActive` + `updatedAtUtc` populated,
    plus `honouredBookingCount` (see below).
  - `status: "BookingsExist"` → only emitted on deactivation; flag NOT
    flipped; `conflictingBookings` lists every future confirmed booking on
    any of the provider's services (with `serviceCategory`, `subCategory`,
    `bookingDate`, etc.) plus a `warningMessage`.
- **"Honour bookings & deactivate" (`acknowledgeExistingBookings: true`,
  2026-08-09).** Deactivating used to be all-or-nothing: a provider who simply
  wanted to stop receiving NEW requests had to cancel real customers first, which
  is the opposite of what they were trying to do. The flag says the provider has
  seen the conflict list and commits to serving those jobs — the switch flips
  immediately (they leave discovery and every create is refused with 409
  `ProviderInactive`) and **the existing bookings are left completely alone**.
  Nothing about the job lifecycle had to change for that to work: `IsActive` is
  read by exactly three procedures — `Booking.CreateBooking`,
  `CreateCustomBooking`, `CreateNightStayBooking` — so an honoured job stays
  startable, completable and payable for free. The response reports
  `honouredBookingCount` (0 on activation and on a deactivation with nothing
  outstanding) and reuses `warningMessage` for the confirmation sentence.
  **Without the flag the historic refusal is unchanged**, which is what lets the
  app show the choice: deactivate → 200 `BookingsExist` + list → "Honor Bookings
  & Deactivate" → resubmit with the flag. There is still **no `force` override**
  — no path cancels a customer's booking on the provider's behalf.
- Sproc `Provider.SetProviderActiveStatus` (`@AcknowledgeExistingBookings BIT = 0`)
  holds `UPDLOCK + HOLDLOCK` on
  the provider row + on the Bookings overlap-count query, so concurrent
  `Booking.CreateBooking` on any of the provider's services serialises
  behind it (race-safe). That still matters on the acknowledge path: the
  provider agreed to honour the bookings they were **shown**, so one landing
  mid-flip is rejected by the flag rather than silently added to their
  commitments. Activation is always applied immediately, no conflict check.
- "Future booking" = `BookingDate > today` OR (`BookingDate = today` AND
  `COALESCE(ActualEndTime, EndTime) > now`). The provider's already-served
  bookings on today are not considered conflicts.
- **The conflict list is single-day only** — it reads `Booking.Bookings` and
  never `Booking.NightStayBookings`, so a provider whose only outstanding
  commitment is a boarding stay is neither blocked nor shown it. Pre-existing;
  it matters more now that the list is what the provider agrees to honour.
  Widening it needs a `bookingType` discriminator + checkout date on
  `ActiveStatusConflictingBookingSummary`, the way the pending-job shape does.
- `IsActive` is surfaced on `GET /providers/{id}/profile` and `GET
  /provider-onboarding/me` (in the latter as nullable, since a pre-profile
  auth identity has no IsActive).

### Booking lead time — a booking must start 2+ hours from now (2026-08-02)

**Rule #1 of the booking rules.** A parent browsing at 11:00 sees **13:00** as the
first bookable slot, however wide open the provider's day is. Single constant +
helpers in [`BookingLeadTime`](src/Pawfront.Application/Bookings/BookingLeadTime.cs)
(`Minimum = 2h`).

The cutoff is derived from the **same instant the parent's modification window
uses** — `serviceStart`, i.e. `BookingDate + StartTime` (single-day) or
`CheckInDate + DropOffTime` (night-stay), UTC. So the two rules now bracket a
booking's life symmetrically: creatable up to `serviceStart − 2h`, changeable up
to the same cutoff.

- **Where it's enforced.** In the **shared slot service**
  (`ProviderAvailabilitySlotService`), which is the single reader behind the
  slots endpoints on **both** hosts, `IProviderWindowAvailabilityChecker`
  (`GET /providers`) and all **five** `/providers/search/*` cards — so every
  parent-facing availability surface inherits it from one place, and what's shown
  stays exactly what create will admit. Plus the create paths as the backstop.
- **Too-soon slots are DROPPED, not zeroed.** `remainingCapacity: 0` means "full,
  try another day", which is the wrong thing to say about a slot that merely came
  too soon. This also, as a side effect, fixes a pre-existing quirk: today's
  **already-elapsed** slots used to be emitted (09:00 still listed at 11:00) and
  now aren't, since a past slot fails the same test.
- **Night-stay** is gated on drop-off on the check-in day, so a same-day stay with
  an 18:00 drop-off is still bookable at 11:00. `OfferingResolution.Resolved`
  gained a `DropOffTime` (populated for `NightStay` only) so the night-availability
  surface and the create path derive the identical instant. An offering with no
  drop-off recorded falls back to the start of the night.
- **The agenda** (`GET /providers/{providerId}/agenda`) still **shows** the blocked
  time — the parent is browsing the provider's day, and a 09:00 job is part of that
  day either way — but marks it `isBookable: false`. The cutoff becomes a block
  **boundary** like a break or closure, so a free morning splits at 13:00 rather
  than the whole merged block being mislabelled.
- **Custom walk-ins are EXEMPT** (`BookingService.CreateCustomAsync`). The provider
  is recording a job happening now; requiring two hours' notice would make the
  feature unusable. Every other create path — App bookings on both hosts,
  single-day and night-stay — enforces it.
- **Rejected with 409 `BookingLeadTimeTooShort`** (`BookingLeadTimeTooShortException`),
  matching `ModificationWindowClosed`'s shape: the request is well-formed, it just
  conflicts with a booking policy. The message names the earliest bookable start.
- **Application layer only, no SQL guard** — deliberately, and unlike capacity. It
  isn't a race: a window that clears the cutoff at validation still clears it
  milliseconds later in the sproc. Same posture as the working-hours and closure
  gates.

### Provider weekly availability + slot computation (Rounds 1 & 2 of calendar)
- `POST /providers/{id}/availability` — saves all 7 day rows atomically
  (delete + insert). Body: `{ "days": [{ dayOfWeek, isOpen, startTime?,
  endTime?, breakStartTime?, breakEndTime? }, ...] }`. Exactly 7 entries,
  dayOfWeek 0..6 each appearing once (0 = Sunday). One optional break per
  day, must fit inside the working window.
- `GET /providers/{id}/availability` — returns whatever is stored
  (`days` is empty until first save; mobile uses that as the "not set yet"
  signal).
- Stored in `Provider.ProviderWeeklyAvailability` with composite PK
  `(ProviderId, DayOfWeek)` and CHECK constraints enforcing all the
  invariants (closed-day has no times, open-day has both, break inside
  window, start < end, etc.). `ON DELETE CASCADE` from `Provider.Providers`.
- `GET /providers/{id}/availability/slots?serviceId=GUID&date=YYYY-MM-DD&durationHours=2&granularityMinutes=30`
  — Reads the `ProviderServices` row for the ServiceId, pulls the matching
  Cosmos offering branch (DayCare vs NightStay vs Session vs Appointment)
  for capacity + duration rule, then walks the working windows for that date
  (minus break, minus any partial-day closures **on this ServiceId**) at the
  requested granularity. Subtracts overlapping confirmed bookings on this
  ServiceId against the offering's capacity. Returns
  `{ providerId, serviceId, date, serviceCategory, subCategory, serviceType,
     durationHours, capacity, granularityMinutes, slots: [...], nights: null }`.
  - **Each slot carries `remainingCapacity`** (2026-07-11): the offering's
    capacity minus the active bookings overlapping that window — the same
    overlap count the race-safe create sproc uses, so what's shown is exactly
    what create will admit. Fully-booked slots ARE emitted with
    `remainingCapacity: 0` (2026-07-17) so the client can render them as
    unavailable; the window-availability checker and the five booking
    searches only count slots with positive remaining capacity as bookable.
  - **NightStay is DATE-granular** (2026-07-11 refactor): capacity is per
    night, not per time window, so for a NightStay ServiceId the SAME endpoint
    ignores `durationHours`/`granularityMinutes`, leaves `slots` empty, and
    fills `nights: [{ date, activeBookings, remainingCapacity, isClosed,
    isAvailable }]` — one entry per night in `[date, endDate]` (`endDate` is a
    new optional query param on both hosts; defaults to `date`; max 31 nights
    → 400 InvalidRequest). A night is unavailable when a FULL-DAY closure on
    the service covers it or active stays (`CheckInDate <= night <
    CheckOutDate`) have used every capacity unit — exactly the create-path
    gates (the weekly time grid is deliberately NOT consulted, mirroring
    night-stay create). Backed by `INightStayOccupancyReader`
    (`NightStayBookingService` second interface → sproc
    `Booking.GetNightStayOccupancy`, range-based). The
    `/providers/search/night-stay` per-night probe and the generic
    `/providers` window checker both branch to this night surface for
    NightStay services.
  - **Duration rule per service type:** PetSitter `DayCare` and
    PetGroomer (`GroomingSession`) require `durationHours >= offering minimum`;
    PetTrainer (`TrainingSession`) and Vet (`VetAppointment`) require
    `durationHours == offering fixed duration`; `NightStay` takes no duration
    (date-granular, see above); PetAdoptionAndSale has no
    `ProviderServices` row and therefore can't be queried here.
  - **Capacity comes from the offering** (`maxPetsAtOneTime` /
    `maxConcurrentSessions` / `maxConcurrentConsultations`) but is scoped by
    ServiceId — DayCare and NightStay each get their own capacity bucket.
- **Parent-facing daily agenda (2026-07-29).** `GET /providers/{providerId}/agenda
  ?serviceId=&date=` on the **parent host** answers a different question from the
  slot endpoint above: not "where does a booking of THIS length fit?" but "what
  does the day look like?". It therefore takes **no duration**, which is the whole
  point — the parent browses the provider's day, picks a gap, and only then asks
  `/availability/slots` for the exact windows. Backed by
  `IProviderDailyAgendaService` (`ProviderDailyAgendaService`), which reads the
  same offering capacity, weekly hours, per-service closures and active bookings
  the slot service reads, so the two surfaces can never disagree about what's
  occupied.
  - **Shape:** a contiguous, non-overlapping timeline of blocks covering the
    working hours, each `{ startTime, endTime, entryType, status, jobId,
    bookingId, remainingCapacity, isBookable }`. The day is cut at every booking
    boundary and adjacent blocks that read alike are merged back, so an untouched
    morning is ONE free block, not a run of fragments. `entryType` ∈ `Free` |
    `Booked` | `Break` | `Closed` is the branch-on field; `status` carries the
    detail.
  - **Other parents' jobs are masked.** The caller's own `PetParentId` is resolved
    from the JWT (`ICurrentPetParentContext`), never from the route. Their own
    bookings surface the real lifecycle status + `jobId` (`PF-000123`) +
    `bookingId`; every other booking flattens to `status: "BOOKED"` with null ids,
    and a Custom walk-in (null `PetParentId`) always masks. A caller with no
    profile masks everything — the null-vs-null comparison is explicitly guarded so
    they don't "own" every walk-in.
  - **`remainingCapacity` is why a `Booked` block can still be bookable** — a
    3-pet daycare with one job at 14:00 has two places left. Same half-open
    overlap count as the create sproc, so what's shown is what create will admit.
  - **Blocked time is emitted, not omitted** (`Break` / `Closed` rows), so the
    timeline reads continuously from opening to closing. Where a partial-day
    closure overlaps the break, **the closure wins** — the blocks never cover the
    same minute twice.
  - **NightStay** short-circuits to a single whole-day block with
    `openingTime`/`closingTime` null: a stay occupies its bucket for the entire
    night and the create path never consults the weekly time grid.
  - New sproc **`Booking.GetAgendaForDate`** (+ `IDailyAgendaReader`, the richer
    sibling of `IDailyBookingReader`) returns the same rows as
    `Booking.GetBookingsForDate` with `BookingId` / `JobNumber` / `PetParentId` /
    `Status` attached. **The two status predicates are deliberately identical —
    change one, change the other.**
- **Bookings.** Real bookings table `Booking.Bookings` carries `ServiceId`.
  Capacity check is race-safe via `Booking.CreateBooking` sproc with
  `UPDLOCK, HOLDLOCK` on the overlap-count query **scoped by ServiceId**,
  so two concurrent POSTs on the same service serialise and the second is
  rejected once capacity is full.
  - `IProviderOfferingResolver` is the shared "look up service capacity +
    duration rule by ServiceId" reader — used by both the slot service
    and the booking service. It joins the `Provider.ProviderServices` row
    with the matching Cosmos offering branch.
  - `BookingService` implements both `IBookingService` (Create / Get /
    Cancel / list-by-provider / list-by-parent) and
    `IDailyBookingReader` (used by the slot service to subtract
    overlapping bookings against capacity, **scoped by ServiceId**).
    Single registration via `BookingService` resolves both abstractions.
  - Booking validation in C# rejects out-of-hours windows, break
    overlaps, duration mismatches, and closure overlaps **on the
    booked ServiceId** BEFORE hitting SQL. The SQL sproc still has
    the capacity check (per service) as the race-safe last line of
    defense, plus its own ServiceId-belongs-to-provider check.
  - Sub-categories carried as a denormalised snapshot on the
    booking row (so historical bookings keep meaning even if the
    provider deregisters).
  - `GET /providers/{id}/bookings` accepts an optional `?date=YYYY-MM-DD`
    query param that narrows results to a single calendar day (day-view
    UI). Omit it for full history. Filter is applied in
    `Booking.ListBookingsByProvider` via a nullable `@BookingDate` param.
- `GET /providers/{id}/policy` — returns both.

### Booking ("job") status lifecycle + audit

> **Time-driven status changes have LEFT the database and the API hosts
> (2026-08-02).** `EXPIRED`, the auto-settled no-shows, and the expired-
> modification revert to `CONFIRMED` (either party's proposal, widened
> 2026-08-02) are now written by a **scheduled external
> job** — `BookingSweepFunction` in the `Pawfront.Functions` Azure Functions app
> (isolated worker, timer-triggered every 5 minutes; see `src/Pawfront.Functions`).
> It calls three new sprocs in order — `Booking.ExpireStaleCreatedBookings`
> (**BR-17 + BR-53**),
> `Booking.RevertExpiredModificationRequests` (BR-30, before the next one so
> a booking whose proposal expires AND whose provider's working day has also ended
> settles in one pass), `Booking.SettleUnstartedJobsAsNoShow` (BR-38) — replacing
> the single retired sproc `Booking.ExpireStaleBookings` (deleted; `DeployAll.sql`
> **drops** it on re-deploy so a stray caller can't keep flipping rows) and the
> `BookingExpirySweeper` hosted service that ran it every 10 minutes in **both**
> API hosts with no coordination between them — a real double-execution bug the
> new design avoids for free, since the Azure Functions host serialises a timer
> trigger's invocations across however many instances **one** Function App scales
> to (this guarantee needs exactly one Function App deployed for this trigger).
> **No sproc changes a booking's status on the basis of elapsed time any
> more.** The in-sproc time checks that remain — `UpdateBookingStatus` /
> `UpdateNightStayBookingStatus` (THROW 51129 / 51249 **and 51153 / 51273**) and
> `RespondBookingModification` / `RespondNightStayBookingModification` (THROW
> 51152 / 51272) — **reject a late transition without writing anything**.
>
> **Consequence to keep in mind:** stored status and effective status can diverge
> between ticks. A booking 25 hours old — or one whose service is now 90 minutes
> away — still reads `CREATED` while the API
> refuses the accept with 409 `BookingExpired`; a lapsed parent proposal stays in
> `MODIFICATION_REQUEST_BY_PARENT` — which is **not** startable — until the next
> tick reverts it. The in-memory dev fallbacks mirror the reject-only behaviour and
> have no equivalent job at all — a dev running against the in-memory store never
> sees a booking auto-settle.
>
> **BR-38's cutoff changed the same day (2026-08-02):** a single-day job now
> settles at the **later of** the provider's `Provider.ProviderWeeklyAvailability`
> closing time for that weekday and the booking's own `EndTime` — not the
> booking's own end alone. A provider running late still has the rest of their
> working day before an unstarted job counts as a no-show. No weekly-availability
> row for that weekday (or one marked closed) falls back to midnight UTC. Night-stay
> is unchanged (check-in day ends at midnight UTC; no working-hours join — see the
> no-show note further down and `docs/booking-rules.md` BR-38 for the full rationale).

`Booking.Bookings.Status` (`NVARCHAR(48)`, literal uppercase) is the expanded
**job** lifecycle:
- `CREATED` (parent booked) → `CONFIRMED` (provider accepted) **or**
  `PROVIDER_DECLINED` (provider rejected).
- **Start-OTP job lifecycle (2026-07-23 — the `ENDING`/end-OTP leg was removed):**
  `CONFIRMED`-equivalent → `START_JOB` → `IN_PROGRESS` → `COMPLETED`. Three
  provider actions, ONE OTP (server-generated, shown to the **parent**, entered
  by the **provider**):
  1. `POST .../start-job` — provider taps "Start Job"; the booking moves to
     `START_JOB` and the **start-OTP** is issued to the parent. **Two gates, both
     against "now" in UTC:**
     - **The service date must be today** (2026-07-27). Single-day compares
       `BookingDate`, night-stay compares `CheckInDate` (the drop-off day) →
       **409 BookingNotOnServiceDate** (THROW 51144 / night-stay 51264). The
       time-of-day *within* the booked window is still not checked, so a provider
       running early or late can start; they just can't start on the wrong day.
     - **The provider must be inside their own weekly working hours** (2026-07-25:
       replaced the old "15 minutes before the scheduled start" gate). The sproc
       reads `Provider.ProviderWeeklyAvailability` for **today's** day-of-week and
       rejects when the day is closed or `now` is outside `StartTime..EndTime` →
       **409 OutsideWorkingHours** (THROW 51137 / night-stay 51257). The break
       window is deliberately **not** consulted, and a provider who has never
       saved weekly hours is **not** gated.

     The date gate runs first, so a provider who is open but looking at the wrong
     day gets the more specific error. Same rules for both booking kinds.
  2. `POST .../start-job/verify` (body `{ otpCode }`) — provider enters the
     start-code the parent showed → `IN_PROGRESS` (6 wrong attempts cancel the
     job, see `OTP_MAX_ATTEMPTS_EXCEEDED`).
  3. `POST .../complete` (body optional: `{ nextConsultationDate?, prescription? }`)
     — provider marks the job done → `COMPLETED`. **No OTP.**
  The start-OTP lives in `Booking.BookingStartOtps` / `NightStayBookingStartOtps`
  (10-min TTL, reuse-while-valid; the `OtpKind` column from the short-lived
  dual-OTP experiment was dropped). The parent reads the active code via the
  GET-one detail (`startOtp` block, surfaced only at `START_JOB`). Sprocs:
  `Booking.StartBooking` (issues the start-OTP + working-hours gate),
  `VerifyBookingStartOtp`, `CompleteBooking` (no OTP; `EndBooking` /
  `CompleteBookingWithOtp` were dropped) + night-stay mirrors. `JOB_STARTED` (the
  single direct-start state) and `ENDING` (the "End Job" end-OTP state) are
  **retired** — kept in the CHECK list for legacy rows, no longer settable. Once
  `IN_PROGRESS` the job is underway: it can no longer be cancelled (→ **409
  BookingInProgress**, THROW 51149 / night-stay 51269) or reported as a no-show;
  it runs through to `COMPLETED`. Evidence photos remain **optional**.
- `PAID` (2026-07-23) — the **last** stage: the parent has paid the provider. Set
  only from `COMPLETED` by the provider via `POST .../bookings/{id}/paid` (+
  night-stay twin), never by a client-chosen status. The transition writes a
  **payment ledger row** to `Booking.BookingPayments` (see the "Booking payment"
  note below). **App bookings only** — a Custom walk-in can't be marked paid (→
  **400 PaymentNotAppBooking**). **Terminal**, and — like `COMPLETED` — it still
  **holds** the booking's slot (NOT in the capacity-freeing set) and counts toward
  `completedBookings`. Sprocs `Booking.MarkBookingPaid` /
  `MarkNightStayBookingPaid` (THROW 51160-51164 / 51280-51283).
- Either party may propose a schedule change:
  `MODIFICATION_REQUEST_BY_PARENT` / `MODIFICATION_REQUEST_BY_PROVIDER`; the
  counterparty resolves it → `PROVIDER/PARENT_ACCEPTED_MODIFICATION` (new
  details applied to the **same booking id**) or
  `PROVIDER/PARENT_DECLINED_MODIFICATION` (old details kept). All four are
  **"live" resting states** — the job can still be started/modified/cancelled
  from them (`BookingStatuses.ConfirmedEquivalent`).
- `PROVIDER_CANCELLED` / `PARENT_CANCELLED` are cancellation states.
- `PARENT_NO_SHOW` / `PROVIDER_NO_SHOW` (2026-07-12) record the counterparty
  failing to appear — a no-show always **names the absent party**: the
  provider reports `PARENT_NO_SHOW`, the parent reports `PROVIDER_NO_SHOW`
  (each via its own host's `POST .../no-show` endpoint; also settable through
  the legacy generic `/status` shim). Allowed from a confirmed-equivalent state
  **or `START_JOB`** (the provider tapped start but the counterparty never turned
  up / handed over the code) — not from `CREATED`, and not once `IN_PROGRESS`
  (by then both parties met) — and only once the counterparty is actually late.
  **The grace window differs by booking kind** (2026-07-29): single-day is
  **30+ minutes** after `BookingDate + StartTime`; **night-stay is 2+ HOURS**
  after `CheckInDate + DropOffTime` — a boarding hand-over is a slower affair
  than an appointment, so a 09:00 check-in is reportable from 11:00. All UTC;
  earlier → **409 NoShowTooEarly** (THROW 51128 / night-stay 51248). Both are
  **terminal** and free capacity.
- **No-shows also settle themselves once the job's moment passes (2026-07-29).**
  Neither party has to report it: if an accepted job is still unstarted when the
  time for it has gone by, the **scheduled external job** marks it —
  and **decides who was absent from the only evidence the system has**:
  - sitting in **`START_JOB`** → **`PARENT_NO_SHOW`**. The provider was there
    and had the start code issued to the parent, who never handed it back.
  - still **confirmed-equivalent** → **`PROVIDER_NO_SHOW`**. The provider never
    so much as tapped Start.

  **"The moment passed" differs by booking kind:**
  - **single-day** — the **provider's working day ended** (revised 2026-08-02;
    previously the booking's own window elapsing, `BookingDate + EndTime`). The
    cutoff is the **later of** the provider's closing time on the booking date
    (`Provider.ProviderWeeklyAvailability` for that weekday) **and** the booking's
    `EndTime`. Keying off closing time is the point: a provider running badly late
    hasn't no-showed at 11:00 just because a 10:00–11:00 slot came and went — they
    have the rest of the day to serve it. Taking the *later* of the two stops a
    booking being settled while its own window is still running, which is possible
    when the provider narrowed their hours after the booking was made. No weekday
    row, or a closed one ⇒ fall back to midnight UTC. Break not consulted.
    (Before 2026-07-29 this case produced `JOB_EXPIRED`.)
  - **night-stay** — the **check-in day ended** (midnight UTC), so an abandoned
    stay settles at check-in rather than waiting for its checkout day. Deliberately
    NOT switched to working hours: a stay is date-granular and the weekly time grid
    never governs it.

  Applies to the same from-state set as a manual report (confirmed-equivalent +
  `START_JOB`), so `CREATED` (never accepted → `EXPIRED`) and `IN_PROGRESS` (the
  job started) are untouched. `JOB_EXPIRED` is fully superseded and catches
  **nothing** (see below). **Midnight means midnight UTC**, per the
  codebase-wide UTC convention — a Swiss provider's cutoff lands at 01:00/02:00
  local, i.e. slightly in their favour. There is no in-sproc flip; how promptly
  the settle lands is entirely the external job's cadence, and a provider can't
  start the job on a later day anyway (the start-job service-date gate, THROW
  51144 / 51264, already rejects it).
  Note the job can settle a short single-day booking slightly **before** a manual
  report becomes legal (a 15-minute slot ends before the manual 30-minute grace
  elapses) — harmless, since its evidence is the stronger one: the whole window
  is gone.
- `EXPIRED` (2026-07-17) — nobody accepted the booking in time. **Two independent
  triggers** produce it, both meaning the provider ran out of time, and either is
  enough:
  1. it sat in `CREATED` for **24+ hours** without the provider accepting (BR-17);
  2. **(2026-08-02, BR-53)** it is still `CREATED` and the service now starts in
     **under 2 hours** — `BookingDate + StartTime`, night-stay
     `CheckInDate + DropOffTime`, UTC. This is the **same cutoff as the booking
     lead time** (BR-01, `BookingLeadTime.Minimum`), so an unaccepted booking dies
     exactly when a fresh booking for that slot could no longer be created; the
     two rules bracket a booking's life the way the modification window does.
     A booking made 90 minutes before the service therefore expires on the next
     tick, hours short of 24, and a booking whose start has already passed is
     caught by the same test.

  Set automatically by the scheduled external
  job, never by a client. The two status-engine sprocs **reject** a transition
  attempted on a `CREATED` booking either trigger has caught — so a late provider
  accept gets **409 BookingExpired** (THROW 51129 / 51153, night-stay 51249 /
  51273; same error code either way, only the message differs) — but **do not
  write the status**, so a row may still read `CREATED` until the job runs. The
  trigger that fired is recorded in the `BookingStatusHistory` note.
  **Terminal** and frees capacity.
- `JOB_EXPIRED` (2026-07-21) — **legacy as of 2026-07-29; no longer produced.**
  It meant: the provider **accepted** the booking but the job never got underway,
  and its scheduled window **fully elapsed** while still confirmed-equivalent **or
  sitting in `START_JOB`** (start-OTP issued, never verified — i.e. never reached
  `IN_PROGRESS`). That is exactly the situation the no-show arm now settles as
  `PROVIDER_NO_SHOW` / `PARENT_NO_SHOW` (see above), for **both** booking kinds.
  The status itself stays valid, **terminal**,
  and capacity-freeing because rows may still carry it. Distinct from `EXPIRED`
  (a `CREATED` booking never accepted). Nothing in the database writes it. Once
  `IN_PROGRESS`, the job started, so a started-but-never-completed job
  was never `JOB_EXPIRED`.
  **Existing single-day rows are relabelled once by `DeployAll.sql`** — who was
  absent is read back from the audit trail rather than guessed: the `JOB_EXPIRED`
  `BookingStatusHistory` row records the status the booking held when it was
  swept, which is the same evidence the live arm branches on (`START_JOB` →
  `PARENT_NO_SHOW`, otherwise `PROVIDER_NO_SHOW`). A row with no such audit entry
  is left alone — there is nothing to attribute it with. Idempotent, and capacity
  is unaffected since all three statuses free the slot. **Night-stay
  `JOB_EXPIRED` rows are deliberately NOT backfilled** — the same one-time pass
  for `Booking.NightStayBookings` has not been written.
- `OTP_MAX_ATTEMPTS_EXCEEDED` (2026-07-21; **renamed 2026-07-25** from
  `OTP_ATTEMPTS_EXCEEDED`) — the provider entered the wrong
  **start-code 6 times** on `.../start-job/verify`; the 6th failure
  cancels the job. Set by `Booking.VerifyBookingStartOtp` (+ night-stay mirror —
  bump the OTP's `FailedAttemptCount`; on the 6th, invalidate the OTP + flip the
  booking + audit), never by a client → the endpoint returns **409
  OtpAttemptsExceeded** (THROW 51136 / night-stay 51256 →
  `OtpAttemptsExceededException`). **Terminal** and frees capacity.
  A dedicated status (rather than a plain `PROVIDER_CANCELLED`) exists so both
  apps can label the row "OTP Max Attempts Exceeded" instead of showing it as an
  ordinary cancellation. `DeployAll.sql` migrates any rows/audit entries still
  carrying the old `OTP_ATTEMPTS_EXCEEDED` value; the 409 **error code** and the
  `OtpAttemptsExceededException` type deliberately kept their old names, so only
  the persisted status string changed on the wire.
- `APPROVAL_NEEDED` is **deprecated** (superseded by modifications) — still in
  the CHECK list so legacy rows stay valid, no longer settable.

**Capacity-freeing ("inactive booking") predicate is now
`Status NOT IN ('PROVIDER_CANCELLED','PARENT_CANCELLED','PROVIDER_DECLINED','PARENT_NO_SHOW','PROVIDER_NO_SHOW','EXPIRED','JOB_EXPIRED','OTP_MAX_ATTEMPTS_EXCEEDED')`**
— a declined, no-show, expired, job-expired, or otp-cancelled job releases its
slot. This predicate is keyed off by every capacity / closure-conflict /
active-status / slot query (and `GetBookingsForDate`), and
no-show/expired/job-expired/otp-exceeded rows are excluded from the
`completedBookings` stat (`SqlProviderBookingStatsReader`).
New app bookings default to `CREATED`; custom walk-ins start `CONFIRMED`.

### Early completion releases the unused remainder (2026-08-09)

The status predicate above is all-or-nothing: a booking either holds its whole
slot or none of it. That was wrong for a job that **finished early** — a 6-hour
day care wrapped up after 3 hours kept the other 3 hours blocked, and a 3-night
stay collected a day early kept that last night blocked, so nobody else could
book time the provider was demonstrably free for.

Two nullable columns record what actually happened, **without touching what was
agreed**:
- `Booking.Bookings.ActualEndTime TIME(0) NULL`
- `Booking.NightStayBookings.ActualCheckOutDate DATE NULL` (a pickup **day**,
  not a stayed night — same `[CheckInDate, X)` meaning as `CheckOutDate`)

**Occupancy is now `[StartTime, COALESCE(ActualEndTime, EndTime))` and
`[CheckInDate, COALESCE(ActualCheckOutDate, CheckOutDate))`.** Ten queries use
those exact expressions and must stay in step — `CreateBooking` (capacity +
the 51069 pet-duplicate check), `CreateCustomBooking`, `CreateNightStayBooking`
(per-night capacity + the 51239 pet-duplicate check), `GetBookingsForDate`,
`GetAgendaForDate`, `GetNightStayOccupancy`, `RespondBookingModification`,
`RespondNightStayBookingModification`, `Provider.CreateClosures` and
`Provider.SetProviderActiveStatus`. The slot grid and the create gate use the
identical expression, so what is shown free is exactly what create admits.

**Stamped on the COMPLETED transition, from BOTH paths** — the dedicated
`CompleteBooking` / `CompleteNightStayBooking` and the legacy `/status` shim
(`UpdateBookingStatus` / `UpdateNightStayBookingStatus`) — since otherwise which
endpoint the provider tapped would decide whether the slot came back. `PAID`
inherits the value (it is only reachable from `COMPLETED`).

**Clamps, all enforced by CHECK constraints as well as the sprocs.** The value
can only ever *shrink* the window:
- Completed at/after the booked end, or on a **later date** than the service
  date ⇒ left NULL, the whole window is held. The date guard matters: a bare
  `TIME` compared against a job that ran past midnight UTC would otherwise free
  the slot wrongly.
- Single-day is floored at `StartTime` (a job can finish before its booked start
  — starting is gated on the service DATE, not the time of day), which releases
  the slot entirely.
- Night-stay is floored at **one night**: the pet was physically there on the
  check-in day, so that night is never given back.

**It does NOT re-price or refund anything.** `Booking.BookingAmounts`, both
booking-detail reads, the terms snapshots and every wire contract still read
`EndTime` / `CheckOutDate` — so the parent pays what they agreed to and the
provider's earnings are unchanged. Charging less for an early finish is a
product decision, not a capacity one, and was deliberately left alone.

NULL — the value on every pre-existing row — means "occupied everything it
booked", so the change is behaviour-neutral until a job completes early. No wire
contract changed and no C# changed: the two occupancy sprocs already alias the
effective end as `EndTime`, and the in-memory dev store cannot complete a
booking at all (`CompleteAsync` throws `NotInMemory`), so there is nothing to
mirror there.

- **Booking location choice (`LocationType`, 2026-07-08).** Both
  `Booking.Bookings` and `Booking.NightStayBookings` carry a nullable
  `LocationType` column (`'ParentLocation' | 'ProviderLocation'` CHECK) — the
  parent's choice of where the service happens. **Required** on the parent-host
  creates (`POST /pet-parents/{id}/bookings` + `/night-stay-bookings` → 400
  `InvalidRequest` when missing, 400 `UnsupportedLocationType` when invalid);
  **optional** on the provider-host create (`CreateBookingRequest.locationType`).
  Every booking-detail read (single-day + night-stay, both hosts) returns a
  top-level **`location`** section `{ locationType, addressLine, city, zipCode,
  latitude, longitude }`. It is now **snapshotted at booking creation** (2026-07-24,
  see the "Snapshots at creation" note below) — frozen so a later edit to either
  party's address never moves an existing booking. The detail read prefers the
  frozen `Snapshot*` columns and falls back to **live** resolution only for legacy
  rows (no snapshot): ParentLocation → the parent's profile
  address (joined in the detail sprocs); ProviderLocation → the provider's
  business address (Cosmos doc root `address`/`zip`/`city` via
  `IProviderDiscoveryService.GetSummaryAsync` — `ProviderSummary` gained
  `Address`/`Zip`) + lat/lng from `Provider.ProviderServiceRegistrations`
  (best-effort; nulls when unresolvable). Shared helpers:
  `BookingService.ResolveProviderLocationAsync` (live) +
  `BookingService.TrySnapshotLocation` (frozen). Only the **selected** `location`
  block is snapshotted; the `parentDetails` / `providerDetails` **contact** address
  blocks stay **live** (current party contact info, by design). `parentDetails`
  (both detail shapes, both hosts) also always carries the parent's profile address
  (`addressLine`/`city`/`zipCode`/`latitude`/`longitude`; null for Custom
  walk-ins). `petDetails` now carries the **full** medical snapshot — added
  `sterilizationStatus`, `medicalHistory`, `temperament` alongside the existing
  breed/vaccination/prescription fields.
- **Night-stay `JobNotes` (2026-07-08).** `Booking.NightStayBookings.JobNotes
  NVARCHAR(2000) NULL` — optional free-text captured on the parent night-stay
  create (`jobNotes`), surfaced on the night-stay detail's
  `bookingDetails.jobNotes` (both hosts). The flat `NightStayBookingResponse`
  intentionally does NOT carry it.
- **Enriched booking-detail read (single-day only).** `GET /bookings/{bookingId}`
  (provider) and `GET /pet-parents/{petParentId}/bookings/{bookingId}` (parent)
  return `BookingDetailResponse` grouped into **five sections** — `bookingDetails`
  (incl. a friendly `jobId` `PF-000123` from the new `Booking.Bookings.JobNumber`
  IDENTITY column), `parentDetails`, `petDetails`, `providerDetails` (provider
  name/mobile/gender joined from `Provider.Providers`; `providerPhotoUrl`
  (2026-08-05) resolved from the **Cosmos** offering doc — the business image for
  shops/hotels/clinics, the freelancer's own profile image — since the SQL
  provider row has no photo column, which is why it used to read null; **plus the
  provider's business `address`/`city`/`zip`** resolved live from
  the Cosmos service doc via `IProviderDiscoveryService.GetSummaryAsync` — so the
  client needn't call `GET /providers/{id}` just for the address; null when the
  offering can't be resolved), `paymentDetails` — plus the
  top-level `startOtp` (parent reads, when startable) + `pendingModification` +
  `prescription` (the Vet prescription block: `{ prescriptionText, isPetVaccinated,
  vaccinations: [...], nextConsultationDate }`; **null until a vet records one**,
  Vet bookings only — see the "Vet prescription" note below).
  Backed by the new `Booking.GetBookingDetail` sproc (base row + `JobNumber` +
  payout columns, LEFT JOIN `Parent.PetParents` + `Parent.Pets`) and
  `IBookingService.GetDetailAsync`. **App** bookings fill parent/pet (name,
  mobile, gender, photo) from the joined records; **Custom** walk-ins from the
  booking's own free-text fields (gender/photo null). `paymentDetails` is
  computed **live**: `pricePerHour` = the offering's unit rate (Custom = stored
  `PricePerHour`); `totalAmount` = rate × time (flat fee for fixed Vet/Trainer/
  grooming-item); `pawfrontFee` = `totalAmount × Payments:PawfrontFeePercentage`
  — **except private (Custom walk-in) jobs, which carry `pawfrontFee` = 0 and
  `feePercentage` = 0** (off-platform, no commission/taxes; 2026-07-11);
  `payoutStatus`/`payoutId` from the booking's payout columns (`PayoutId` minted
  at `COMPLETED`, `payoutStatus` `Pending` until the provider records the payment
  and `Paid` after, or the terminal **`NO_PAYOUT`** when the job ended as a
  no-show or expired — see below), plus **`payoutMethod`/`paidAtUtc`** (2026-08-05) LEFT-JOINed
  from the `Booking.BookingPayments` ledger — the ledger row is the only place the
  `Cash`/`Digital` the money actually changed hands as is recorded, so both stay
  null until the booking reaches `PAID`.
  Pricing is null when the offering can't be resolved (deactivated service). The
  flat `BookingResponse` (create/list/status) is unchanged. Night-stay detail is
  **not** enriched yet (separate entity/endpoint — follow-up).
- **One dedicated endpoint per transition** (no shared generic setter — though
  the legacy `POST .../bookings/{id}/status` stays as a back-compat shim). Each
  is a thin handler that pins the target status + actor (from the host/route);
  the actor's id is never taken from the body. Provider host:
  `POST /providers/{providerId}/bookings/{bookingId}/{accept|decline|start-job|start-job/verify|complete|cancel|no-show}`
  + `GET /terms-changes`, `/modifications`, `/modifications/{accept|decline}`,
  `POST/GET /evidence`.
  Parent host: `POST /pet-parents/{petParentId}/bookings/{bookingId}/{cancel|no-show}` +
  `GET /terms-changes`, `/modifications`, `/modifications/{accept|decline}`,
  `GET /evidence`, and
  `GET /pet-parents/{petParentId}/bookings/{bookingId}` (single read that
  **issues the start-OTP** while the booking is `START_JOB`). `/accept`, `/decline`,
  `/cancel`, `/no-show` flow through `Booking.UpdateBookingStatus`
  (UPDLOCK+HOLDLOCK), which enforces party + per-actor settable set + from-state
  (+ the 30-minute no-show grace window + the cancel-blocked-once-underway guard).
  `/start-job` (→ `START_JOB` + start-OTP, working-hours gate) via `Booking.StartBooking`
  / `StartNightStayBooking`; `/start-job/verify` (body `{ otpCode }`, →
  `IN_PROGRESS`) via `Booking.VerifyBookingStartOtp` / `VerifyNightStayBookingStartOtp`;
  `/complete` (**provider-only, from `IN_PROGRESS`, no OTP**)
  via `Booking.CompleteBooking` / `CompleteNightStayBooking`.
  The provider's `/no-show` sets `PARENT_NO_SHOW`; the parent's sets
  `PROVIDER_NO_SHOW`. The former **`COMPLETED` evidence gate** (THROW
  51127/51247) was **removed** — evidence photos are optional.
  `/complete` takes an **optional body** `{ nextConsultationDate?:
  "yyyy-MM-dd" }` — the provider can propose the pet's next visit while completing
  the job. Stored in
  `Parent.PetNextConsultations` (one row per pet + provider type, upserted; type
  derived server-side from the booking's category: PetGroomer → `Groomer`, Vet →
  `Vet`, PetTrainer → `Trainer`) and surfaced on pet reads as
  `nextConsultations: [{ type, nextConsultation }]`. Validated BEFORE the
  transition: 400 `NextConsultationNotSupported` (PetSitter/AdoptionSale booking),
  400 `NextConsultationRequiresPet` (Custom walk-in / no linked pet), 400
  `InvalidNextConsultationDate` (past date). Sproc
  `Parent.UpsertPetNextConsultation` (THROW 51221).
  `/complete` also accepts an optional `prescription` block (Vet bookings only —
  see the "Vet prescription" note below).
- **Vet prescription** (`Booking.BookingPrescriptions`, one row per booking,
  upserted): a vet records the visit's prescription — `{ prescriptionText?,
  isPetVaccinated, vaccinations: [...] }` (vaccine names stored as a JSON array in
  a single column; app-owned System.Text.Json (de)serialization). Written **two
  ways**: (1) the optional `prescription` block on `POST .../bookings/{id}/complete`,
  and (2) the dedicated `POST /providers/{providerId}/bookings/{bookingId}/prescription`
  (upsert — lets the vet fill/edit independently of ending the job). Both go through
  `Booking.UpsertBookingPrescription` (Vet-only, provider-only, only from
  `IN_PROGRESS`/`COMPLETED` — the retired `ENDING` tolerated; THROW 51290-51293).
  Surfaced on the single-day
  booking-detail read (both hosts) as the top-level `prescription` section — **null
  until recorded**. The block's `nextConsultationDate` is **NOT** stored on the
  prescription row: it's the pet's rolling Vet follow-up
  (`Parent.PetNextConsultations`, type `Vet`), LEFT-JOINed into `GetBookingDetail`
  by `PetId` — so it reflects the pet's latest Vet next-consult, not necessarily
  this booking's. Night-stay detail does not carry a prescription (PetSitter, not Vet).
- **Start-OTP** (`Booking.BookingStartOtps` / `NightStayBookingStartOtps`,
  telemetry-tracked): the provider's `/start-job` action issues the code; the
  parent shows it and the provider enters it via `/start-job/verify` →
  `IN_PROGRESS`. Completion needs no OTP. 6-digit plaintext share-codes (10-min
  TTL, reuse-while-valid). The parent reads the active code off the GET-one
  detail (`startOtp` block, present only at `START_JOB`). `VerifyBookingStartOtp`
  validates (consume on success, bump `FailedAttemptCount` on mismatch, cancel
  the job on the 6th → `OTP_MAX_ATTEMPTS_EXCEEDED`). The `OtpKind` column from the
  short-lived dual-OTP experiment was dropped.
- **Evidence** (`Booking.BookingEvidence`): provider uploads photo(s) via
  `POST .../evidence` (blob `BlobUploadKind.BookingEvidence`, `booking-evidence/`
  folder, 3 MB, JPEG/PNG/WebP); **optional** — no longer gates `COMPLETED`.
- **Booking payment** (`Booking.BookingPayments`, 2026-07-23): one row per paid
  booking, written when the provider marks a `COMPLETED` booking `PAID`
  (`POST /providers/{id}/bookings/{bookingId}/paid` + night-stay twin, body
  `{ paymentMethod: "Cash" | "Digital" }`). Columns: `{ BookingPaymentId,
  BookingType ('SingleDay'|'NightStay' — discriminates which booking table
  `BookingId` points at), BookingId, ProviderId, PetParentId, Amount, PawfrontFee,
  PaymentMethod, PaidAtUtc }`, UNIQUE `(BookingType, BookingId)` (paid once). It's
  a **financial ledger — NO FK** to the booking tables (payment history survives
  booking deletion), indexed on `ProviderId` for the future per-provider "total
  received" report (`SUM(Amount)` = gross the parent paid; `SUM(Amount) −
  SUM(PawfrontFee)` = provider net). `Amount`/`PawfrontFee` are computed
  server-side by `BookingService.MarkPaidAsync` (and the night-stay twin) from the
  booking's **price-locked total** — it reuses `GetDetailAsync`'s `TotalAmount` /
  `PawfrontFee`, so there's a single pricing source of truth.
  **As of 2026-08-05 the mark-paid sprocs also settle the payout** —
  `PayoutStatus → 'Paid'` (and a `PayoutId` stamped if one is somehow missing).
  The reference itself is minted earlier, at `COMPLETED`; see the "Earnings &
  spend reporting" section for the payout lifecycle and the reporting APIs built
  on it.
  **Snapshots at creation (price / cancellation policy / selected-location address —
  2026-07-24).** Three things are frozen onto the booking row **at creation** so a
  later provider edit never re-prices / re-rules / re-addresses an already-created
  booking (protects even CONFIRMED bookings). All three read paths **prefer the
  snapshot, falling back to live only for legacy rows**:
  1. **Price (price-lock):** the offering's unit rate → `Booking.Bookings.PricePerHour`
     (now populated for App bookings too, not just Custom — the old
     `CK_Bookings_SourceShape` App-`PricePerHour`-must-be-NULL clause was **relaxed**,
     which had made App inserts fail once the offering had a price) /
     `Booking.NightStayBookings.PricePerNight`. Only the unit *rate* is snapshotted;
     the total is always `rate × quantity` (hours / nights), so an accepted
     modification that changes duration/nights still recomputes against the locked
     rate. Fallback: `ResolveAppPricingAsync` / night-stay `pricePerNight ??=`.
  2. **Cancellation policy:** the provider's current
     `Provider.ProviderCancellationPolicies.MinimumHoursBeforeCancellation` →
     `CancellationPolicyHours INT NULL` (both booking tables; CHECK `NULL|24|48|72|96`).
     Resolved **in the create sproc** (no new C# param). The detail read now returns
     `MinimumHoursBeforeCancellation` from this column — the live `IProviderPolicyService`
     read was **removed** from both booking services (constructor dep dropped). A null
     column value legitimately means "no restriction".
  3. **Selected-location address:** the `LocationType`-driven address →
     `Snapshot{AddressLine,City,ZipCode,Latitude,Longitude}` (both booking tables).
     ParentLocation is snapshotted **in the sproc** (from `Parent.PetParents`);
     ProviderLocation is resolved in C# (Cosmos summary + registration coords via
     `BookingService.ResolveProviderAddressSnapshotAsync`) and passed to the sproc as
     `@SnapshotProvider*` params. See the `location` note above.
  Existing rows are **backfilled** once by `DeployAll.sql` (policy from the current
  provider policy; ParentLocation address from `Parent.PetParents`). SQL can't reach
  Cosmos, so **ProviderLocation address text** and **legacy App `PricePerHour`** are
  NOT backfilled — those legacy rows keep live-falling-back on read (a future C# pass
  could freeze them). New bookings snapshot all three at creation. Wire contract is
  unchanged — only the *source* of `location` / `minimumHoursBeforeCancellation` moved
  from live to frozen.
- **Modifications** (`Booking.BookingModifications` is the **staging area** —
  holds ONLY the open proposal, UNIQUE per booking; **editing is limited to date
  + time**, no service-item change): `RequestBookingModification` stages the
  proposed schedule (validated for working hours / closures / duration in the
  Application layer first — groomer duration is checked against the booking's
  *existing* item); `RespondBookingModification` on **accept** re-checks capacity
  race-safely, copies the staged date/time onto the booking ("staging → main"),
  on **decline** leaves the booking untouched — and **either way DELETES the
  staging row** (the proposal lives in staging only while pending). The staged
  proposal is surfaced on every single-booking read (both hosts, single-day +
  night-stay) as `pendingModification` (via `GetPendingBookingModification`),
  null unless a `MODIFICATION_REQUEST_BY_*` is open — so the counterparty can see
  what's proposed before accepting/declining.
- **The modification window closes 2 hours before the service, for EITHER party
  (2026-07-31; widened to the provider side 2026-08-02 — previously parent-only).**
  One derived instant drives two rules: `modificationCutoff = serviceStart − 2h`,
  where `serviceStart` is `BookingDate + StartTime` (single-day) or
  `CheckInDate + DropOffTime` (night-stay), UTC like everything else.
  1. **Neither party can open a proposal at or after the cutoff** → **409
     `ModificationWindowClosed`** (THROW 51151 / night-stay 51271). The app hides
     "Modify Job" from the same instant; it derives the cutoff itself from the
     `bookingDate` + `startTime` already on every booking read — the server does
     not publish a `modificationCutoffUtc` field.
  2. **An open proposal — from either party — that reaches the cutoff unanswered
     expires**: the staging row is discarded (exactly as a decline would) and the
     booking **reverts to `CONFIRMED`** with a `System` audit row. Done by the
     **scheduled external job** (`Booking.RevertExpiredModificationRequests`,
     which captures the row's actual `FromStatus` per booking rather than
     assuming `MODIFICATION_REQUEST_BY_PARENT`, since it now handles both). The
     respond sprocs **reject** a response arriving past the cutoff → **409
     `ModificationRequestExpired`** (THROW 51152 / night-stay 51272), but **do
     not perform the revert** — so between job runs the booking is still parked
     in whichever `MODIFICATION_REQUEST_BY_*` status it was in, with its staging
     row intact, and neither party can move it. Unlike everything else the job
     settles, this one is **not terminal** — the booking stays live.

  Together these mean **no booking should be left sitting in either
  `MODIFICATION_REQUEST_BY_*` status from T−2h onward**, which is the point: that
  status is not in `ConfirmedEquivalent`, so a stale proposal would otherwise
  block `/start-job` and strand the booking. Note this now depends **entirely on
  the external job running** — nothing in the database unsticks it, so the job's
  cadence is the real bound on how long a booking can be stranded. Reverting to
  literal `CONFIRMED` loses nothing — the five
  confirmed-equivalent states behave identically and the prior resting state is
  still in `BookingStatusHistory`.

  Note the two rules share a boundary, so a request landing at 07:59 for a 10:00
  start leaves the counterparty about a minute to answer; there is deliberately
  no minimum lead time.
- **Terms drift on an edit (2026-07-27).** A booking freezes the provider's terms
  at creation (price, cancellation policy, selected-location address; + drop-off /
  pick-up on a stay) and every read prefers the snapshot — that's what stops a
  later provider edit from re-pricing an existing booking. When someone goes to
  **edit** the booking, the drift the snapshot is hiding is surfaced so the app can
  show a "these changed — still want to reschedule?" sheet, and it is adopted only
  if they proceed:
  - **`GET .../bookings/{bookingId}/terms-changes`** (+ night-stay twin, **both
    hosts**) → `{ bookingId, hasChanges, changes: [{ field, changeType,
    bookedValue, currentValue, message }] }`. Always **200**; an undrifted booking
    reports `hasChanges: false` with an empty list (no sheet). `field` ∈ `Price`,
    `CancellationPolicy`, `DropOffTime`, `PickUpTime`, `Location`, `Duration`,
    `MinimumDuration`, `MinimumNights`. `changeType` is **`ValueChanged`** (a frozen
    term now differs from the live one — accepting adopts the new value) or
    **`RuleViolation`** (the booked window no longer satisfies a changed
    duration/min-duration/min-nights rule; there is no old-vs-new value to adopt, so
    the requester must pick a conforming window). Values are display strings —
    the fields span money, hours, clock times, and a postal address. The route is
    scoped to the caller's own booking (404 `BookingNotFound` /
    `NightStayBookingNotFound` otherwise), same posture as `status-history`.
  - **`POST .../modifications`** gained **`acknowledgeTermsChanges`** (bool,
    default false). With drift present and the flag unset the request is rejected
    **409 `BookingTermsChanged`** — a stale sheet can't slip a proposal through.
    **No drift ⇒ nothing changes for existing clients.** With the flag set, the
    Application layer stages the **complete** current term set onto the staging row
    (each field live-when-resolvable, else the booking's own frozen value — so the
    accept-side SQL applies it verbatim).
  - **The drifted values land only on accept.** `RespondBookingModification` copies
    the staged terms onto the booking together with the new schedule; a **decline
    leaves the booking's frozen terms untouched**. The values applied are the ones
    the requester was shown — they are NOT re-read live at accept time, so a second
    provider edit in between can't sneak in. `pendingModification` carries them as
    `acknowledgedTerms` (null in the ordinary case) so the responder sees that
    accepting re-prices/re-rules the booking, not just its schedule.
  - Backed by Application orchestrator `IBookingTermsChangeService`
    (`BookingTermsChangeService`), which composes the same readers the create path
    uses (`IProviderOfferingResolver`, `IProviderPolicyService`,
    `IPetSitterServiceRegistry`, `IProviderDiscoveryService`,
    `IProviderServiceLocationRegistry`) — "current" therefore means exactly what a
    booking made right now would freeze. Every lookup is best-effort: a term whose
    source can't be resolved reports no drift and keeps its frozen value.
  - **Not diffed:** a **Custom walk-in's price** (its `PricePerHour` is the rate the
    provider typed for that one job, not the offering's — adopting the offering rate
    would overwrite what they charged); and any term on a **legacy row that never
    froze one** (null snapshot ⇒ no baseline, and those reads already fall back to
    live). `Location` drift covers both parties' addresses — the parent's profile
    address for a `ParentLocation` booking, the provider's business address for
    `ProviderLocation`; the offering's delivery-location *setting* is deliberately
    not part of the set.
- **Night-stay parity:** all of the above is mirrored for
  `Booking.NightStayBookings` (the multi-night entity) — its own twin tables
  (`NightStayBookingStartOtps` / `NightStayBookingEvidence` /
  `NightStayBookingModifications`) and sprocs, modifications proposing a
  `CheckInDate`/`CheckOutDate` range. The provider host now has a night-stay
  management surface (`/providers/{id}/night-stay-bookings/...`, new file
  `Pawfront.Api/Endpoints/NightStayBookingEndpoints.cs`); the parent host adds
  the parent-side transitions + OTP injection on its existing GET-one.
- **Audit tables** `Booking.BookingStatusHistory` /
  `NightStayBookingStatusHistory` (append-only, `ON DELETE CASCADE`,
  `From/ToStatus NVARCHAR(48)`): one row per transition — seeded on create, and
  written by every transition path (status engine, start, modification
  request/respond, legacy cancel). Read via
  `GET .../bookings/{bookingId}/status-history` (oldest-first; caller-is-a-party
  → 404 otherwise).

### Night-stay (multi-night boarding) bookings — separate from single-day
Multi-night boarding (PetSitter **NightStay** service) is its own booking entity
because a stay is a **check-in / check-out date range**, not a single-day time
window. It lives in `Booking.NightStayBookings` (+ `NightStayBookingStatusHistory`),
**not** `Booking.Bookings`.
- **Date model:** a stay spans `[CheckInDate, CheckOutDate)` — the **checkout day
  is NOT a stayed night** (same semantics as the `GET /providers/search/night-stay`
  search). Capacity is enforced **per night** across the range, race-safe via
  `Booking.CreateNightStayBooking` (`UPDLOCK, HOLDLOCK`, recursive-CTE night walk).
  Max 30 nights (validated in `NightStayBookingService`).
- **Capacity** is the shop-wide `maxPetsAtOneTime` from the NightStay offering;
  `DropOffTime`/`PickUpTime` are snapshotted onto the booking from the offering.
- **Night-stay availability is date-granular (2026-07-11).** The slots
  endpoint, the `/providers/search/night-stay` per-night probe, and the generic
  `/providers` window checker all read per-night occupancy (sproc
  `Booking.GetNightStayOccupancy` via `INightStayOccupancyReader`) against the
  offering's per-night capacity — see the "weekly availability + slot
  computation" section for the `nights` response shape. Fully-booked nights
  report `remainingCapacity: 0` / `isAvailable: false` (previously they showed
  as available and only failed at create time, 51235). Additionally,
  `Booking.GetBookingsForDate` UNIONs active night-stay bookings covering the
  date as full-day `00:00–23:59:59` windows (rows only match a NightStay
  ServiceId) — defense-in-depth for any residual hourly-slot path. The
  in-memory dev fallback mirrors both (`InMemoryBookingStore` +
  `InMemoryNightStayBookingStore` resolve the same singleton).
- **Same 6-state lifecycle + audit trail** as single-day bookings (CREATED →
  CONFIRMED → COMPLETED, APPROVAL_NEEDED, PROVIDER/PARENT_CANCELLED). Cancelled
  rows free their per-night capacity.
- **Endpoints are on the PARENT host only** (the pet parent is who books) —
  ownership-filtered under `/pet-parents/{petParentId}/night-stay-bookings`:
  create / list-mine / get-one / status (actor=Parent) / status-history. The
  shared Application+SQL layer is host-agnostic (`INightStayBookingService`,
  `Booking.ListNightStayBookingsByProvider` + `CancelNightStayBooking` exist for
  a future provider host but aren't wired to endpoints yet).
- **The single-day `POST .../bookings` rejects a NightStay ServiceId** with
  **400 UseNightStayEndpoint** (`BookingNightStayUseDedicatedEndpointException`,
  thrown from both `BookingService.CreateAsync` and `CreateCustomAsync`) — you
  must use the night-stay endpoint with `checkInDate`/`checkOutDate`.

### Earnings & spend reporting (2026-08-05) — payouts, provider earnings, parent spend

**Cash is the only payment method today**, which decides most of the design below:
the parent hands the provider money directly, so there is no transfer leg for
Pawfront to execute — "payout" here means *the provider has been paid*, not
*Pawfront owes them*.

**A payout reference is minted when the job COMPLETES, not when it is paid.**
`Booking.CompleteBooking` / `CompleteNightStayBooking` stamp
`PayoutId` (`PO-000123`) and leave `PayoutStatus = 'Pending'`; the existing
`POST .../bookings/{id}/paid` then flips it to `'Paid'` alongside the
`Booking.BookingPayments` ledger row. The two columns already existed as
capture-only (`Pending|Processing|Paid|Failed`, joined by `NO_PAYOUT` on
2026-08-09) — nothing had ever written them.
That split is the whole point of the earnings screen: **Received** (marked paid)
vs **Awaiting payment** (job done, provider hasn't recorded the cash).
- New **`Booking.PayoutNumberSequence`** mints the number. A SEQUENCE, not a
  per-table IDENTITY, because single-day and night-stay bookings sit in separate
  tables but share **one** payout namespace — exactly as they share the ledger.
  Distinct from `JobNumber` (`PF-000123`): a job that never completes never earns
  a payout reference, so the two drift apart by design.
- **Custom walk-ins are never stamped.** They are off-platform, carry 0%
  commission, and `MarkBookingPaid` rejects them (THROW 51163) — a payout on one
  would sit "awaiting payment" forever and skew the totals.
- The mark-paid sprocs also stamp a `PayoutId` if it is missing, so a booking
  completed before this shipped doesn't end up settled-but-unreferenced.
- `DeployAll.sql` backfills once, idempotently: `PayoutStatus = 'Paid'` on
  already-`PAID` rows (the column was lying about them), and `PayoutId` on
  completed rows via `sp_sequence_get_range` (the documented bulk allocation —
  `NEXT VALUE FOR` isn't usable per-row in a set-based UPDATE).

**`NO_PAYOUT` closes the other end of the lifecycle (2026-08-09).** `Pending` was
the value on every row that never reached `COMPLETED`, so a job that ended as
`PARENT_NO_SHOW` / `PROVIDER_NO_SHOW` / `EXPIRED` sat there reading "the money is
on its way" forever, for an outcome where nobody performed and nobody owes. Both
apps had to override it locally to display "No Payout". Those three outcomes now
write the terminal **`NO_PAYOUT`** instead.
- **Written by the four procedures that write the statuses**, not derived on read
  — the column should be true, and a derived value would have to be re-derived
  identically in every reader (two booking-detail sprocs, two flat reads, the
  earnings list). `UpdateBookingStatus` / `UpdateNightStayBookingStatus` cover a
  **reported** no-show; `SettleUnstartedJobsAsNoShow` and
  `ExpireStaleCreatedBookings` cover the ones the sweep **derives**. A settled
  no-show must look identical whichever way it was settled, so all four write the
  same value. `EXPIRED` needs only the sweep: the status-engine sprocs reject a
  late transition without writing (THROW 51129/51153), so the sweep is its only
  writer.
- **Overwriting is safe in the status engine** because the from-state rules admit
  a no-show only from a confirmed-equivalent status or `START_JOB` — none of
  which can already be `Paid`, since `PAID` is reachable only from `COMPLETED`.
- **Nothing in earnings changes.** `Booking.BookingAmounts` gates on
  `IsEarned` (`COMPLETED` or `PAID`), so these bookings were never in the totals
  or in `GET /providers/{id}/earnings/bookings` to begin with. The fix is about
  the **booking reads** — `paymentDetails.payoutStatus` on both hosts' detail
  endpoints, plus the flat `BookingResponse` / `NightStayBookingResponse`.
- **Cancellations, declines and `OTP_MAX_ATTEMPTS_EXCEEDED` deliberately still
  read `Pending`.** The same "no money moves" argument seems to apply, but a
  cancelled booking has a cancellation policy attached and the refund /
  cancellation-fee leg is not built — so whether money moves on one is genuinely
  undecided, and freezing it as `NO_PAYOUT` now would prejudge it. The three
  states here have no such open question.
- `PayoutStatuses` (`src/Pawfront.Application/Bookings/PayoutStatuses.cs`) names
  the vocabulary for C# and gives the in-memory dev stores something better than
  a hardcoded `"Pending"`; SQL remains the only writer for real rows.

**`Booking.BookingAmounts` (new inline TVF) is the single definition of what a
booking is worth**, and both sides read it — so a provider's "earned" and a
parent's "spent" on the same booking cannot disagree. It unifies the two booking
tables, takes `@ProviderId` **or** `@PetParentId` (the other NULL), and returns
every booking with `Status`, `IsEarned`, `IsPaid`, `IsPrivate`, `Amount`, `Fee`.
- **The ledger wins** whenever a `BookingPayments` row exists — it froze Amount +
  PawfrontFee at payment time, so a later change to `Payments:PawfrontFeePercentage`
  can't rewrite history. Only unpaid bookings are priced from the creation-time
  price-lock, and **that arithmetic mirrors `BookingService.GetDetailAsync` /
  `NightStayBookingService` exactly** — Custom = rate × hours (any category),
  App PetSitter = rate × hours, every other App single-day service = the flat
  snapshot, night-stay = rate × nights. **Change the C# and you must change the
  TVF**, or the earnings screen will disagree with the booking detail.
- A legacy row with no price snapshot yields `Amount` NULL, surfaced as
  `unpricedBookings` rather than silently under-reporting.

**What counts, and when.**
- **Earned / spent = `COMPLETED` or `PAID`.** Completed-but-unpaid is included
  deliberately: the provider did the work and the parent has typically already
  paid in cash — only the provider's "mark paid" tap is outstanding, and the
  parent has no control over it.
- **Bucketed by SERVICE date** — `BookingDate` (single-day) / `CheckOutDate`
  (night-stay), never `PaidAtUtc`. A job done Sunday and marked paid Monday
  belongs to Sunday's week, and an unpaid booking has no payment date at all yet
  still has to land in a period.
- **Periods are calendar-aligned, in UTC** (`EarningsPeriodRange`): Weekly =
  Mon–Sun, Monthly / Quarterly / Yearly = the current calendar period, AllTime =
  unbounded. Whole period, not truncated at today, since a booking can be marked
  COMPLETED ahead of its service date. UTC matches the codebase-wide convention,
  so a Swiss provider's month rolls over at 01:00/02:00 local. **Still open** —
  notifications were switched to Swiss local on 2026-08-06 but these period
  boundaries were not, so a job served late on the last Sunday of a month can land
  in the next month's figures.
- **Events are not earnings.** Ticket proceeds are a separate flow.

**Money is reported three ways** because with cash they differ: **gross** (what
the parent pays — the provider physically holds it), **fee** (the commission they
collected on Pawfront's behalf and owe back), **net** = gross − fee, the headline.
Each splits into `received*` / `awaiting*`. Custom walk-ins are excluded from all
of them and reported as `privateJobCount` / `privateJobAmount`, so the provider
still sees the work without it distorting platform earnings. Net is computed in
C# (`ProviderEarningsTotals.NetAmount`), never in SQL — one subtraction, one place.

**Application layer** lives in `Pawfront.Application/Earnings/`:
`IProviderEarningsService` / `IParentSpendService` (+ their narrow
`I*Store` SQL readers), `EarningsPeriod` + `EarningsPeriodRange`,
`EarningsQueryParsing` (shared sort vocabulary so both hosts accept the same
values), and `ParentBookingStatusFilter`, which expands the friendly
`Completed` / `Upcoming` / `Cancelled` groups into raw lifecycle statuses **in C#**
— the sprocs take a plain CSV, so adding a status means editing `BookingStatuses`
and that one file rather than four stored procedures. Paging is capped at
**20** server-side. In-memory dev fallbacks (`NullProviderEarningsStore` /
`NullParentSpendStore`) report zeros — the in-memory stores hold neither the
ledger nor the payout columns.

**The provider earnings routes are ownership-enforced from the JWT** — the only
routes on that host besides account-delete that are. The rest of the provider host
trusts the route's `providerId`, which is fine for profile-shaped reads but not for
revenue: a provider reading a competitor's takings by guessing a GUID is a
materially different exposure. Mismatch → **403 Forbidden**. The parent routes need
no special handling; they sit on the existing `RequireOwnedPetParent()` group.

The parent endpoints are **additions** — the existing unpaginated
`GET /pet-parents/{id}/bookings` and `/night-stay-bookings` are untouched.

### Reviews & ratings (2026-08-08) — both directions on a finished booking

Two placeholders that had been wired ahead of this feature are now real:
`ProviderPublicProfileResponse.Reviews` (always an empty array) and
`PetParentDetailsResponse.Rating` (always null).

**One table, both directions**, in a new **`[Review]`** schema —
`Review.BookingReviews`, discriminated by `ReviewerType`:

| `ReviewerType` | Author | Subject | Carries |
|---|---|---|---|
| `Parent` | the pet parent | the provider | rating 1–5, optional comment (**1000 chars**), optional photos (max 5) |
| `Provider` | the provider | the pet parent | rating 1–5 **only** |

The provider direction being rating-only is enforced three times over: that host
exposes no comment field, `BookingReviewService` drops one if a direct caller sends
it, and `CK_BookingReviews_ProviderRatingHasNoComment` refuses to store one.

**Reviewable means `COMPLETED` or `PAID`, not `COMPLETED` alone.** PAID sits
downstream of COMPLETED, so gating on COMPLETED would close the review window the
moment the provider recorded payment — which for cash is usually immediate. A
**Custom walk-in can never be reviewed** (no pet-parent record to author or receive
one → 400 `ReviewNotAppBooking`).

**The gate lives in `Review.UpsertBookingReview`**, not the Application layer: it is
a decision about rows the database holds, and putting it in the same transaction as
the write makes it race-safe for free. That sproc is **deliberately ONE procedure
for both booking kinds**, breaking the `MarkBookingPaid`/`MarkNightStayBookingPaid`
twin convention — those twins exist because the flows genuinely differ, whereas here
the only difference is which table supplies the party ids and status.

- The booking read takes **no `UPDLOCK`**, unlike most write paths here. Once a
  booking is reviewable it stays reviewable (`COMPLETED` → `PAID` is the only exit
  and `PAID` is terminal), so a concurrent status change cannot invalidate a review
  being accepted. The lock that does matter is `UPDLOCK + HOLDLOCK` over the unique
  key `(BookingType, BookingId, ReviewerType)`, which turns a concurrent
  double-submit into an update instead of a UNIQUE violation.
- **No FK on `BookingId`** — same posture as `Booking.BookingPayments`: one column
  can't reference two tables.
- **Submit is an upsert.** Resubmitting replaces rating + comment on the same row, so
  a corrected star doesn't duplicate. `CreatedAtUtc` does **not** move on an edit — it
  is the date the review was *given*, which is what the list sorts on; `UpdatedAtUtc`
  moves. The endpoints answer **201** on a first review and **200** on an edit.
- **The review itself is not deletable**, only its photos — so a rating is never
  stranded.

**Photos are a second call after the review exists** (`POST .../review/photos`,
multipart, one file, ≤3 MB, JPEG/PNG/WebP) because the blob path is keyed by the
review's own id: `review-photos/<bookingReviewId>/`. New
`BlobUploadKind.ReviewPhoto`; the folder is a C# default in `BlobStorageOptions`, so
no `appsettings` change. The 5-photo cap is enforced in
`Review.AddBookingReviewPhoto` under `UPDLOCK + HOLDLOCK` (the count is a race) and
pre-checked in the endpoint so a caller at the cap isn't charged an upload first.

**`Review.ListProviderReviews` returns three result sets** — a
**whole-population** summary (count, average, 1–5 histogram), the ordered page, then
that page's photos pre-joined. The summary is deliberately not page-scoped: it is the
profile header's "4.6 (23)" and must not shift as the reader pages. Sorting is
`sortBy=Date|Rating` × `sortDirection=Asc|Desc` with a `BookingReviewId` tie-break
(without it OFFSET paging repeats or skips rows sharing a timestamp or score); a
`Rating` sort uses newest-first as its secondary, which is what a reader scanning
"all the 5s" expects. `take` caps at **20**, the same figure the earnings and spend
lists use.

**Reviewer names are joined LIVE, never denormalised onto the review** — so a parent
who deletes their account reads **"Deleted User"** rather than leaving their real name
frozen in every review they ever wrote. This is the reason the review row stores no
name, and it is the same invariant the booking reads hold.

**Two placeholders filled + one new section:**
- `GET /providers/{providerId}` (parent host) gains `reviewSummary` and fills
  `reviews` with the newest **10** as a *preview*; the full sorted/paged list is the
  dedicated endpoint. `ProviderReviewResponse` (the empty placeholder record) was
  replaced by `ProviderReviewItemResponse`, so one type serves both surfaces.
- `GET /pet-parents/{petParentId}/details` (provider host) now returns a real
  `rating`, plus a new `ratingCount` — "5.0" off one rating and "4.6" off forty are
  very different claims, so the count travels with the average.
- Every booking-detail read (both kinds, both hosts) gains a **`review`** section
  `{ canReview, myReview }`. `canReview` mirrors the SQL gate (COMPLETED-or-PAID and
  an App booking) so the app knows when to offer the prompt; it stays true after a
  review exists, since a review can be edited. **Each host reports its own side
  only** — the parent host never returns the provider's private rating of the parent.
  Same per-host split `startOtp` already uses.
- **The parent's two "my bookings" LISTS carry a slimmer `review` block
  (2026-08-09):** `{ canReview, rating }`, added to
  `ParentServiceBookingCardResponse` and `ParentNightStayBookingCardResponse`.
  **`canReview` deliberately means something different here** — a list asks "is
  anything outstanding", so it goes **false once the booking has been reviewed**,
  where the detail's stays true (a review is editable, and the detail is where you
  edit it). The two fields read as a pair: `true/null` prompt, `false/4` already
  rated, `false/null` not reviewable. Only the score travels; comment and photos
  stay on the detail. Backed by `Review.ListPetParentBookingReviews` — **one read
  per page**, not per card, which is why it is scoped to the parent rather than
  taking booking ids, plus the filtered index
  `IX_BookingReviews_PetParent_Authored` (the existing `IX_BookingReviews_PetParent`
  is filtered to the *provider* direction and cannot serve it). Resolved inside
  `ParentBookingEnrichmentService` alongside the provider/price enrichment, and
  best-effort like the rest of it: a failed read leaves every card `false/null`
  rather than failing the list.

**The two rating-submit routes on the provider host resolve the caller's ProviderId
from the JWT** and 403 on a mismatch, joining earnings and account-delete as the
exceptions to that host's "trust the route id" posture. It matters more here than for
a read: the sproc's party check compares the actor against the booking's `ProviderId`,
so trusting the route would let anyone knowing a provider id and one of their booking
ids write a rating **as** that provider. The LIST route is left unchecked — a
provider's received reviews are public data, served unfiltered on the parent host too.

**In-memory dev fallback is functional** (dict-backed `InMemoryBookingReviewStore`),
not a zeros-reporting Null store like `NullProviderEarningsStore` — this is a write
flow, so a store that accepted a submit and never showed it again would make the
feature untestable without SQL. It cannot enforce the eligibility gate (it can't see
the booking tables), same limitation the other in-memory stores carry for capacity
and closures.

**Deliberately not built:** **event reviews** (deferred — `Event.EventBookings`
identifies its booker only by free-text `BookerEmail` with no FK, so attendee
eligibility needs a persisted booker id first); a rating on the discovery card or the
five search cards; and a `REVIEW_RECEIVED` push (no such type in the Notifications V3
spec).

### Per-service catalog (`Provider.ProviderServices`)
A provider's offering can expose more than one bookable service (e.g.
PetSitter's DayCare AND NightStay). Closures, bookings, and slot queries
all reference a specific **ServiceId**, not the whole provider.

- Table `Provider.ProviderServices`: `(ServiceId GUID PK, ProviderId,
  ServiceCategory, SubCategory, ServiceType, IsActive, CreatedAtUtc, UpdatedAtUtc)`,
  UNIQUE(`ProviderId`, `ServiceType`). `ServiceType` ∈ `DayCare`, `NightStay`,
  `GroomingSession`, `TrainingSession`, `VetAppointment` (PetAdoptionAndSale has
  no offering and therefore no row here).
- Rows are upserted automatically when an offering POST runs — the endpoint
  handler calls `IProviderServiceCatalog.UpsertAsync` for each sub-offering
  present and `DeactivateAsync` for any that were removed (rows are soft-
  deactivated, not deleted, so historical closures/bookings remain valid).
- `GET /providers/{providerId}/services` returns the active catalog (with
  `?includeInactive=true` to see deactivated rows). Replaces the legacy
  in-memory `POST/GET /providers/{providerId}/services` placeholders, which
  were removed.

### Provider closures (sick leave / vacation) — **per-service**
- `POST /providers/{id}/closures` — body: `{ serviceIds: [GUID, ...], startDate, endDate, startTime?, endTime?, reason? }`.
  - `serviceIds` is required and non-empty. The server validates every id
    belongs to the provider and is active, then creates **one closure row
    per service id in a single transaction** (all-or-nothing).
  - Full-day across the range when no times given. Partial-day (`startTime`+`endTime` set) requires `startDate == endDate`.
  - Always returns 200 + envelope `success=true`. The response payload is **discriminated**:
    - `status: "Created"` → `closures` is populated (one entry per requested ServiceId).
    - `status: "BookingsExist"` → no closures were created; `conflictingBookings` lists confirmed bookings inside the window for any of the targeted services (each carries `serviceId`) plus a `warningMessage`. Provider must move/cancel them and retry. **There is no `force` override**.
  - SQL sproc `Provider.CreateClosures` (plural; the legacy `CreateClosure` is dropped on re-deploy) holds `UPDLOCK, HOLDLOCK` on the conflict-detect query so concurrent `Booking.CreateBooking` on any of the targeted services serialises behind it (race-safe).
- `GET /providers/{id}/closures?serviceId=&from=&to=` — list closures whose date range intersects `[from, to]`. `serviceId` narrows to a single service.
- `DELETE /providers/{id}/closures/{closureId}` — reopen one closure row.
- Slot service consults closures **scoped by ServiceId**: a DayCare closure does not affect NightStay slots/bookings.
- Booking service consults closures **scoped by ServiceId**: overlap → `ProviderClosedOnDateException` mapped to **409 ServiceClosed**.
- Table: `Provider.ProviderClosures` (`ClosureId, ProviderId, ServiceId (FK → ProviderServices), StartDate, EndDate, StartTime?, EndTime?, Reason?, CreatedAtUtc`). CHECKs enforce `EndDate >= StartDate`, both times together-or-neither, and partial-day windows require `StartDate = EndDate`.

### Onboarding status orchestrator
- `GET /providers/{id}/onboarding-status` — single endpoint that returns:
  `basicInfo`, `serviceSelection`, `selectedServiceDetails` (one entry per
  registered category, checks both license + offering in Cosmos),
  `payoutAndCancellation`, `verification` (email + mobile), and
  `isFullyOnboarded` roll-up.
- Backed by `Provider.GetProviderOnboardingStatus` sproc (4 result sets in
  one round-trip) + a fan-out of point reads to each registered category's
  Cosmos registry.

### Events (provider- OR parent-created)
- `Event.Events` carries **nullable `ProviderId` AND nullable `PetParentId`**
  with a CHECK enforcing exactly one is set. Provider-organised and
  parent-organised events live in the same table; booking, counter, and
  catalog/detail flows are organiser-agnostic (keyed by EventId).
- Provider create: `POST /providers/{id}/events` (sproc `Event.CreateEvent`,
  THROW 51030). Parent create: `POST /pet-parents/{petParentId}/events`
  (sproc `Event.CreatePetParentEvent`, THROW 51207) on the pet-parent host.
  Identical request body + Cosmos physical-extension flow; only the organiser
  column differs.
- `POST /providers/{id}/events/banner-image` and
  `POST /pet-parents/{petParentId}/events/banner-image` (multipart) → URL.
- Both create-event request bodies carry top-level ticketing
  (`isPaid`, `price?` — applies to ALL event types, online included) plus
  `physical: { maximumCapacity, location }` for physical events. `price` is
  required when `isPaid` is true (>= 0); ignored/null otherwise. Top-level
  `isPaid` and `price` are returned on every event read regardless of
  `eventType`. `physical.location` (the venue address —
  `houseNumber`/`street`/`city`/`zip`/`country` required, `latitude`/
  `longitude` optional) is **required for physical events** (→ `400
  InvalidRequest` if missing) and is null/absent for online events.
- Both create-event bodies also carry a top-level **`cancellationPolicy`**
  (refund policy) — **optional for every event type** (it doesn't apply to free
  events, so it's never required; `null`/omitted stores NULL). When supplied it
  must be one of `FullRefundUpTo4Hours` / `FullRefundUpTo2Hours` / `NoRefund`
  (→ `400 InvalidRequest` otherwise). Stored as `Event.Events.CancellationPolicy
  NVARCHAR(32) NULL` and returned (nullable) on every event read. (The refund
  *execution* flow isn't built yet — this just captures the advertised policy.)
  Validated via `NormalizeOptional` in `EventService.ValidateForCreate`.
- Both create/edit bodies also carry a top-level **`eventLink`** (the joining
  URL for **online** events). It's **optional** (capture-and-return, not
  enforced) and applies **only to online events** — for physical events any
  submitted link is dropped (stored null), since they carry a venue location
  instead. Stored on the SQL `Event.Events.EventLink` column (`NVARCHAR(1000)
  NULL`) and **returned on every event read** (list + detail, both hosts) as
  `eventLink`. To make it required-for-online, throw in
  `EventService.ValidateForCreate` when online + blank.
- `EventResponse` exposes both `providerId` and `petParentId` as nullable —
  exactly one is populated.
- `GET /providers/{id}/events` — list this provider's events (now hydrates
  Cosmos physical details per event, same as the catalog list). Parent mirror:
  `GET /pet-parents/{petParentId}/events` (sproc `Event.ListEventsByPetParent`,
  ownership-filtered).
- `GET /events/{eventId}` — single event detail. SQL + Cosmos if physical.
- **Edit event (full replace):** `PUT /providers/{id}/events/{eventId}` (sproc
  `Event.UpdateEvent`, THROW 51216) and `PUT /pet-parents/{petParentId}/events/{eventId}`
  (sproc `Event.UpdatePetParentEvent`, THROW 51217, ownership-filtered). Body
  is identical to the create body — **every field editable** (category,
  child-friendly, title/description, banner, amenities, eventType, dates/times,
  ticketing, cancellationPolicy, physical capacity + location). Same validation
  as create. The service reconciles the Cosmos physical doc: upsert when the
  edited event is physical, delete the old doc when it becomes online OR its
  category (the Cosmos partition key) changes. Editing a paid event to free
  drops its payout methods. Returns the full detail shape (incl. payment
  options + attendees). 404 `EventNotFound` when not found / not owned; 400
  `InvalidRequest` on validation. Payout methods themselves are still set via
  the dedicated `POST /events/{eventId}/payout-methods` endpoint, not the edit.
- **Partial edit (PATCH):** `PATCH /providers/{id}/events/{eventId}` and
  `PATCH /pet-parents/{petParentId}/events/{eventId}` (ownership-filtered).
  Same fields as the PUT body but **every field is wrapped in
  `Optional<T>`** (`Pawfront.Contracts.Common.Optional<T>` + its STJ
  `OptionalJsonConverterFactory`), so the server distinguishes **omitted**
  (leave unchanged) from **explicitly null** (clear — e.g. `bannerImageUrl:
  null` removes the banner). **No new service/SQL** — the endpoint reads the
  current event (`GetAsync`), overlays only the supplied fields via
  `Optional.Or(current)`, then calls the SAME `UpdateAsync` /
  `UpdateByParentAsync` full-replace path, so all cross-field rules are
  re-validated against the merged result (flipping `eventType`→Physical still
  requires a `physical` block; flipping `isPaid`→true still requires a
  `price`; online↔physical Cosmos-doc reconciliation is unchanged). The merge
  happens in the endpoint layer (Application never sees `Optional<T>`,
  preserving the Domain←Application←Contracts layering). Same 404
  `EventNotFound` / 400 `InvalidRequest` map as PUT. Note: read-merge-write is
  not transactional (same non-atomic Cosmos reconciliation as PUT).
- **Booking stats on every event read** (list + detail, both hosts): a
  top-level `bookings: { maxBookings, totalBookings }` block. `maxBookings` =
  the physical venue capacity (null/unlimited for online events);
  `totalBookings` = tickets booked so far (non-cancelled, `Status='Confirmed'`)
  — a `TotalBookings` correlated-SUM column appended to result set 1 of all six
  event-returning sprocs (so `EventSqlSnapshot` carries it uniformly).
- **`isBookable` flag on the event catalog list + detail** (`GET /events` and
  `GET /events/{eventId}`, both hosts): `true` when the caller may still book
  tickets, `false` once the caller **already holds non-cancelled tickets** for
  that event (a cancelled booking frees the seat → bookable again). The caller
  is matched by their Firebase **email claim** (booker identity is free text —
  same model as the cancel/"my bookings" flows); the `List`/`GetById` handlers
  fetch the caller's booked-event-id set once via
  `IEventBookingService.ListBookedEventIdsByBookerEmailAsync` (sproc
  `Event.ListBookedEventIdsByBookerEmail` — `DISTINCT EventId WHERE
  Status='Confirmed'`) and membership-test each event. No email claim → empty
  set → everything bookable. `EventResponse.IsBookable` **defaults to `true`**
  on organiser-context reads (create / edit / `GET .../events` own-events
  lists), which aren't booking surfaces.
- **Event detail extras** (`GET /events/{eventId}` + the edit response, both
  hosts; null on list reads): `paymentOptions: ["Cash"|"Digital", ...]` (the
  event's payout methods) and `attendees: [{ attendeeName, ticketNumber }]`
  (non-cancelled bookings, **names only** — booker contact / payment stays on
  the organiser-only dashboard). Surfaced via two extra `GetEvent` /
  `UpdateEvent` result sets (RS3 payout methods, RS4 attendee names).
- Organiser dashboard (attendees / metrics) is **provider-only** — those
  sprocs filter by ProviderId, so parent events simply never match. Not
  exposed on the parent host. (The full-PII attendee list lives here; the
  public detail exposes names only.)
- Storage split: SQL has bulk + amenities junction + ticketing
  (`IsPaid`/`Price`) + `CancellationPolicy` + `EventLink` (online joining URL);
  Cosmos has physical capacity + venue location. Online events have only the
  SQL row (their `EventLink` is the online analog of the physical venue).
- Categories (8): `AdoptionAndRescue`, `PetTraining`, `Charity`,
  `Volunteering`, `HealthAndWellness`, `SocialAndCultural`,
  `OutdoorActivities`, `ParentEducation`.
- Amenities (8): `FreeParking`, `PaidParking`, `Restrooms`, `DrinkingWater`,
  `FoodAndBeverage`, `SeatingAreas`, `FirstAidBooth`, `None`. `None` can't
  coexist with others (enforced in validation). **Amenities are mandatory for
  Physical events** (at least one; use `None` to mean "no amenities") but
  **optional for Online events** (online has no venue) → `400 InvalidRequest`
  if a physical event is created with an empty amenities list.

### Event ticket bookings
Anyone with a Firebase login can buy tickets for an event (physical OR
online). **No FK to PetParents** — attendee names are free text and never
validated against any user table. Bookings are **per ticket**: 4 attendees
→ 4 child ticket rows under one parent booking.

**Ticket-count rule by event type:**
- **Physical** — any number of tickets per booking (≥1), gated against the
  venue `maximumCapacity`.
- **Online** — exactly **one** ticket per booking (the ticket is just the
  signed-in attendee's seat). More than one attendee name →
  **400 OnlineEventSingleTicket**. Online events have no `maximumCapacity`,
  so there is no sold-out limit — capacity is passed as NULL and the sproc
  skips its capacity check.

- `POST /events/{eventId}/bookings` — body:
  `{ bookerName, bookerEmail, bookerMobile?, attendeeNames: [...], paymentMethod }`.
  `paymentMethod` is one of `CreditCard`, `Twint`, `Cash`, or `Free`
  (validated in `EventBookingService` + the `CK_EventBookings_PaymentMethod`
  CHECK; same set on both hosts). Returns the booking + ticket rows; booking
  is created in `PaymentStatus = Pending`.
- **An event's organiser cannot book their own event.** Both hosts resolve the
  caller's own organiser id from the JWT (provider host → ProviderId via
  `IProviderOnboardingService.ResolveProviderByFirebaseUidAsync`; parent host →
  PetParentId via `ICurrentPetParentContext`) and pass it on
  `CreateEventBookingCommand.BookerOrganiserId`. The shared
  `EventBookingService.CreateAsync` rejects when that id matches the event's
  `ProviderId` **or** `PetParentId` (GUIDs are globally unique, so the check is
  host-agnostic) → **403 SelfBookingNotAllowed**
  (`EventBookingSelfBookingNotAllowedException`). The id is taken from the JWT,
  never the body; a caller with no organiser profile resolves to null and is
  never blocked.
- `GET /event-bookings/{bookingId}` — booking + one entry per ticket
  (denormalised on the wire — if 4 tickets were bought, 4 entries appear in
  the `tickets` array).
- `POST /event-bookings/{bookingId}/payment-confirmation` — external
  gateway callback. Body: `{ paymentStatus, paymentReference? }` where
  `paymentStatus` is `Paid` or `Failed`. Idempotent for redelivery of the
  same (status, reference) pair; throws **409 PaymentAlreadyConfirmed** if
  the booking was already finalised with a different result.
- Storage: SQL only. `Event.EventBookings` (one row per transaction) +
  `Event.EventBookingTickets` (one row per attendee/ticket). **No Cosmos
  doc is written** — the event Cosmos doc is read for `maximumCapacity`
  (pricing `isPaid`/`price` comes from the SQL `Event.Events` row) but never
  mutated, so per-event ETag contention is avoided.
- Capacity is enforced (physical events only) inside `Event.CreateEventBooking`
  by SUMming `TicketCount` over confirmed rows for the event under `UPDLOCK +
  HOLDLOCK`, then rejecting when the requested ticket count would push the
  total past `@MaximumCapacity`. Concurrent buyers serialise and the
  (N+1)-th seat is rejected once the event is full. Maps to **409
  EventSoldOut** (`51091`). `@MaximumCapacity` is **NULL for online events**,
  which skips this check entirely.
- `TotalAmount` is a snapshot of `price × ticketCount` at booking time.
  For free events (`IsPaid = false`) it is 0.
- A physical event whose Cosmos capacity doc is missing can't be priced for
  capacity and returns **400 EventNotBookable** (`EventBookingNotPhysicalException`).
- **Cancel a booking (soft-cancel):** `DELETE /event-bookings/{bookingId}` on
  **both hosts**. Authorisation is by the caller's Firebase **email claim** —
  there's no FK to any user table, so the booker (parent OR provider, whoever
  bought the tickets) is matched against the booking's free-text `BookerEmail`.
  Flips `Status → Cancelled` + stamps `CancelledAtUtc`; the seat capacity is
  **released automatically** because `CreateEventBooking` only SUMs
  `Status='Confirmed'` rows. Backed by `Event.CancelEventBooking` (UPDLOCK +
  HOLDLOCK on the row read, serialises against double-cancel + the create-side
  capacity check). Returns the cancelled booking with its tickets (same shape
  as GET). 403 `EmailClaimMissing` (token has no email claim); 404
  `EventBookingNotFound` (unknown id OR not the caller's — indistinguishable, no
  existence leak, THROW 51218); 409 `EventBookingAlreadyCancelled` (THROW 51219).
- **Refund** of a Paid booking is **not** built yet — the cancel just frees the
  seat + flips status; the external refund leg is still TODO.

### Event organiser dashboard (metrics + attendees)
Two **organiser-only** GETs that return the data the event creator needs
to monitor an event. Both URL-scope under `/providers/{providerId}` and
verify the event in the path belongs to that provider — a mismatch
returns **404 EventNotFound** (we don't leak existence).

- `GET /providers/{providerId}/events/{eventId}/attendees` — returns one
  row per ticket. Excludes Cancelled bookings; surfaces `paymentStatus`
  per row so the organiser can see Pending/Paid/Failed.
- `GET /providers/{providerId}/events/{eventId}/metrics` — returns
  `{ views, shares, inquiries, confirmedAttendees, earnings }`.
  `confirmedAttendees` = `SUM(TicketCount)` over confirmed Paid bookings;
  `earnings` = `SUM(TotalAmount)` over confirmed Paid bookings.

The three engagement counters (`views`, `shares`, `inquiries`) are simple
integers on `Event.Events`. They're bumped via **public** increment
endpoints (open to any signed-in Firebase user, not just the organiser):

- `POST /events/{eventId}/views`
- `POST /events/{eventId}/shares`
- `POST /events/{eventId}/inquiries`

Each returns the updated `{ viewCount, shareCount, inquiryCount }` so the
mobile client can update its UI without a follow-up read. Backed by
`Event.IncrementEventCounter` (atomic single-column `UPDATE`).

The same three counters are also surfaced **read-only** on every event read
(detail + list, both hosts) under a `pawPrints` object on `EventResponse`:
`{ pawPrints: { viewCount, shareCount, inquiryCount } }`. They're appended to
result set 1 of all six event-returning sprocs (`GetEvent`, `CreateEvent`,
`CreatePetParentEvent`, `ListEvents`, `ListEventsByProvider`,
`ListEventsByPetParent`) so the shared `EventSqlSnapshot` / `ReadEventRow`
carries them uniformly. A freshly created event reads `0/0/0`.

### Event organiser block
Every event read (detail + list, both hosts) carries an **`organizer`** object
on `EventResponse`: `{ organizer: { type, id, name, imageUrl } }`. `type` is
`Provider` or `PetParent` (derived from whichever of `ProviderId` /
`PetParentId` is set — exactly one); `id` is the matching organiser id; `name`
is the organiser's display name (`FirstName + ' ' + LastName`); `imageUrl` is
their profile photo — populated for **pet-parent** organisers (`ProfilePhotoUrl`),
**null for providers** (the `Provider.Providers` row has no profile-photo
column; a provider's business name/photo live in Cosmos and aren't joined by
these SQL-only sprocs). `OrganizerName` / `OrganizerImageUrl` are `LEFT JOIN`ed
from `Provider.Providers` / `Parent.PetParents` and appended to result set 1 of
all six event-returning sprocs, so `EventSqlSnapshot` / `ReadEventRow` carry
them uniformly; `EventService.BuildOrganizer` resolves the `type` + `id`. The
in-memory dev fallback leaves `name` / `imageUrl` null.

**Inquiries are currently a counter only.** If a richer "inquiry has
content (text, contact info, reply thread)" model is needed later, it
would add an `Event.EventInquiries` table; the counter on `Event.Events`
would then become a denormalised cache (or be replaced by a JOIN).

### Push notifications — transactional outbox + FCM (2026-08-02, triggers wired 2026-08-04)

**Engine built; every booking trigger in the Notifications V3 spec is wired.** The
only unwired types are the four whose product modules don't exist here at all —
messaging, invoicing, disputes, promotional — which carry copy + routes + payload
contract but nothing enqueues them. See `docs/notifications.md` for the
mobile-facing payload contract.

**Booking notifications are enqueued in T-SQL, not C#.** The transition sprocs
(`UpdateBookingStatus`, `StartBooking`, `VerifyBookingStartOtp`, `CompleteBooking`,
`MarkBookingPaid`, `Request/RespondBookingModification` + night-stay mirrors) each
`EXEC Notification.EnqueueBookingNotification` before returning their booking row.
Three reasons this beat calling from the endpoint handlers: it's atomic with the
status flip, both hosts get it from one place (no duplicated call sites), and the
sproc already has `@Actor` — so the "notify the OTHER party" relevance rule is
decided where the data is. The C# `IBookingNotificationService` remains ONLY for
booking CREATE, where the sproc is shared by both hosts and a provider creating
their own booking must not be notified.

**`Notification.EnqueueBookingNotification` is the single place the booking `data`
object is built** (canonical ids + template params, ~23 call sites). It
deliberately does NOT build `serviceName`: the 18 grooming display names live in
the C# `GroomingServiceCatalog`, so it emits raw `ServiceType` + `ServiceItemCode`
and `NotificationRenderer` names it at render time — one naming rule for all
producers instead of a T-SQL copy that drifts.

**`EnqueueNotification` gained `@SuppressResultSet`** (default 0). A result set
from a nested `EXEC` propagates to the client, so without it every transition
sproc would return a phantom result set after its booking row and break the C#
readers.

```
BookingService / EventBookingService / sweep sprocs
        │  INotificationPublisher (or EXEC Notification.EnqueueNotification)
        ▼  ── same transaction as the domain change ──
 Notification.NotificationOutbox ──► NotificationDispatchFunction (timer, 1 min)
                                          │ renders copy, reads tokens, sends
                                          ▼
                                  FirebaseAdmin → FCM HTTP v1
                                          │ UNREGISTERED/INVALID → IsActive = 0
```

**Why an outbox rather than sending inline.** Booking statuses are changed by
**three** processes — the provider API, the parent API, and the
`Pawfront.Functions` sweep. `EXPIRED`, the auto-settled no-shows and the
modification revert happen ONLY in the sweep, which has no request context and
is pure T-SQL, so an inline send could never cover them. The outbox also keeps
an external HTTP call off the request path and gives durable retry. Cost: up to
~1 minute of dispatch latency.

**One row is BOTH the delivery job and the in-app inbox entry** — a notification
is stored exactly once. `Title`/`Body`/`Route` are **NULL at enqueue time** and
rendered by the dispatcher from `NotificationType` + `DataJson`, so all
user-facing copy lives in ONE C# file
([`NotificationTemplateCatalog`](src/Pawfront.Application/Notifications/NotificationTemplateCatalog.cs))
and a T-SQL sweep can enqueue nothing but a type + parameters yet produce the
same wording as the API hosts. The inbox lists only rendered rows
(`Title IS NOT NULL`).

- **Table `Notification.NotificationOutbox`** — `Audience` (`Provider` |
  `PetParent`) + `RecipientId` (polymorphic: ProviderId or PetParentId).
  **No FK** to Providers/PetParents, same posture as `Booking.BookingPayments`:
  an anonymised account keeps its notification history. `Status` is
  `Pending → Sending → Sent | NoDevice | Failed`; **`NoDevice` is a success** —
  the notification is real and belongs in the inbox, the recipient just has no
  active token. Filtered UNIQUE `DedupeKey` (e.g. `BOOKING_ACCEPTED:<bookingId>`)
  makes enqueue idempotent, which matters because the sweeps run every 5 minutes
  and the inbox makes duplicates user-visible.
- **Claim is lease-based.** `Notification.ClaimPendingNotifications` flips rows to
  `Sending` and pushes `NextAttemptAtUtc` forward; a dispatcher that dies
  mid-batch releases its rows automatically when the lease lapses. It returns
  **two result sets** — the claimed rows, and their recipients' active FCM tokens
  pre-joined — so dispatch never issues an N+1 token lookup.
  `CompleteNotificationDelivery` writes the rendered copy + outcome via a TVP and
  reschedules failures with exponential backoff (2/4/8/16/32 min, capped at 60)
  until `MaxAttempts`.
- **Two Firebase projects, two credentials.** The provider app is
  `littersoftprovider`, the parent app `pawfrontparent-89296`. FCM HTTP v1 is
  per-project — a token from one is rejected by the other with
  `SENDER_ID_MISMATCH` — so `Audience` selects both the token table and the
  credential. `FirebaseAppRegistry` caches one `FirebaseApp` per audience.
  Credentials come from a gitignored file (dev) or `IPawfrontSecretProvider`
  (Key Vault, prod); **never** from `appsettings.json`.
- **The two CRUD hosts reference Firebase nowhere** — `Pawfront.Api` and
  `Pawfront.PetParentApi` only ever write an outbox row, and
  `Pawfront.Functions` owns delivery. That falls out of the outbox design and is
  worth preserving.
  **`Pawfront.ChatApi` is the deliberate exception** (2026-08-09): it references
  `Pawfront.Infrastructure.Firebase` and sends its own pushes. The outbox exists
  to keep an outbound HTTP call off the *booking* request path, where a minute of
  latency costs nothing and durability is everything; for chat the latency IS the
  feature. It still writes the same outbox row — pre-claimed — so the row remains
  the record and the retry backstop. See the chat section below.
- **Read and write sides live in different projects.** The API hosts get
  `SqlNotificationPublisher` (write-only) from `Pawfront.Infrastructure.Sql`; the
  dispatcher's `SqlNotificationOutboxStore` (claim/complete/prune) lives in
  `Pawfront.Functions/Notifications/` and talks to SQL directly, exactly as the
  three booking sweeps do. `Pawfront.Functions` therefore does **not** reference
  `Pawfront.Infrastructure.Sql` at all.
- **Hybrid `notification` + `data` payload.** The `notification` block makes the
  OS display it even when the app is killed; `data` carries the routing contract
  (`v`, `type`, `route`, `entityType`, `entityId`, `notificationId`, `sentAtUtc`
  + template params). **Every FCM data value must be a string** — there is no
  nested-object support — hence the flat shape.
- **Android specifics that silently break delivery:** `channel_id` must match a
  channel the app already created (Android 8+ *drops* notifications naming an
  unknown channel), and the icon is a **drawable resource name bundled in the
  app**, not a URL. A rich `ImageUrl` *is* a URL, but the OS fetches it
  anonymously — a bare private-container blob URL will not render.
- **Dead-token hygiene:** only `UNREGISTERED` / `INVALID_ARGUMENT` /
  `SENDER_ID_MISMATCH` deactivate a token. Transient codes (`UNAVAILABLE`,
  `INTERNAL`, quota) deliberately do **not** — deactivating on those would
  permanently silence a live device.
- **Routes in
  [`NotificationRoutes`](src/Pawfront.Application/Notifications/NotificationRoutes.cs)
  are PROVISIONAL placeholders** awaiting the mobile team's route table. They are
  isolated in that one file so adopting the real ones is a single-file change.
- In-memory dev fallback is `NullNotificationPublisher` (logs and drops) — a
  developer on the in-memory store gets no notifications, same posture as the
  booking sweeps.

**The `data` object (2026-08-04).** Every notification carries a canonical id
block — `category` (`BOOKING`/`EVENT`/`MESSAGING`/`PROMOTIONAL`), `bookingId`,
`eventId`, `parentId`, `providerId`, `petId`, `isNightStay`, `payoutId` — filled by
`NotificationPayloadBuilder.ApplyCanonicalFields`. **A field that doesn't apply is
sent as an EMPTY STRING, not omitted:** a client can't tell "this type has no pet"
from "the server forgot to set it", so omission would push that ambiguity into
every tap handler. `isNightStay` exists because the two booking tables share no id
space, so `bookingId` alone can't say which detail screen to open. `category` is
written from the TEMPLATE, never the row, so it can't disagree with `type`.

**Copy varies by audience; the wire `type` does not.** Mirrored cards (reminders,
no-shows, modification expiry) are ONE event rendered per app —
`NotificationTemplateCatalog.AudienceOverrides` keyed by `(type, audience)`, with
fallback to the shared entry. Duplicating the type would force the apps to handle
two keys for one thing and let the halves drift.

**Timer-driven notifications live in `Booking.SendBookingReminders`**, run by the
new **`BookingReminderFunction` every 1 minute** — separate from the 5-minute
`BookingSweepFunction` because it changes NO status (pure enqueue, no lifecycle
risk) and because `BOOKING_REMINDER_STARTING_SOON` would otherwise land anywhere
from 0–5 minutes out. **Re-firing is prevented by the outbox's filtered UNIQUE
`DedupeKey`, not by a flag on the booking** — the predicates are "is it now past
X", which stays true every tick; a missed tick is self-healing, a duplicate is a
no-op, and there's no second copy of "already sent" to keep in step.

**`BookingStartOtps.SeenAtUtc` / `NightStayBookingStartOtps.SeenAtUtc`
(2026-08-04)** — stamped by `Issue*StartOtp` (the sproc that returns the code to
the parent), `COALESCE`d so re-opening the screen doesn't reset it. It exists only
to separate two nudges the V3 spec words differently: `BOOKING_START_OTP_NOT_SEEN`
("you're late, open your code") vs `BOOKING_START_OTP_NOT_SHARED` ("you have it —
share it or be marked a no-show").

**Two no-show types on purpose:** `BOOKING_NO_SHOW_REPORTED` (a party tapped it →
counterparty ONLY, they already saw the result) vs `BOOKING_NO_SHOW_AUTO_SETTLED`
(BR-38 derived it → BOTH, nobody tapped anything). Both carry `absentParty`, which
is what lets one template read correctly in either direction and on either app.
Likewise **two modification-expiry types**, one per deadline arm — see BR-30 below.

**The original trigger — "New Service Booking" (`BOOKING_REQUESTED` /
`NIGHT_STAY_BOOKING_REQUESTED`).** Fires to the **provider** when a parent creates
a booking, from the **parent host's two create handlers only**
(`PetParentEndpoints.CreateServiceBooking`, `NightStayBookingEndpoints.CreateBooking`).
Composed by `IBookingNotificationService` (`BookingNotificationService`), which is
deliberately **not** called from `BookingService.CreateAsync` — that method is
shared by both hosts, and a provider creating a booking on their own host must not
be notified about their own action. Custom walk-ins have no parent and are
excluded for free.
- Body (**rewritten 2026-08-10**, both kinds share one sentence):
  `{parentName} has requested {serviceName} for {petName} on {serviceDateWithYear}.
  Respond within {respondWithin}.` → *"John has requested Bath & Dry for Bruno on
  5 Aug 2026. Respond within 24 hours."*
- **The body now quotes the LENGTH of the window, not the deadline instant** —
  `respondWithin`, derived by the renderer as `acceptByUtc − createdAtUtc`. That
  is the number the provider acts on, and it is **not always 24 hours**: it reads
  "24 hours" when BR-17 governs and the real, shorter figure ("2 hours 15
  minutes") once BR-53 has. The deadline itself still travels as `acceptByUtc`
  (+ the rendered `acceptBy`), which is what an exact live countdown must use.
- **Measured from CREATION, not from send time.** Deliberate: the same window
  rendered against `now` would read "23 hours 59 minutes" in the ordinary case,
  which looks like a bug. The push leaves within ~1 min of the booking, so the
  difference isn't actionable. The cost is that a delivery retried under backoff
  (up to 60 min) quotes a window that has partly run down — acceptable, and
  `acceptByUtc` is exact either way.
- **`acceptBy` is NOT just "created + 24h".** `BookingAcceptanceDeadline.Compute`
  returns the EARLIER of **BR-17** (`CreatedAtUtc + 24h`) and **BR-53**
  (`serviceStart − BookingLeadTime.Minimum`), because either can expire the
  booking first. A booking made at 09:45 for a 14:00 service the same day must be
  accepted by **12:00 that day** — quoting created+24h there would promise the
  provider a deadline hours *after* the service, contradicting what
  `Booking.ExpireStaleCreatedBookings` actually does. `PendingWindow` mirrors that
  sproc's `@PendingHours` default — change one, change the other.
  Note this makes the copy depart from the letter of the V3 spec in a narrow band:
  a booking made 24–26 h before its service gets its true window (say "23 hours"),
  where the spec's "more than 24 hours out ⇒ 24 hours" would over-promise.
- **`serviceName` is shown in the body again** (it was dropped 2026-08-02 in
  favour of the accept-by sentence). It comes from `BookingServiceLabel` — the
  grooming menu item's display name when the booking has a `ServiceItemCode`, else
  a friendly `ServiceType` ("Day Care", "Vet Appointment"); a stay folds its length
  in ("Night Stay (3 nights)"), which is why the night-stay copy can show only the
  check-in date. This is why
  **`GroomingServiceCatalog` MOVED from `Pawfront.Infrastructure.Cosmos` into
  `Pawfront.Application/Services/PetGroomer/`** (public now, next to the
  `GroomingServiceCatalogEntry` record it already populated) — Application can't
  reference Infrastructure.Cosmos. `CosmosPetGroomerServiceRegistry.GetServiceCatalog()`
  is now a straight pass-through.
- **`serviceDateWithYear`** ("5 Aug 2026") is a second date format derived
  alongside `serviceDate` ("5 Aug") and used by these two templates ONLY — this is
  the card that asks the provider to commit to a date, whereas the rest of the copy
  reminds them about one they already have. Both are sent.
- `PetOwnershipLookup` carries **`PetName`** and **`ParentName`** so the create
  handlers get both from the point read they already make — no extra query
  (`ParentName` is a LEFT JOIN onto `Parent.PetParents`, so an unresolvable name
  can never turn an ownership answer into a null). Inline SQL, no sproc or
  DeployAll change. The single-day handler was passing `pet.PetType` where the
  pet's NAME was wanted, so that body read "for Dog" until 2026-08-10.
- **Pre-deploy outbox rows degrade cleanly**: templates live in code, so a row
  enqueued before this renders under the new copy with no `parentName` or
  `createdAtUtc` — "A customer has requested … Respond within the time shown in
  the app."
### Notification times render in SWISS local time (2026-08-06)

Notification copy used to be rendered in UTC like everything else, which made every
date and time in it **wrong by one or two hours** for every user — a 14:00 UTC
booking was announced as "14:00" to a provider whose clock read 16:00, and the
error changed size with DST. All consumers of both apps are in Switzerland, so the
fix is a display conversion to `Europe/Zurich`; **nothing stored moved off UTC.**

**Producers now hand over raw UTC instants and never a formatted string.** The
conversion + formatting happen once, at render time, in
[`NotificationLocalTime`](src/Pawfront.Application/Notifications/NotificationLocalTime.cs)
via `NotificationRenderer.WithLocalTimes`. That is the same division of labour
`serviceName` already used, for the same reason: booking notifications are
enqueued from three processes, one of which (`Pawfront.Functions`) is pure T-SQL.
Converting in `AT TIME ZONE` instead would have put the zone, the DST rules **and**
the `d MMM` / `HH:mm` format strings in two places that must never disagree.

| Producer emits (UTC instant) | Renderer derives (recipient-local) |
|---|---|
| `serviceStartUtc` — `BookingDate + StartTime`, or `CheckInDate + DropOffTime` | `serviceDate`, `startTime` (+ `checkInDate`, `dropOffTime` when night-stay) |
| `checkOutUtc` — `CheckOutDate + PickUpTime` | `checkOutDate` |
| `newServiceStartUtc` | `newServiceDate`, `newStartTime` |
| `newCheckOutUtc` | `newCheckOutDate`, and `newStartTime` **for night-stay** |
| `closingAtUtc` | `closingTime` |
| `acceptByUtc` | `acceptBy` |

- **Both halves reach the app.** `rendered.Data` is what the payload builder
  copies, so the push carries the display strings *and* the instants; the docs tell
  mobile to prefer the instant whenever it formats a time itself.
- **`closingAtUtc` is an instant, not a `TIME`** — converting a bare clock time
  needs the date it falls on. `SendBookingReminders` already had `ClosesAtUtc`
  computed, so its `ClosingTimeText` column was dropped rather than converted.
- **Night-stay modification copy is unchanged**: its `{newStartTime}` slot carries
  the new check-out **date** ("12 Aug at 17 Aug"), which is deliberate — a stay is
  proposed as a range and one shared template serves both booking kinds. The
  renderer applies that override after the `newServiceStartUtc` block, so ordering
  there matters.
- **Rows already in the outbox are safe.** A pre-deploy row has display strings and
  no instant, so it renders exactly as it used to; derived values only overwrite a
  display key when the matching instant is present. Malformed instants fall through
  to the renderer's neutral fallbacks instead of throwing.
- **The in-app inbox's `DataJson` now carries the instants instead of the
  pre-formatted `serviceDate`/`startTime`** (the enqueued JSON is never rewritten
  with the rendered values). Strictly better for the bell menu, which can format
  locally; note it was never identical to the push payload anyway, since
  `category` and the empty-string canonical fields are added at send time only.

**PROVISION for per-user timezones.** `NotificationLocalTime.Resolve(string?)` is
the only way the renderer gets a zone: blank or unrecognised ⇒ Swiss, so a bad
profile value can never fail a send. `NotificationDispatcher` calls it with
`recipientTimeZoneId: null` today. To go per-user: add `TimeZoneId` to
`Provider.Providers` / `Parent.PetParents`, join it in
`Notification.ClaimPendingNotifications`, put it on `ClaimedNotification`, and pass
it at that one call site. **Both IANA and Windows ids work** — `Europe/Zurich`
resolves on Windows and Linux, with `W. Europe Standard Time` named as an explicit
fallback for an ICU-less host (UTC only as a last resort).

**Everything else in the codebase still renders UTC wall-clock**, which is the one
thing to keep in mind here: `/availability/slots`, the agenda, booking reads and
the two apps' own screens are untouched, so a notification saying "16:00" now sits
next to an API payload saying `14:00`. That is the deliberate scope of this change
— see the deferred-issues list.

### Chat — provider ↔ pet-parent messaging (2026-08-09)

Real-time 1:1 chat, in its own host: **`Pawfront.ChatApi`** (SignalR hub + REST).
Mobile contract in [`docs/chat.md`](docs/chat.md).

**OPEN model** — any pet parent may message any provider, with **no booking
between them**. That is the right call for pre-sales questions, and it is why
blocking and rate limiting are part of the feature rather than follow-ups.

**Why a third host, not a hub on each existing one.** Azure SignalR cannot route a
message from host A's hub to a client connected to host B's hub. Two hubs would
mean two group namespaces and a cross-host relay. One hub, one host — which then
has to validate BOTH Firebase projects anyway, so the dual-scheme auth lands in a
host whose whole job is that rather than duplicated in two.

**Auth is the only place in the product where one host accepts two Firebase
projects.** Two named `JwtBearer` schemes (`ProviderFirebase` / `ParentFirebase`)
behind one `ChatUser` policy; whichever validates stamps a `pawfront_audience`
claim, so nothing downstream has to ask which fired. Expect one "issuer validation
failed" debug line per request — that is the mechanism working. The bearer token
is additionally accepted from `?access_token=` **on `/hubs/*` only**, because a
WebSocket handshake cannot carry a header; the REST surface stays header-only so
tokens never reach access logs.

**No `IUserIdProvider`.** SignalR's `GetUserId` is synchronous, but mapping a
Firebase token to a ProviderId / PetParentId needs an async SQL lookup — the id
this product addresses people by simply cannot be produced there. Two group
namespaces joined in `OnConnectedAsync` (which IS async) do the job instead:
`conv:{conversationId}` and `user:{Type}:{id}`. Azure SignalR routes groups across
instances, so nothing is lost.

**Storage split.** SQL owns the thread index, per-side read state, blocks and
presence — everything that must be consistent and countable. Cosmos owns message
bodies in the **`ChatMessages`** container, partition `/conversationId`, so a
thread's history is always a single-partition query. Same split as
`Event.Events` + the Events document.

**`[Chat]` schema** — `Conversations` (UNIQUE `(ProviderId, PetParentId)`, which
is what makes get-or-create race-safe), `ConversationParticipants` (a row per side
so every sproc is symmetric instead of branching on `CASE WHEN @ActorType`),
`ChatConnections` (presence), `BlockedParticipants`. No FK to
`Provider.Providers` / `Parent.PetParents` — same posture as
`Booking.BookingPayments`; an anonymised account keeps its conversations, and
counterparty names are **joined live** so a deleted account reads "Deleted
Provider" / "Deleted User".

**An INACTIVE provider is still chattable** — only `IsDeleted` closes the door.
Inactive means "not taking bookings": hidden from discovery, refused by
`Booking.CreateBooking`, but their profile is still viewable by deep link and
answering a question before switching back on is exactly what chat is for.

**Sending is TWO SQL calls around the Cosmos write (2026-08-10).** It used to be
one — `Chat.AppendMessage`, now **retired and dropped** — which committed the whole
SQL side *before* the body was written, so a failure afterwards left the thread
advanced, the recipient pushed, and the sender told the send had failed. Replaced
by:

```
Chat.ReserveMessageSequence  ->  Cosmos CreateItem  ->  Chat.CommitMessageAppend
  sequence only,                    the body            preview, read state, push
  nothing observable
```

- **`Chat.ReserveMessageSequence`** authorises the sender (party + block checks),
  takes `LastSequence + 1` under `UPDLOCK` on the conversation row, and records the
  reservation. It deliberately writes **nothing a client can observe**, and it
  commits before returning, so no lock is held across the Cosmos call.
- **`Chat.CommitMessageAppend`** does everything visible, in one transaction:
  refresh the inbox cache, advance the sender's read pointer, increment the
  recipient's unread, read presence, and — only when the recipient has no
  connection with `ActiveConversationId` = this conversation, and has not muted —
  enqueue their push and return its id, its `DataJson` and their FCM tokens.
  Deciding presence inside the sproc is race-safe for free, the same reasoning that
  put the notification enqueue inside the booking transition sprocs.
- **`Chat.ReleaseMessageReservation`** frees an uncommitted reservation when the
  body fails to write. Best-effort: leaving the row costs nothing, because a retry
  of the same id reuses its sequence and completes the send.

**A failed send therefore leaves no trace but a spent sequence number** — a gap,
and gaps were always free here (`LastSequence` is a high-water mark, which is
exactly why there is no `MessageCount` column to be wrong). Round-trip count is
unchanged: the reserve call replaces the Cosmos point read that used to do the
dedupe.

**Commit is idempotent, and that is load-bearing.** If a send dies *after* the body
lands, the retry finds it in Cosmos and comes straight back to commit, finishing the
delivery that was missed; a second commit moves no counter and queues no second
push. It also tolerates out-of-order commits — two racing sends both count toward
unread, but the inbox preview is only overwritten by the highest committed
sequence.

**`Chat.ConversationMessages` is the per-message ledger** the two phases hand off
through: `(ConversationId, MessageId)` PK, `Sequence`, `SenderType`/`SenderId`,
`ReservedAtUtc` (the message's `CreatedAtUtc` — the body is written between the
phases, so the timestamp has to come from the first), and `CommittedAtUtc` (NULL
until the body is durable). **The PK is what enforces `clientMessageId` uniqueness
per conversation**, across the hub and REST alike — previously that rested entirely
on the Cosmos document id, which cannot help in the one case that matters, where
the Cosmos write is what failed. It is NOT a copy of the message; the body still
lives only in Cosmos.

**Instant push, with the outbox demoted to a backstop.** `Notification.EnqueueInstantNotification`
writes the row **already claimed** (`Status = 'Sending'`, lease held). The
dispatcher's predicate is `Status IN ('Pending','Sending') AND NextAttemptAtUtc <= now`,
so it skips a leased row — no double push — but takes it over if the lease lapses
because the chat host died mid-send. The host renders with the **same**
`NotificationRenderer` + `NotificationPayloadBuilder`, sends with the **same**
`IPushSender`, and reports through the **same**
`Notification.CompleteNotificationDelivery`. There is no case where a message is
stored and its notification is lost.

**The message id comes from the client** and is both the Cosmos document id and
half the `Chat.ConversationMessages` PK — one value keys idempotency in both
stores. Cosmos enforces uniqueness on nothing but `id`, and ids are
partition-scoped so two threads cannot collide.

**Presence** is `Chat.ChatConnections`, written on connect and cleared on
disconnect. `OnDisconnectedAsync` is best-effort — a crashed host never fires it —
and a stale row is worse than clutter: it makes its owner look permanently present
and silences their pushes. `ChatPresenceSweepFunction` (Pawfront.Functions, every
1 minute) purges rows quiet for 3 minutes. Clients heartbeat every ~60 s.

**Rate limits** partition on the **Firebase uid claim**, not the IP — abuse is
per-account, a carrier NAT shares one IP, and the uid is available synchronously
(the partition selector is sync; a ProviderId needs async SQL). 30 messages/minute
and 10 new conversations/hour. **Covers the REST send only** — a hub invocation
arrives over an established socket, so `ChatHub.SendMessage` is not limited.

**Blob** — `chat-attachments/<conversationId>/<guid>.<ext>`,
`BlobUploadKind.ChatAttachment`, ≤5 MB (larger than the 3 MB galleries: a chat
photo is usually straight from a camera). Uploaded BEFORE the message, the
opposite order to review photos — there the review must exist to key the path,
here the conversation already does.

**THROW codes 51320-51328:** `51320` provider not found · `51321` provider account
deleted · `51322` pet parent not found or deleted · `51323` blocked (either
direction) · `51324` actor is not a party · `51325` conversation not found ·
`51326` sender is not a party · `51327` block between two participants on the same
side · `51328` no reservation exists for this message (commit without reserve —
unreachable through the service, which always reserves first).

**Deliberately not built:** reporting/abuse (it terminates in a support workflow
and there is no Helpline module — same reason `DISPUTE_RESOLVED` has copy but no
trigger); group threads; message editing; and a mute ENDPOINT — `IsMuted` is
modelled end to end and consulted by the push decision, but nothing can set it yet.

## In progress / next step

**"New Job Request" copy rewritten (2026-08-10) — built, NOT deployed. NO SQL in
this one.** See the "New Service Booking" trigger notes above. Pure C#: the two
`*_BOOKING_REQUESTED` templates, `respondWithin` + `serviceDateWithYear` derived in
`NotificationRenderer`, `createdAtUtc` + `parentName` emitted by
`BookingNotificationService`, `ParentName` added to `PetOwnershipLookup` (inline
SQL in `SqlPetParentOwnershipReader` — **not** a sproc, so `DeployAll.sql` does not
need re-running for it). Also fixes the single-day handler passing `pet.PetType`
where the pet's name belonged.

**Two artifacts must ship: `Pawfront.PetParentApi` (producer) and the Function App
(renderer/dispatcher).** Order doesn't matter and neither half breaks alone — an
old dispatcher against the new host just renders the old copy, and a new
dispatcher against the old host renders the new copy with its two neutral
fallbacks. Both are needed for the fixed body.

Verified: the solution builds clean (0 warnings, 0 errors), and the producer +
renderer were exercised end-to-end against real payloads through the actual
`BookingNotificationService` → `NotificationRenderer` path — BR-17 giving "24
hours", BR-53 giving "2 hours 15 minutes" (including from a sub-second creation
stamp, which truncation would have shown as 14), a 45-minute window, the exact-24h
boundary giving 22 hours, missing names falling back, a 23:30 UTC winter service
correctly rolling its local date forward, the night-stay twin, and a pre-deploy
outbox row degrading as described. **Not** verified: no push has been sent — that
still needs the two Firebase service-account credentials.

After deploying, confirm one real push per shape: create a booking well in advance
(expect "Respond within 24 hours" with the parent's real name and the pet's real
name, not "Dog"), then one about 4 hours out (expect the short window), then a
night stay.

**`NO_PAYOUT` terminal payout status (2026-08-09) — built, NOT deployed.** See
"`NO_PAYOUT` closes the other end of the lifecycle". Widens
`CK_Bookings_PayoutStatus` + `CK_NightStayBookings_PayoutStatus` (with **drop +
recreate repair blocks** in `DeployAll.sql` — the by-name `IF NOT EXISTS` guards
could never repair an existing constraint, the same trap `CK_Providers_Gender`
hit), alters four sprocs (`UpdateBookingStatus`, `UpdateNightStayBookingStatus`,
`SettleUnstartedJobsAsNoShow`, `ExpireStaleCreatedBookings`) to write it, and
adds a one-time idempotent backfill over existing no-show / expired rows. C# is
one new constants file plus the two in-memory dev mappings. Re-run
`Deployment/DeployAll.sql`.

**Deploy order does not matter for this one** — it is additive on the wire and
the C# never sends the value, so an old API against the new SQL just reports the
new status, and a new API against the old SQL behaves exactly as today.

Verified: the solution builds clean (0 warnings, 0 errors); the two table files,
the four sprocs and `DeployAll.sql` parse clean under a verified-loaded net462
ScriptDom `TSql160Parser`; and all four sproc bodies were diffed against their
DeployAll copies and are identical modulo comments. **Not** verified: nothing has
run against the dev database, so the CHECK repair blocks, the `NOT LIKE
N'%NO[_]PAYOUT%'` definition probe and the backfill are parse-clean only.

After deploying, confirm: report a no-show on a confirmed booking (both kinds)
and read `paymentDetails.payoutStatus` on the detail — expect `NO_PAYOUT`, not
`Pending`; let a `CREATED` booking expire via the sweep and check the same; and
re-read a booking that was already no-showed before the deploy to confirm the
backfill caught it. Then check a normal `COMPLETED` booking still reads `Pending`
and flips to `Paid` on mark-paid, and that `GET /providers/{id}/earnings/overview`
is unchanged (these bookings were never in the totals — `BookingAmounts` gates on
`IsEarned`).

**"Honour bookings & deactivate" (2026-08-09) — built, NOT deployed.** See the
"Provider master Active/Inactive switch" section. `Provider.SetProviderActiveStatus`
gains `@AcknowledgeExistingBookings BIT = 0` and a 4th success column
(`HonouredBookingCount`); the request/response contracts, the service interface
and both implementations follow. Re-run `Deployment/DeployAll.sql`.

**This one is NOT additive — deploy the SQL before the API.** The C# now always
passes `@AcknowledgeExistingBookings`, so against the old procedure **every**
deactivation fails ("too many arguments specified"), not just the new path. It
is the only item in the undeployed batch with that property.

Verified: the solution builds clean (0 warnings, 0 errors) and the sproc file,
`Tables/Providers.sql` and `DeployAll.sql` parse clean under a verified-loaded
net462 ScriptDom `TSql160Parser`; the sproc's DeployAll copy was diffed against
the standalone file and is identical modulo comments. **Not** verified: nothing
has run against the dev database, so the new parameter and the 4-column success
shape (read as `GetInt32(3)`) are parse-clean only.

After deploying, confirm: deactivate with a future confirmed booking and no flag
(expect 200 `BookingsExist`, `isActive` untouched — check `GET
/providers/{id}/profile` still reads true); resubmit with
`acknowledgeExistingBookings: true` (expect 200 `Updated`,
`honouredBookingCount` matching the list length); then confirm the provider is
gone from `GET /providers` and all five `/providers/search/*`, that a new
booking is refused with 409 `ProviderInactive`, and — the point of the whole
change — that the honoured booking still runs `/start-job` →
`/start-job/verify` → `/complete` → `/paid` normally. Also deactivate a provider
with no future bookings and confirm `honouredBookingCount` is 0.

**Early-completion capacity release (2026-08-09) — built, NOT deployed.** See
"Early completion releases the unused remainder" above. Adds
`Booking.Bookings.ActualEndTime` + `Booking.NightStayBookings.ActualCheckOutDate`
(each with a clamping CHECK), rebuilds the two capacity indexes
(`IX_Bookings_Service_Date_Status`, `IX_NightStayBookings_Service_Dates_Status`)
to cover the new column, and alters **twelve** sprocs: the four that write
COMPLETED (`CompleteBooking`, `CompleteNightStayBooking`, `UpdateBookingStatus`,
`UpdateNightStayBookingStatus`) and the eight that read occupancy
(`CreateBooking`, `CreateCustomBooking`, `CreateNightStayBooking`,
`GetBookingsForDate`, `GetAgendaForDate`, `GetNightStayOccupancy`,
`RespondBookingModification`, `RespondNightStayBookingModification`), plus
`Provider.CreateClosures` and `Provider.SetProviderActiveStatus`. Re-run
`Deployment/DeployAll.sql`. **No C# changed and no wire contract changed.**

Verified: the solution builds clean (0 warnings, 0 errors); all 16 touched SQL
files and `DeployAll.sql` (551 batches) parse clean under a **verified-loaded**
net462 ScriptDom `TSql160Parser`; and all 158 procedures in `DeployAll.sql` were
scanned for duplicate `DECLARE`s (T-SQL variables are procedure-scoped, and
ScriptDom does NOT catch a collision) — none. **Not** verified: nothing has run
against the dev database, so the new columns, the CHECK constraints, the index
rebuilds and the `COALESCE` expressions are parse-clean only — a binder error
would only surface on deploy.

After deploying, confirm: start and complete a day-care booking well before its
booked end, then re-read `/availability/slots` and `/providers/{id}/agenda` for
that service and date (expect the freed hours bookable, subject to the 2-hour
lead time, and the block's `isBookable` true), and actually create a booking in
the released window; do the same for a night stay completed a day early against
`?endDate=` on the slots endpoint and `/providers/search/night-stay`. Then check
the booking-detail read still shows the ORIGINAL `endTime` / `checkOutDate` and
an unchanged `paymentDetails.totalAmount`, and that `GET
/providers/{id}/earnings/bookings` reports the same amount as before — the whole
point is that capacity moved and money did not. Also complete one booking AFTER
its booked end and confirm nothing is released (the column stays NULL).

**Pending-job details + list review block (2026-08-09) — built, NOT deployed.**
Two unrelated asks in one batch; re-run `Deployment/DeployAll.sql` for both.
1. **Both deletes' pending-job list gained `providerProfilePhotoUrl` and a `price`
   block** — see "The pending-job shape". `Parent.DeletePetParent`'s result set 3
   gained four pricing-input columns, and **`Parent.DeletePetParentPet` gained the
   refusal itself**: a pet with unfinished jobs now answers **409
   `PendingJobsExist`** instead of soft-deleting. That is a **behaviour change** on
   an endpoint that previously always succeeded — worth flagging to mobile.
2. **`review: { canReview, rating }` on the parent's two "my bookings" lists**, via
   the new `Review.ListPetParentBookingReviews` + the new filtered index
   `IX_BookingReviews_PetParent_Authored`. Purely additive on the wire.

Verified: the solution builds clean; the two edited sprocs, the new one, the
edited table file and `DeployAll.sql` all parse clean under the **net472**
ScriptDom (`TSql160Parser`, null-guarded); and the parent host starts with its
route table intact. **Not** verified: none of it has run against the dev database —
parse-clean is not deploy-clean, so the new `BlockedByPendingJobs` ordinal on
`DeletePetParentPet` (read as `GetBoolean(4)`), the widened pending-job column list
(one C# reader, `PendingJobReader`, serves both sprocs — they must stay in step),
and the `TINYINT` rating read as `GetByte` are all unexercised.

After deploying, confirm: delete a pet that has a CONFIRMED booking (expect 409 +
`pendingJobs`, and the pet still readable), then cancel it and retry (expect the
soft delete); check a blocked account delete's jobs carry a non-null
`providerProfilePhotoUrl` and a `price.totalAmount` matching that booking's
`paymentDetails.totalAmount` on its detail read — including one night stay, whose
total is rate × nights; and read `GET /pet-parents/{id}/bookings` before and after
reviewing a completed booking (expect `canReview` true→false with the rating
appearing).

**Chat send/read fixes (2026-08-10) — code done, chat SQL APPLIED TO DEV, host
not redeployed.** The first real run of chat against the dev database found the
feature completely unable to send or read a message. Two independent root causes,
both proven rather than inferred:

1. **`Chat.AppendMessage` crashed its caller after committing.** Its result set 2
   projected `COALESCE(@RecipientIsMuted, 0)`, and because **INT outranks BIT in
   data type precedence** that column came back typed `INT`. `SqlDataReader`
   `GetBoolean` does not coerce — it threw `InvalidCastException` — and by then the
   procedure's transaction had committed. So every send advanced the thread,
   incremented unread and enqueued the recipient's push, then returned 500. The
   provider got the message; the sender was told it failed. It also explained the
   duplicate-send report: the Cosmos write sat *after* the throw, so no document
   was ever stored, and the `clientMessageId` dedupe — which was a Cosmos point
   read — had nothing to find. Confirmed against dev: conversation
   `95d0920a-…d955` read `LastSequence = 4` with **zero** documents in its Cosmos
   partition.
2. **`GET /messages` sent Cosmos a query it cannot parse.** `ListAsync` built
   `(@beforeSequence IS NULL OR c.sequence < @beforeSequence)`; Cosmos NoSQL has no
   `IS NULL` **operator** (only the `IS_NULL()` function), so the service rejected
   it with `SC1001 Syntax error, incorrect syntax near 'IS'` → 400 → 500, on every
   call including the first page. That is why no parameter combination helped —
   the query never reached the parameter.

Fixed by: the reserve/commit split described in the Chat section (which is also
what makes a send atomic and moves `clientMessageId` uniqueness into a primary
key); `CAST(... AS BIT)` on every flag the two new procedures project, plus a
`ReadFlag` helper in `SqlChatConversationStore` so the same slip can never again
take down a write path; and building the history cursor clause only when there is
a cursor.

**Applied to the dev database already** (targeted, NOT a full `DeployAll` run —
that would have dragged in every other undeployed batch): `Chat.ConversationMessages`,
`Chat.ReserveMessageSequence`, `Chat.CommitMessageAppend`,
`Chat.ReleaseMessageReservation`, and the DROP of `Chat.AppendMessage`. All of it
is mirrored in `DeployAll.sql`, so a later full run is a no-op for these objects.
**Other environments still need `Deployment/DeployAll.sql`.**

**Deploy the SQL before the host.** The C# no longer calls `Chat.AppendMessage` at
all, so a new host against an old database fails every send with "could not find
stored procedure". The reverse is harmless: the old host is already 100% broken on
this path.

Verified against dev: the solution builds clean (0 warnings, 0 errors); the chat
host starts and its DI graph validates; both new procedures return **`Boolean`**
for every flag column (the exact defect, gone); reserving twice with one message id
returns the **same** sequence with `isReplay` true and does not advance
`LastSequence`; a reservation on its own leaves preview, unread count and outbox
untouched (the atomicity property); commit moves unread 4→5, writes exactly one
outbox row and returns the recipient's FCM token; **committing again changes
nothing and queues no second push**; release frees an uncommitted reservation and
refuses a committed one; a stranger is still rejected with 51326. Separately, the
**shipped** `CosmosChatMessageStore` was driven through its real DI registration
against the dev Cosmos account: list with no cursor, list with
`beforeSequence=999999`, paging back through `nextBeforeSequence`, the 409 replay
(first write wins), and soft delete all behave correctly. Probe rows were rolled
back and probe documents deleted.

**Not verified:** no HTTP request or hub connection has been made — that needs a
real Firebase token from each project (the web API keys are blank in
`docs/Pawfront.E2E.postman_environment.json`), and no push has been delivered,
which additionally needs the two Firebase service-account credentials (**still
outstanding**, same blocker as the rest of the notification work). Without
credentials the host still starts and chat still works; sends report "No Firebase
credentials are configured", the outbox row is left un-completed, and the
scheduled dispatcher retries it.

**The four messages already in conversation `95d0920a-…d955` are unrecoverable** —
their bodies were never written anywhere, so the thread reads empty apart from the
inbox preview holding the last one's text. Sequences 1-4 are now permanent gaps.

After redeploying the host, verify in this order: (1) `POST /conversations` between
a real provider and parent; (2) send a message and confirm the Cosmos document, the
`Chat.ConversationMessages` row and the `Chat.Conversations` preview all agree;
(3) **send the same `clientMessageId` over the hub and over REST** and assert
`LastSequence` moves once, not twice; (4) two hub clients, both online — assert
delivery in well under a second AND that **no** outbox row was written; (5)
recipient offline — assert exactly ONE row and that it ends `Status = 'Sent'`, not
`Pending` (Pending would mean the timer is about to send a duplicate); (6) kill the
chat host mid-send and assert the scheduled dispatcher takes the leased row over
exactly once.

Still previously unverified and unchanged by this batch: the rest of the chat
surface (blocks, attachments, presence sweep) has never run against SQL either,
though its procedures are deployed.

**Booking reviews & ratings (2026-08-08) — built, NOT deployed.** See the "Reviews &
ratings" section above. Adds the **`[Review]` schema**, two tables
(`BookingReviews` + `BookingReviewPhotos`, three indexes) and six sprocs
(`UpsertBookingReview`, `GetBookingReview`, `AddBookingReviewPhoto`,
`DeleteBookingReviewPhoto`, `ListProviderReviews`, `GetPetParentRatingSummary`), all
mirrored into `DeployAll.sql` — re-run it. **No existing SQL object was altered**, so
this half of the batch is purely additive and independent of the other undeployed
work below.

Verified: the solution builds clean, and all eight new SQL files plus `DeployAll.sql`
(503 batches) parse clean under a **real** ScriptDom `TSql160Parser`. Note the first
parse attempt silently reported everything "ok" because the net8.0 ScriptDom assembly
won't load under Windows PowerShell 5.1 and the null parser was never checked — the
script at `scratchpad/ParseSql.ps1` now uses the **net472** build and throws if the
parser or a parse result is null. **Not** verified: none of it has run against the dev
database, so the `TINYINT` rating (read as `GetByte`, not `GetInt32`), the filtered
indexes and the three-result-set `ListProviderReviews` are parse-clean only.

After deploying, confirm: complete a booking and check `review.canReview` flips true
on both hosts' detail reads; submit a parent review then resubmit it (expect 201 then
200, with `createdAtUtc` unchanged); attach 6 photos (the 6th should 409); rate the
parent from the provider host and check `rating` + `ratingCount` appear on
`GET /pet-parents/{id}/details`; then confirm `GET /providers/{id}/reviews` reconciles
its summary with its rows and that `GET /providers/{providerId}` shows the same
average. Also worth one check that a PAID booking is still reviewable (that is the
main departure from the letter of the original request).

**Swiss-local notification times (2026-08-06) — built, NOT deployed.** See the
"Notification times render in SWISS local time" section above. Touches six sprocs
(`EnqueueBookingNotification`, `Request`/`RespondBookingModification`,
`Request`/`RespondNightStayBookingModification`, `SendBookingReminders`) plus the
Application renderer and `NotificationDispatchFunction`. **Deploy the SQL and the
Function together** — see the ordering caveat in deferred issue 7. Verified so far:
the solution builds clean, all six sprocs + `DeployAll.sql` parse clean under
ScriptDom, and the renderer was exercised against real payloads (summer 14:00 UTC →
`16:00`, winter → `15:00`, a 23:30 UTC drop-off correctly rolling the date forward,
night-stay modification copy unchanged, pre-deploy rows and malformed instants both
degrading as designed). **Not** verified: none of the SQL has run against the dev
database, so the new `DATETIME2(0)` parameters and the `CONVERT(..., 126)`
expressions are parse-clean only — a binder error would surface on deploy.
After deploying, confirm one push per shape: a fresh booking request, an accepted
modification (both kinds), and a pick-up nudge.

**August QA batch (2026-08-05) — built, NOT deployed.** Seven reported issues.
Five needed code; two were already correct in the repo and only needed the
outstanding SQL deploy. Re-run `Deployment/DeployAll.sql` for all of it.
1. **Parent account delete now refuses while unfinished jobs exist** — 409
   `PendingJobsExist` with the list in `data`. Decided inside
   `Parent.DeletePetParent` so it is race-safe. New third result set.
2. **Inactive providers no longer appear in discovery or the five searches** —
   new `ActiveOnlyProviderDiscoveryService` decorator + batched
   `IProviderActiveStatusReader`. No schema change.
3. **`providerDetails.providerPhotoUrl` is populated** from the Cosmos offering
   doc's image (was hardcoded null in all four detail mappings). No SQL change
   beyond what was already there.
4/6. **`paymentDetails` payout block is real** — the `PayoutId`-at-`COMPLETED` /
   `PayoutStatus`-at-`PAID` stamping was already written on 2026-08-05 and merely
   undeployed; what is new is **`payoutMethod` + `paidAtUtc`**, LEFT-JOINed from
   `Booking.BookingPayments` onto both booking-detail sprocs (appended last, so
   reader ordinals stayed stable).
5. **Parent mobile uniqueness needed no code** — `Parent.CompletePetParentProfile`'s
   THROW 51222 pre-check, the `UX_PetParents_MobileNumber` unique backstop, and the
   409 `MobileNumberAlreadyExists` mapping were all already in the repo. Purely a
   deployment gap. (If the dev database already holds duplicate numbers, DeployAll
   PRINTs them and skips the index — resolve them and re-run.)
7. **`DELETE /pets/{petId}` is a soft delete** — new `Parent.Pets.IsDeleted` +
   `DeletedAtUtc`, anonymise-in-place, and it no longer NULLs
   `Booking.Bookings.PetId`. Deleted pets are filtered out of every parent-facing
   read, the ownership reader, onboarding status, and both booking creates.

Nothing here has been executed against the dev database. The solution builds
clean and every SQL file parses, but parse-clean is not deploy-clean. Verify
after deploying: delete a parent with a CONFIRMED booking (expect 409 +
`pendingJobs`), flip a provider inactive and confirm they vanish from
`/providers/search/*`, read a booking detail for `providerPhotoUrl`, mark a
booking paid and check `payoutStatus`/`payoutId`/`payoutMethod`, and delete a pet
that has a past booking then confirm the provider's booking detail still shows
"Deleted Pet" rather than a blank.

**Earnings & spend reporting is built but NOT deployed (2026-08-05).** Re-run
`Deployment/DeployAll.sql` — it adds `Booking.PayoutNumberSequence`, the
`Booking.BookingAmounts` function and four sprocs
(`GetProviderEarningsSummary`, `ListProviderEarningsBookings`,
`GetPetParentBookingSummary`, `ListPetParentBookingHistory`), alters four existing
sprocs to stamp/settle the payout columns, and runs the one-time
`PayoutStatus`/`PayoutId` backfills. The solution builds clean and every SQL file
parses, but **none of it has been executed against the dev database** — the sprocs,
the TVF and especially the `sp_sequence_get_range` backfill are unverified at
runtime (parse-clean is not deploy-clean; a binder error would only surface on
deploy). Verify after deploying: complete a job and check `PayoutId` is stamped,
mark it paid and check `PayoutStatus` flips, then confirm
`GET /providers/{id}/earnings/overview` reconciles with
`GET /providers/{id}/earnings/bookings`.

**The booking expiry sweep has been rebuilt as an Azure Function (2026-08-02).**
The old in-database sweep (`Booking.ExpireStaleBookings` + the
`BookingExpirySweeper` hosted service, every 10 min in **both** API hosts with no
coordination between them) is gone — deleted from the repo and dropped by
`DeployAll.sql`. Its replacement is `BookingSweepFunction` in the new
`Pawfront.Functions` project (isolated-worker Azure Functions, `net10.0`,
`[TimerTrigger("0 */5 * * * *")]` — every 5 minutes), now in `Pawfront.slnx`.

It calls three new sprocs, sequentially over one `SqlConnection` per tick (see
`src/Pawfront.Functions/Sweeps/`):
1. `Booking.ExpireStaleCreatedBookings` (BR-17 **+ BR-53**) — a booking nobody
   accepted → `EXPIRED`, on either trigger: `CREATED` older than 24 h, **or**
   `CREATED` with under 2 h to `BookingDate+StartTime` /
   `CheckInDate+DropOffTime` (**added 2026-08-02**; the same cutoff BR-01 uses to
   refuse a new booking for that slot, so the unaccepted one dies when a
   replacement could no longer be made). Matching reject-only guards were added
   to both status-engine sprocs (THROW 51153 / 51273 → 409 `BookingExpired`),
   without which the rule would only hold to the job's 5-minute granularity.
2. `Booking.RevertExpiredModificationRequests` (BR-30, **widened 2026-08-02** to
   cover both proposal directions — previously parent-only) —
   `MODIFICATION_REQUEST_BY_PARENT` **or** `MODIFICATION_REQUEST_BY_PROVIDER`
   within 2 h of `BookingDate+StartTime` / `CheckInDate+DropOffTime` → back to
   `CONFIRMED`, staging row deleted. Runs **before** #3 so one tick can settle a
   booking that then also proves a no-show.
3. `Booking.SettleUnstartedJobsAsNoShow` (BR-38, **revised cutoff** — see the
   lifecycle-section note above) — confirmed-equivalent or `START_JOB` past its
   settlement moment → `START_JOB` gives `PARENT_NO_SHOW`, anything else
   `PROVIDER_NO_SHOW`.

Each sproc keeps its own `BEGIN TRAN … COMMIT` (no shared transaction across the
three — one failing doesn't roll back a rule that already committed this tick).
The old arm 4 (`JOB_EXPIRED`) was **dead code** — its predicate was identical to
arm 3's, which ran first — and was deliberately **not** ported. The old
double-instance bug is avoided by construction, not by extra code: the Azure
Functions host serialises a timer trigger's invocations across however many
instances **one** Function App scales to — this only holds as long as
`Pawfront.Functions` is deployed as a single app, not once per API host as the
old hosted service was.

Configuration mirrors the two API hosts' `ConnectionStrings:SqlServer` +
`AzureKeyVault:*` via `Pawfront.Infrastructure.Azure`'s existing
`AddPawfrontAzureInfrastructure`, wired in the Function's `Program.cs`, and it
now lives in a committed **`src/Pawfront.Functions/appsettings.json`** copied
into the publish payload — same shape and same values as the API hosts'
(`AzureKeyVault:Enabled = false`, so the SQL connection string is read straight
from config and Key Vault is never contacted).

**The config gotcha that bit once (2026-08-02):** `local.settings.json` is a
**local-only** file and is *never* part of the published payload — verified by
publishing and listing the output. The deployed app therefore saw neither
`AzureKeyVault__Enabled=false` nor the connection string; with
`AZURE_FUNCTIONS_ENVIRONMENT=Production` (so `IsDevelopment()` false) and
`GetValue("AzureKeyVault:Enabled", true)` **defaulting to true**, DI took the
Key Vault branch and threw `AzureKeyVault:VaultUri is required` while activating
`BookingSweepFunction` — whose ctor takes `IPawfrontSecretProvider`, so the
`SecretClient` is built eagerly even though `GetConnectionStringAsync` prefers
`ConnectionStrings:SqlServer` and would never have used the vault.

`Program.cs` re-adds the JSON sources explicitly from `AppContext.BaseDirectory`
(the Functions host doesn't guarantee the content root is the payload folder),
then re-applies `AddEnvironmentVariables()` **last** so an Azure Application
Setting still overrides the file. Those Application Settings bind through
environment variables, so nested keys there use **double underscore**, not colon
— `ConnectionStrings__SqlServer`, `AzureKeyVault__Enabled`. `IConfiguration`'s
env-var provider translates `__` → `:` automatically, so the C# reads are
unchanged from the API hosts' style.

Still open: the sprocs are new and **not yet deployed** (re-run
`Deployment/DeployAll.sql`); the Function itself hasn't been run against the dev
DB. See `docs/booking-rules.md` for the full BR-17/BR-30/BR-38 write-up.

**Event ticket cancellation is built; the refund leg is not.** The
soft-cancel endpoint `DELETE /event-bookings/{bookingId}` (both hosts) flips
`Status → Cancelled` + stamps `CancelledAtUtc` via `Event.CancelEventBooking`,
which releases capacity automatically (the `CreateEventBooking` capacity SUM
filters `Status = N'Confirmed'`). The who-can-cancel policy decision is
settled: the booker is matched by their Firebase **email claim** against the
free-text `BookerEmail`. **Still TODO:** the external refund leg when
`PaymentStatus = Paid` — cancelling a paid booking today frees the seat but
does not initiate a refund.

The earlier deferred **pet-parent subscription** design (with
`EventSubscriptions` + `EventSubscriptionPets`, pets per subscriber,
UNIQUE(EventId, PetParentId)) was **superseded** by the simpler ticket-
booking model — anyone can book, attendees are free text, payment is
explicit. If a richer "pet parent + their pets attend" model is needed
later, it would layer on top of `Event.EventBookings` rather than replace
it.

## Deferred / known issues — pull forward when relevant

1. **Credentials in `appsettings*.json` files** — SQL password, Cosmos
   AccountKey, Blob AccountKey are all in committed config. **Rotate
   these.** Move to user-secrets or Key Vault before any production
   exposure.
2. **Legacy in-memory `IProviderService` / `POST GET /providers`** —
   these two endpoints (different from the real provider profile flow)
   are placeholders backed by `InMemoryProviderService` and don't persist
   across restarts. Safe to delete when no longer used as a smoke test.
   The real `Provider.Providers` row IS persisted — written by
   `Provider.CompleteProviderProfile` and read by the new
   `Provider.GetProviderProfile` sproc (exposed at
   `GET /providers/{id}/profile`). Bookings, closures, services,
   policies, availability, OTPs, and events are all SQL-backed.
   The legacy in-memory `/providers/{id}/services` POST/GET were removed
   when the real per-service catalog (`Provider.ProviderServices`) shipped;
   `GET /providers/{id}/services` now returns the SQL-backed catalog.
3. **Pet Adoption & Sale offering** not built — only basic registration.
4. **Online events** have no Cosmos doc / extension fields yet — their only
   online-specific field is the SQL `Event.Events.EventLink` (joining URL).
5. **No DELETE for events** — create + full-replace edit (`PUT`) exist; there
   is no delete/cancel-an-event flow yet.
6. **No pet-parent auth.** All endpoints share the provider's
   `FirebaseUser` policy. When the consumer app launches it'll need its
   own auth (separate Firebase project or role claim differentiation).
7. **Push notifications: engine built, all booking triggers wired (2026-08-04).**
   Every card in the Notifications V3 spec now has a trigger except the three
   whose modules don't exist — `INVOICE_ISSUED` (no invoicing),
   `DISPUTE_RESOLVED` (no Helpline/ticket module), `PROMOTIONAL_MESSAGE` (no
   campaign module). Those carry type + copy + route + payload contract so mobile
   can build against them; wiring one later is a single publisher call.
   **`MESSAGE_RECEIVED` left this list on 2026-08-09** — the chat module enqueues
   it from `Chat.AppendMessage`, and it is the one type normally sent by an API
   host rather than by the scheduled dispatcher (see the Chat section). Of the six **event-ticket** types (2026-08-04) the two
   ORGANISER-side ones are now wired via `Notification.EnqueueEventNotification`
   (called from `Event.CreateEventBooking` / `Event.CancelEventBooking`); the four
   buyer-side ones are not — `EVENT_BOOKING_CONFIRMED` / `_CANCELLED` are the
   buyer's own action (relevance rule), and the two payment types have **no
   addressable recipient**: `Event.EventBookings` identifies its booker only by
   free-text `BookerEmail` with no FK, and the payment webhook carries no JWT, so
   sending to the buyer needs a persisted booker id on the booking row first.
   Still outstanding: the two Firebase
   service-account credentials, the real mobile route table (`NotificationRoutes`
   is placeholders), and APNs keys on both Firebase projects. **Device-token
   register/refresh IS built** — `POST`/`DELETE /device-tokens` on both hosts
   (2026-08-03). **None of the new SQL is deployed yet** — re-run
   `Deployment/DeployAll.sql`, and deploy `BookingReminderFunction` with the
   existing Function App. The **Swiss-local-time change (2026-08-06)** is in the
   same undeployed batch and spans both halves: the six enqueue/producer sprocs
   AND `NotificationDispatchFunction`. Deploy them **together** — an old sproc
   against the new dispatcher still renders (it emits the display strings and no
   instant, the back-compat path), but a NEW sproc against an OLD dispatcher emits
   instants nothing reads, and every date and time in the copy collapses to the
   renderer's "the booked date" / "the booked time" fallbacks.
   Separately, SMS OTP is still unwired — `NoOpProviderMobileOtpSender` /
   `NoOpPetParentMobileOtpSender` remain the only OTP senders. That is a
   different transport (SMS, not push) and is unaffected by the FCM work.
8. **`/api/v1/events/{eventId}` is provider-agnostic** — for future
   pet-parent discovery. No discovery search yet (filter by category,
   date, geo).
9. **`/health` is behind the `FirebaseUser` policy** — fine for now but
   may need to be open for load balancer probes later.
10. **Only NOTIFICATIONS render in Swiss local time (2026-08-06); every API
    response still returns UTC wall-clock.** So a reminder now says "16:00" for a
    booking whose `startTime` reads `14:00` on `GET /providers/{id}/bookings`, the
    agenda, the slot grid and the booking detail. If the apps display those raw,
    the two surfaces disagree — worth confirming with mobile which side they were
    already compensating for, since a client that was adding the offset itself will
    now double-count it on notifications. Two ways out, both bigger than the
    notification fix: convert on every read surface too, or store a per-user
    `TimeZoneId` and treat the wall-clock columns as that zone's local time
    throughout. The notification layer is already built for the second
    (`NotificationLocalTime.Resolve`); nothing else is.
    Related: earnings period boundaries are still UTC-aligned (see that section).
11. **Event reviews are NOT built (deferred 2026-08-08).** Booking reviews shipped;
    reviewing an *event* was scoped out because eligibility can't be established
    cleanly yet: `Event.EventBookings` identifies its booker only by free-text
    `BookerEmail` with **no FK** to `Parent.PetParents`, and an event has no
    completion status. Doing it properly needs (a) a persisted booker id on the
    ticket row, and (b) a decision on what stands in for "COMPLETED" — the
    likeliest answer being a non-cancelled ticket plus the event's
    `EndDate + EndTime` having passed. The `[Review]` schema was created rather than
    putting these tables under `[Booking]` precisely so an `EventReviews` sibling can
    land there later. Related open questions when it does: whether reviews of a
    *provider-organised* event roll into that provider's rating (they should not, by
    default), and whether a provider who bought a ticket may review.
12. **No rating on the discovery or search cards.** `GET /providers` and the five
    `/providers/search/*` cards carry `completedBookings` but no rating, so a parent
    only sees the score once they open a profile. The batched shape needed for it
    already exists in the same fan-out those cards use for banners and booking
    stats — it would be a `GetByProviderIdsAsync` on the review reader plus one field
    per card.
13. **No push notification when a review is received.** There is no such type in the
    Notifications V3 spec, so nothing was invented; wiring one later is a single
    publisher call plus a template entry, the same as the four unwired types in
    issue 7.
14. **`ChatHub.SendMessage` is NOT rate limited.** ASP.NET Core rate limiting is
    middleware, and a hub invocation arrives over an already-established socket
    rather than as a new request — so the 30/min and 10/hour policies cover the
    REST send and conversation-open only. A client determined to flood will use
    the socket. Closing it needs a limiter inside the hub method (a per-connection
    token bucket in `Context.Items` would do), which was deliberately not built
    for the first cut. Blocking still works, so the recipient has a remedy.
15. **Chat muting has no endpoint.** `Chat.ConversationParticipants.IsMuted` is
    modelled end to end — column, procedures, and it is genuinely consulted by
    `Chat.AppendMessage`'s push decision — but nothing can set it. Adding it is one
    sproc and one route; it was left out because the plan did not call for it and
    inventing the UX for it here would have been guessing.
16. **Chat attachment blobs are never cleaned up.** A soft-deleted message clears
    its `attachment` from the Cosmos document, but the blob itself stays in
    `chat-attachments/<conversationId>/`. Same known gap as the pet and review photo
    galleries, and it wants one sweep covering all of them rather than a per-feature
    fix.
17. **Chat has no E2E scenario coverage.** The Postman/newman suite in
    [`docs/E2E-TESTING.md`](docs/E2E-TESTING.md) does not touch the chat host. The
    REST surface is scriptable there (that is partly why the socket-less send path
    exists), but the hub and the push-suppression behaviour are not — proving those
    needs a real SignalR client and two Firebase tokens.

## Endpoint catalogue (current)

Two hosts. Provider host (`Pawfront.Api`) — all under `/api/v1`, all require
the `FirebaseUser` policy (Firebase project `littersoftprovider`). Pet-parent
host (`Pawfront.PetParentApi`) — also `/api/v1`, requires the `PetParentUser`
policy (separate Firebase project, currently `littersoftpetparent`).

### Pet-parent host (`Pawfront.PetParentApi`)

```
GET    /health
GET    /metadata                                                                 static reference vocabularies for mobile pickers → { animals: [{code, displayName}], behaviours: [{code, displayName}] }. Derived from the Pawfront.Domain.Vocabularies enums (Animal/Behaviour). Not ownership-filtered (needed during onboarding before a profile exists).

POST   /parent-onboarding/firebase-auth                                          body { fcmToken?, deviceId?, devicePlatform? } — upserts Parent.ParentAuthIdentities + optional Parent.ParentDeviceTokens (one parent → many FCM tokens). Reads identity claims from the Firebase JWT.
POST   /parent-onboarding/profile                                                body { firstName, lastName, gender, mobileCountryCode, mobileNumber, dateOfBirth, addressLine, latitude, longitude, zipCode, city, description? }. `description` ("About Me") is OPTIONAL — omit it or send null/blank and an empty string is stored (it also carries a C# default, so it stays out of the OpenAPI `required` list). `gender` accepts the picker's labels case-insensitively and ignoring spaces/hyphens — Male, Female, Others, Non Binary (also NonBinary / Other / PreferNotToSay) — normalised onto the canonical stored set { Male, Female, NonBinary, Other, PreferNotToSay } that CK_PetParents_Gender allows ("Others" stores as "Other"). **The owning auth identity is resolved server-side from the JWT sub/user_id claim** — the body intentionally has no parentAuthIdentityId field, so a caller cannot complete another parent's profile by guessing the id. Creates Parent.PetParents, flips ParentAuthIdentities.SignUpStatus → ParentProfileCompleted, back-fills PetParentId on device tokens. Idempotent (returns existing row if already linked). Sproc `Parent.CompletePetParentProfile` takes `@FirebaseUserId` and resolves the auth identity row under `UPDLOCK + HOLDLOCK`. **One account per mobile number:** the sproc pre-checks (MobileCountryCode, MobileNumber) under `UPDLOCK + HOLDLOCK` and THROWs 51222, with the UNIQUE index UX_PetParents_MobileNumber as the race-safe backstop → 409 MobileNumberAlreadyExists either way. 400 UnsupportedGender / InvalidRequest; 404 ParentAuthIdentityNotFound when no auth identity exists for the Firebase user (caller must hit `firebase-auth` first).
POST   /device-tokens                                                            body { fcmToken, deviceId?, devicePlatform? } — registers/refreshes the caller's FCM token. **An FCM token is not stable** (reinstall, cleared app data, restore, Firebase's own rotation) and the sign-in flow only runs at sign in, so the app must call this on every launch AND from Firebase's onTokenRefresh callback; without it a rotated token is never reported and the device silently stops receiving notifications. Owner resolved from the JWT (sub/user_id) → ParentAuthIdentityId, never from the body. **NOT ownership-filtered** — the token binds to the auth identity, which exists before the profile does, so a parent mid-onboarding can register (PetParentId is back-filled by profile completion). **Stale-token retirement:** when `deviceId` is supplied, every OTHER active token for that physical device is deactivated *including another account's*, so a reinstall retires the old token immediately (rather than waiting for FCM to report UNREGISTERED on the next send) and a device that changes hands stops receiving the previous account's notifications; omit `deviceId` and nothing is retired, since devices can't be told apart and guessing would kill the user's other phones. `devicePlatform` ∈ { Android, iOS } case-insensitive (400 InvalidRequest otherwise); blank/omitted stores null. Returns { deviceTokenId, ownerId (null pre-profile), deviceId, devicePlatform, isActive, retiredTokenCount, lastSeenAtUtc, createdAtUtc, updatedAtUtc } — the FCM token itself is deliberately NOT echoed back. Sproc `Parent.SaveParentDeviceToken` (THROW 51225). 404 ParentAuthIdentityNotFound when the caller hasn't hit `firebase-auth` yet.
POST   /device-tokens/deactivate                                                  body { fcmToken } — sign-out counterpart; flips IsActive = 0 so the handset stops receiving that account's notifications (matters most on a shared or resold device). Scoped to the caller's own auth identity, so a caller can't deactivate someone else's token even with a valid token string. The row is kept rather than deleted — FcmToken is UNIQUE, so signing back in reactivates it in place. Sproc `Parent.DeactivateParentDeviceToken` (THROW 51226). 404 DeviceTokenNotFound — "unknown" and "not yours" are the same case by design, so it can't be used to probe whether a token is registered. **POST, not DELETE:** minimal APIs refuse an inferred body on DELETE (startup throws "Body was inferred but the method does not allow inferred body parameters"), and both workarounds are worse — an FCM token in the URL lands in every access log, and DELETE bodies are stripped by some proxies, which would make sign-out fail silently. "Deactivate" is also the honest verb, since the row is never deleted.
GET    /parent-onboarding/me                                                     resolves the caller's Firebase uid (sub/user_id claim) → { parentAuthIdentityId, petParentId?, firebaseUserId, email, isEmailVerified, displayName, signUpStatus, hasProfile, mobileVerifiedAtUtc? }. Mirror of the provider host's `/provider-onboarding/me` — used by mobile after a reinstall (which wipes local storage but Firebase keeps the session) to recover the PetParentId. PetParentId / HasProfile / MobileVerifiedAtUtc are only populated once `POST /parent-onboarding/profile` has run. Backed by `Parent.GetPetParentByFirebaseUid` (LEFT JOIN PetParents). 404 ParentAuthIdentityNotFound when no auth identity exists yet for this Firebase user (caller must hit `firebase-auth` first).

DELETE /pet-parents/{petParentId}                                                "Delete account" = ANONYMISE + permanently DISABLE, NOT a row delete. Scrubs the personal fields on Parent.PetParents (name → "Deleted User", gender/DOB/mobile/address replaced, about + profile photo cleared), sets IsDeleted=1 + DeletedAtUtc, severs the Firebase auth identity (freeing the real uid AND mobile number for a fresh sign-up), anonymises the parent's Pets IN PLACE (name → "Deleted Pet", microchip/photo/notes cleared; type/breed/gender/DOB/weight/vaccination/sterilization kept so the provider's booking history keeps meaning), and deletes only operational data + media (device tokens, mobile OTPs, identity document, parent + pet photo galleries, next-consultations). **RETAINED untouched:** service + night-stay bookings with all their children, organised events + their ticket bookings, and the Booking.BookingPayments ledger — deleting them would destroy the PROVIDER's history too. One transaction via Parent.DeletePetParent (THROW 51223), then a best-effort blob sweep; no Cosmos leg (a parent owns no Cosmos doc). Orchestrated by IParentAccountService. Idempotent (second call returns the original deletedAtUtc with wasAlreadyDeleted: true). Returns { petParentId, deletedAtUtc, wasAlreadyDeleted, anonymisedPetCount, retained*Count… }. **409 PendingJobsExist** (2026-08-05) when the parent still has UNFINISHED jobs — any booking of either kind in a non-terminal status: a request the provider hasn't answered, a confirmed job still to come, one underway, or one with an open modification proposal. Nothing is scrubbed in that case; the response keeps the standard envelope with the list in `data`: { petParentId, pendingJobs: [{ bookingId, bookingType (SingleDay|NightStay), jobId, providerId, providerName, providerProfilePhotoUrl, serviceCategory, subCategory, status, serviceDate, checkOutDate (stays only), startTime, endTime, petName, serviceId, serviceItemCode, price: { pricePerUnit, priceUnit (PerHour|PerNight|PerService|PerAppointment|PerSession), totalAmount, pawfrontFee, feePercentage } }] }, soonest first. providerProfilePhotoUrl and price are filled by IPendingJobEnricher after the sproc returns — the photo lives in Cosmos and the money needs the fee percentage + a live-offering fallback, neither reachable from SQL; both are best-effort and degrade to nulls rather than failing the refusal. Same job shape as DELETE /pets/{petId}. No force override. The check is inside the sproc's transaction, under the UPDLOCK + HOLDLOCK it already holds on the parent row, so a booking created at the same instant serialises rather than slipping past. Ownership-filtered, so it can only ever delete the caller's own account. 404 PetParentNotFound. After it runs the caller's JWT no longer resolves → every /pet-parents/* route answers 403 ParentProfileNotCompleted, and PATCH /profile is additionally blocked by 409 ParentAccountDeleted.
GET    /pet-parents/{petParentId}/profile                                        full profile read-back: firstName, lastName, gender, email + isEmailVerified (JOINed from Parent.ParentAuthIdentities), mobileCountryCode/mobileNumber, dateOfBirth, addressLine, latitude, longitude, zipCode, city, description (About Me), profilePhotoUrl, mobileVerifiedAtUtc, timestamps. Backed by Parent.GetPetParentProfile (empty result → 404 PetParentNotFound). Ownership-filtered.
PATCH  /pet-parents/{petParentId}/profile                                        body { firstName, lastName, gender, dateOfBirth, addressLine, zipCode, city, description? } — edits the basic-profile subset via Parent.UpdatePetParentProfile (THROW 51208). Same optional-`description` + widened-`gender` rules as the create above. Deliberately NOT editable here: mobile number (must re-verify via OTP), latitude/longitude (no coordinates accompany an address edit — they go stale until a future geocoding pass), profile photo (own endpoint). Returns the same full read-back shape as the GET. 404 PetParentNotFound; 409 ParentAccountDeleted (THROW 51224 — the account was deleted; an edit would undo the anonymisation); 400 UnsupportedGender / InvalidRequest. Ownership-filtered.
POST   /pet-parents/{petParentId}/profile-image                                  multipart form-data { file }. Validations: file required, <=3 MB, content type ∈ { image/jpeg, image/png, image/webp }. Uploads to the shared blob container under the [PetParentProfilePhotos] folder and saves the resulting URL on Parent.PetParents.ProfilePhotoUrl via Parent.UpdatePetParentProfilePhoto. 400 InvalidFile / ImageTooLarge / UnsupportedImageFormat; 404 PetParentNotFound (sproc 51201).
POST   /pet-parents/{petParentId}/pets                                           body { petType, petName, breed, gender, dateOfBirth, weight, microchipId?, description? }. Inserts into Parent.Pets via Parent.AddPetParentPet. PetType ∈ {Dog, Cat, Hamster, GuineaPig}; Gender ∈ {Male, Female}; Weight DECIMAL(5,2) > 0. MicrochipId is globally UNIQUE (filtered) — collision returns 409 MicrochipIdAlreadyExists. 404 PetParentNotFound (sproc 51202); 400 UnsupportedPetType / UnsupportedPetGender / InvalidRequest. Response carries medical-info fields too — all null until PATCH below runs.
GET    /pet-parents/{petParentId}/pets                                           returns every pet on file for the parent with the full medical-info snapshot, embedded photo gallery, and nextConsultations [{ type: Groomer|Vet|Trainer, nextConsultation }] (written by the provider booking-complete flow). Backed by Parent.ListPetParentPets (three result sets: pets + photos + next-consultations, joined in C# by PetId). Photos within each pet are ordered oldest-first. Empty array when the parent has no pets (or doesn't exist) — list semantics, no 404. Distinct response type PetParentPetWithPhotosResponse so AddPet / PATCH medical-info responses stay unchanged.
GET    /pet-parents/{petParentId}/event-bookings                                 returns the caller's event-ticket bookings — slim summary cards with the joined event (title, category, eventType, start date/time, banner URL, and venue `eventLocation` for physical events — null for online) so the mobile "My Bookings" screen can render without a follow-up fetch. Backed by Event.ListEventBookingsByBookerEmail (SQL) + a per-booking Cosmos point read that hydrates the venue location (physical events only, fanned out in parallel; a failed read returns that card with a null location). **Booker identity on Event.EventBookings is free text (no FK to PetParents), so the filter matches on the caller's Firebase email claim** — the route's petParentId is verified by the ownership filter, then the JWT email is used as the SQL filter. Ordered most-recent first; cancelled bookings included. Mobile drills into GET /event-bookings/{bookingId} for the full shape with attendee names. 403 EmailClaimMissing when the JWT carries no email claim (rare).
GET    /pet-parents/{petParentId}/bookings                                       the parent's own SERVICE bookings ("my bookings"), most-recent first (BookingDate/StartTime DESC), cancelled included. Ownership-filtered (petParentId from JWT), so a caller only sees their own. [] when none — no 404. Backed by IBookingService.ListByPetParentAsync (sproc Booking.ListBookingsByPetParent) + IParentBookingEnrichmentService. Returns sectioned cards `ParentServiceBookingCardResponse` { booking, providerDetails, serviceDetails, cancellationPolicy, location, review } — cancellationPolicy + location added 2026-07-24, both read from the booking's frozen-at-creation snapshot (serviceDetails.pricePerHour likewise prefers the price-locked rate, live only as the legacy fallback). `serviceDetails.description` (2026-07-29) is the opposite case — the groomer menu item's blurb / the trainer's privateTrainingDescription, read LIVE on purpose (cosmetic copy, never price-locked), so a provider's later edit shows through; null for the other categories and when the offering can't be resolved. `location` address fields are null on legacy rows without a snapshot (the booking-DETAIL read is the live-fallback authority) and on Custom walk-ins. `review` (2026-08-09) is { canReview, rating } — the caller's OWN review state, read for the whole page in one call (sproc Review.ListPetParentBookingReviews). **canReview here is NOT the booking detail's canReview:** on a list the useful question is whether anything is outstanding, so it means "show the review prompt" and goes FALSE once the parent has reviewed — read the pair together: true/null = ask them, false/4 = they gave four stars, false/null = not reviewable. (The detail's stays true after a review exists, because a review can be edited; only the score travels here, comment + photos live on the detail.) Eligibility mirrors the SQL gate — COMPLETED or PAID, App booking. A failed ratings read leaves every card false/null rather than failing the list. (The provider host's GET /pet-parents/{petParentId}/bookings is unscoped there and keeps the flat BookingResponse shape.)
POST   /pet-parents/{petParentId}/bookings                                       body { petId, serviceId, bookingDate, startTime, endTime, serviceItemCode?, jobNotes?, locationType } — parent-initiated SERVICE booking. locationType is REQUIRED (ParentLocation | ProviderLocation → 400 InvalidRequest / UnsupportedLocationType); drives the detail read's `location` address block. ("book now" from a search result/slot). Booker = route petParentId (ownership-filtered; never from body). Provider resolved server-side from serviceId. petId must be one of the caller's pets (404 PetNotFound / 403 Forbidden inline; sproc re-checks via THROW 51068 → 400 InvalidPetId). Same shared IBookingService.CreateAsync + race-safe Booking.CreateBooking sproc as the provider host — full validation chain (working hours, closures → 409 ServiceClosed, duration rules, groomer serviceItemCode, capacity → 409 CapacityExceeded, 409 ProviderInactive, **booking lead time → 409 BookingLeadTimeTooShort** when the requested start is under 2 h away). Booking.Bookings now carries nullable PetId (FK → Parent.Pets), surfaced as petId on every booking read.
GET    /pet-parents/{petParentId}/bookings/summary                               [?period=Weekly|Monthly|Quarterly|Yearly|AllTime &from= &to= &petId= &status=] counts + spend for the parent's bookings. Returns { period, periodStart, periodEnd, totalBookings, singleDayBookings, nightStayBookings, completedBookings, upcomingBookings, cancelledBookings, paidBookings, awaitingPaymentBookings, unpricedBookings, amountSpent, upcomingAmount }. The three buckets are mutually exclusive and sum to totalBookings — declines, no-shows and expiries all count as cancelled, since from the parent's side they equally mean "it didn't happen". amountSpent covers COMPLETED **or** PAID: a job the provider finished but hasn't tapped "mark paid" on is included, because the parent already handed over the cash and has no control over that tap (awaitingPaymentBookings says how many). upcomingAmount is what confirmed future bookings will cost and is deliberately NOT part of amountSpent. `status` accepts a comma-separated mix of the friendly groups Completed / Upcoming / Cancelled and raw lifecycle statuses. Same filters — and the identical WHERE clause — as /history below, so the summary always describes exactly the set that list returns. Ownership-filtered. 400 InvalidRequest (bad period/status, or from > to).
GET    /pet-parents/{petParentId}/bookings/history                               [?period= &from= &to= &petId= &status= &sortBy=Date|Amount &sortDirection=Asc|Desc &skip= &take=] paginated history, single-day and night-stay merged into ONE feed (a parent thinks "my bookings", not "my two kinds of bookings"); bookingType discriminates and the other kind's fields are null. Unlike the provider earnings list this is NOT restricted to completed bookings — cancelled and upcoming ones belong in a history screen, each carrying its expected `amount`. Explicit from/to override period; take capped at 20; sort defaults to Date/Desc with a BookingId tie-break. Rows carry jobId, status, serviceDate, providerId + providerName (personal name from SQL — the business name lives in Cosmos, same as the booking-detail read), pet name + photo, isCompleted, isPaid, amount, paidAtUtc, paymentMethod. The Pawfront commission is deliberately absent: the parent pays `amount` either way and the split is the provider's concern. Ownership-filtered. 400 InvalidRequest. **Additive** — the existing unpaginated GET /pet-parents/{id}/bookings is unchanged.
POST   /pet-parents/{petParentId}/bookings/{bookingId}/status                    body { status, note? } — parent sets APPROVAL_NEEDED|COMPLETED|PARENT_CANCELLED|PROVIDER_NO_SHOW on their own booking; audited. Actor=Parent, actorId=route petParentId. 403 Forbidden (not the parent's booking), 400 BookingStatusNotAllowed, 409 BookingStatusTerminal|BookingStatusUnchanged. Shared Booking.UpdateBookingStatus sproc with the provider host.
POST   /pet-parents/{petParentId}/bookings/{bookingId}/no-show                   parent reports the PROVIDER never showed up → sets PROVIDER_NO_SHOW (terminal, frees capacity, audited). Allowed only from a confirmed-equivalent state and only 30+ minutes after the booking's scheduled start (BookingDate + StartTime, UTC). Reporting is optional: if nobody reports and the job is still unstarted at the end of the PROVIDER'S WORKING DAY on the booking date (their closing time from ProviderWeeklyAvailability, or the booking's own EndTime if that is later; midnight UTC when no hours are saved), the scheduled external job settles it automatically (START_JOB → PARENT_NO_SHOW, confirmed-equivalent → PROVIDER_NO_SHOW) — this used to be JOB_EXPIRED, and until 2026-08-02 fired at the booking's own end time. 404 BookingNotFound, 403 Forbidden, 409 BookingNotStartable (wrong from-state), 409 NoShowTooEarly (grace window not elapsed, THROW 51128), 409 BookingStatusTerminal.
GET    /pet-parents/{petParentId}/bookings/{bookingId}/terms-changes             terms-changes` — the provider's terms that changed since the booking was created (price, cancellation policy, drop-off/pick-up, selected-location address) plus rule-violation rows (fixed duration / minimum duration / minimum nights) checked against the booked window. Always 200 → { bookingId, hasChanges, changes: [{ field, changeType, bookedValue, currentValue, message }] }; hasChanges false = no confirmation sheet. Feeds `acknowledgeTermsChanges` on POST .../modifications (409 BookingTermsChanged without it once drifted). 404 BookingNotFound when the booking isn't the caller's. Ownership-filtered group.
GET    /pet-parents/{petParentId}/bookings/{bookingId}/status-history            full status audit trail, oldest-first (404 if not the parent's booking). Ownership-filtered group; bookingId re-checked against the booking's PetParentId.
POST   /pet-parents/{petParentId}/bookings/{bookingId}/review                    body { rating, comment? } — the parent's review of the provider for a finished booking. rating 1..5; comment optional, max 1000 CHARACTERS (blank/omitted stores nothing and reads back null). Reviewable = the booking is COMPLETED **or PAID** (PAID is downstream, so gating on COMPLETED alone would close the window the moment the provider recorded payment) and is an App booking. **Upsert:** resubmitting replaces rating + comment on the same review, so a corrected star doesn't duplicate — 201 on the first review, 200 on an edit. createdAtUtc does NOT move on an edit (it's the date given, which is what the list sorts on); updatedAtUtc does. The author is the route's petParentId (ownership-filtered, never from the body) and the gate runs inside Review.UpsertBookingReview, in the same transaction as the write. Photos are attached separately (below) because the blob path is keyed by the review id this call mints. 404 BookingNotFound; 403 Forbidden (not your booking); 409 BookingNotReviewable (not COMPLETED/PAID, THROW 51302); 400 ReviewNotAppBooking (Custom walk-in) / InvalidRequest (rating out of range, comment too long). Night-stay twin: POST /pet-parents/{petParentId}/night-stay-bookings/{bookingId}/review.
GET    /pet-parents/{petParentId}/bookings/{bookingId}/review                    the caller's own review of this booking with its photos. 404 ReviewNotFound when they haven't written one — the absence of a resource at this URL; the booking detail's `review` section is where it's reported as a nullable field instead. Night-stay twin under /night-stay-bookings/.
POST   /pet-parents/{petParentId}/bookings/{bookingId}/review/photos             multipart { file } — one photo per call (same as every other gallery flow here). <=3 MB, image/jpeg|png|webp. Uploads to review-photos/<bookingReviewId>/ then inserts the row. Max **5 photos per review**, enforced in SQL under UPDLOCK + HOLDLOCK (two uploads in flight would each see room) and pre-checked here so a caller at the cap isn't charged an upload first. 404 ReviewNotFound ("submit your review before attaching photos" — the review must exist to own the blob path); 409 ReviewPhotoLimitReached; 400 InvalidFile / ImageTooLarge / UnsupportedImageFormat. Night-stay twin under /night-stay-bookings/.
DELETE /pet-parents/{petParentId}/bookings/{bookingId}/review/photos/{photoId}   removes one photo (scoped by review + author) + best-effort blob delete. Returns { bookingReviewPhotoId, bookingReviewId, photoUrl, deletedAtUtc }. **The review itself is not deletable** — only its photos — so a rating is never stranded. 404 ReviewPhotoNotFound (unknown id, wrong review, and "not yours" are one case). Night-stay twin under /night-stay-bookings/.
GET    /providers/{providerId}/reviews                                           [?sortBy=Date|Rating &sortDirection=Asc|Desc &skip= &take=] the reviews parents have left for a provider — what a parent reads before booking. Returns { providerId, summary { reviewCount, averageRating, fiveStar..oneStar }, reviews: [{ bookingReviewId, bookingType, bookingId, jobId, petParentId, parentName, parentPhotoUrl, rating, comment, photoUrls, createdAtUtc, updatedAtUtc }], skip, take, hasMore }. bookingType discriminates because the two booking kinds share no id space — bookingId alone can't say which detail screen to open. **The summary is whole-population, not page-scoped**: it's the header's "4.6 (23)" and must not shift as the reader pages; averageRating is null (not 0.0) when nobody has reviewed. sortBy=Rating uses newest-first as its secondary, and both sorts carry a bookingReviewId tie-break so OFFSET paging can't repeat or skip. take caps at 20. parentName/photo are joined LIVE, so a deleted account reads "Deleted User". NOT ownership-filtered — it's someone else's public profile data, same posture as the rest of /providers/*. 400 InvalidRequest on an unknown sortBy/sortDirection. Mirrored on the provider host.
POST   /pet-parents/{petParentId}/night-stay-bookings                            body { petId, serviceId, checkInDate, checkOutDate, jobNotes?, locationType } — multi-night boarding booking (PetSitter NightStay only). jobNotes is optional free text (returned on the detail read); locationType is REQUIRED (ParentLocation | ProviderLocation → 400 InvalidRequest / UnsupportedLocationType). Distinct entity from single-day bookings: the stay spans [checkInDate, checkOutDate) — checkOutDate is the pickup day, NOT a stayed night. Booker = route petParentId (ownership-filtered). Provider resolved server-side from serviceId; petId must be one of the caller's pets. Per-night capacity is race-safe (Booking.CreateNightStayBooking). DropOff/PickUp times snapshotted from the offering. Max 30 nights. Errors: 404 PetNotFound / 403 Forbidden (pet), 400 InvalidServiceId / NotNightStayService / OfferingNotConfigured / InvalidPetId / InvalidNightStayDates, 404 ProviderNotFound / PetParentNotFound, 409 ProviderInactive / CapacityExceeded / ServiceClosed / **BookingLeadTimeTooShort** (drop-off on the check-in day is under 2 h away).
GET    /pet-parents/{petParentId}/night-stay-bookings                            the parent's own night-stay bookings, most-recent first (CheckInDate DESC), cancelled included. Ownership-filtered. [] when none. Returns sectioned cards `ParentNightStayBookingCardResponse` { booking, providerDetails, serviceDetails, cancellationPolicy, location, review } — cancellationPolicy + location added 2026-07-24, from the frozen-at-creation snapshot (serviceDetails.pricePerNight likewise prefers the price-locked rate, live only as the legacy fallback). `location` address fields are null on legacy rows without a snapshot. `review` (2026-08-09) is { canReview, rating }, identical in meaning to the single-day list's — see there.
GET    /pet-parents/{petParentId}/night-stay-bookings/{bookingId}                single night-stay booking (404 NightStayBookingNotFound if unknown or not the caller's). Ownership-filtered.
POST   /pet-parents/{petParentId}/night-stay-bookings/{bookingId}/status         body { status, note? } — parent sets APPROVAL_NEEDED|COMPLETED|PARENT_CANCELLED|PROVIDER_NO_SHOW (cancel is done here, mirroring single-day). Actor=Parent. 404 NightStayBookingNotFound, 403 Forbidden, 400 BookingStatusNotAllowed, 409 BookingStatusTerminal|BookingStatusUnchanged.
POST   /pet-parents/{petParentId}/night-stay-bookings/{bookingId}/no-show        parent reports the PROVIDER never showed up for the stay → sets PROVIDER_NO_SHOW (terminal, frees remaining per-night capacity, audited). Allowed from a confirmed-equivalent state or START_JOB, and only **2+ HOURS** after CheckInDate + DropOffTime (UTC) — night-stay's own grace window, NOT the single-day 30 minutes (a 09:00 check-in is reportable from 11:00). Reporting is optional: if nobody reports and the stay is still unstarted at midnight UTC on the check-in day, the scheduled external job settles it automatically (START_JOB → PARENT_NO_SHOW, confirmed-equivalent → PROVIDER_NO_SHOW). 404 NightStayBookingNotFound, 403 Forbidden, 409 BookingNotStartable, 409 NoShowTooEarly (THROW 51248), 409 BookingStatusTerminal.
GET    /pet-parents/{petParentId}/night-stay-bookings/{bookingId}/terms-changes  terms-changes` — the provider's terms that changed since the booking was created (price, cancellation policy, drop-off/pick-up, selected-location address) plus rule-violation rows (fixed duration / minimum duration / minimum nights) checked against the booked window. Always 200 → { bookingId, hasChanges, changes: [{ field, changeType, bookedValue, currentValue, message }] }; hasChanges false = no confirmation sheet. Feeds `acknowledgeTermsChanges` on POST .../modifications (409 BookingTermsChanged without it once drifted). 404 NightStayBookingNotFound when the stay isn't the caller's.
GET    /pet-parents/{petParentId}/night-stay-bookings/{bookingId}/status-history full status audit trail, oldest-first (404 if not the parent's booking).
GET    /pets/{petId}                                                             single pet profile — full basic-info + medical-info snapshot + embedded photo gallery (oldest-first), the same PetParentPetWithPhotosResponse shape as the list endpoint. Backed by Parent.GetPetParentPet (two result sets: pet + photos). Ownership-filtered (RequireOwnedPet → 404 PetNotFound for unknown pet, 403 Forbidden for someone else's). The handler also maps an empty result set to 404 defensively.
PATCH  /pets/{petId}                                                             body { petType, petName, breed, gender, dateOfBirth, weight, microchipId?, description? } — same shape as AddPet. Updates the basic-info subset via Parent.UpdatePetParentPet; medical-info columns are deliberately untouched (use PATCH /medical-info). Same validations and error map as AddPet: 404 PetNotFound (sproc 51205); 409 MicrochipIdAlreadyExists; 400 UnsupportedPetType / UnsupportedPetGender / InvalidRequest.
PATCH  /pets/{petId}/medical-info                                                body { vaccinationStatus, sterilizationStatus, medicalHistory?, temperament?, vaccinationType?, vaccinationDose?, prescription? }. Fills in medical fields on an existing pet via Parent.UpdatePetMedicalInfo. VaccinationStatus ∈ {Vaccinated, NotVaccinated}; SterilizationStatus ∈ {Sterilized, Intact}; Temperament ∈ {Anxious, Friendly, Aggressive} but OPTIONAL — omit/empty to store null (a pet can be added without a known temperament); MedicalHistory, VaccinationType, VaccinationDose, and Prescription are free text and nullable (surfaced on every pet read AND on the booking-detail petDetails section). 404 PetNotFound (sproc 51203); 400 UnsupportedVaccinationStatus / UnsupportedSterilizationStatus / UnsupportedTemperament (only when a non-empty invalid value is sent) / InvalidRequest.
DELETE /pets/{petId}                                                             SOFT-deletes the pet via Parent.DeletePetParentPet (THROW 51214 → 404 PetNotFound) — an ANONYMISE + HIDE, not a row delete, the same shape as the two account deletes. **The row survives on purpose:** Booking.Bookings.PetId references it and Booking.GetBookingDetail resolves the whole petDetails block through that join, so removing it (as this endpoint used to, NULLing PetId on every booking that referenced the pet) silently erased the pet from the PROVIDER's record of a job they actually performed. What goes: name → "Deleted Pet", MicrochipId → NULL (a real-world UNIQUE identifier — clearing it frees the chip for re-registration), profile photo, description and the free-text medical fields; plus the photo gallery (Parent.PetPhotos) and next-consultation rows outright. What stays: type, breed, gender, DOB, weight and the vaccination / sterilization / temperament statuses — the facts that keep a past booking meaningful. IsDeleted = 1 then hides the pet everywhere that matters: the ownership reader treats it as missing (so every /pets/{petId}/* route 404s, on BOTH hosts), it drops out of GET /pet-parents/{id}/pets, the onboarding-status pet stages, the discovery/search petId filter, and both booking creates (THROW 51068 / 51233). Booking READS deliberately do NOT filter on it — that history is why the row is kept, and the provider sees "Deleted Pet". Photo blobs are left for a future sweep. Returns { petId, petParentId, deletedAtUtc, wasAlreadyDeleted }. **409 PendingJobsExist** (2026-08-09) when THIS PET still has unfinished jobs — any booking of either kind naming it in a non-terminal status. Nothing is scrubbed; the response keeps the standard envelope with { petId, pendingJobs: [...] } in `data`, the identical job shape the account delete returns (see "The pending-job shape"), soonest first. Same rule and reasoning as the account delete: a provider is holding a slot for this animal, or has it in their care right now, and anonymising it out from under them is not something the parent can undo. Decided inside the sproc's transaction under the UPDLOCK + HOLDLOCK it already holds on the pet row — the row a concurrent Booking.CreateBooking must read — so a booking landing at the same instant serialises rather than slipping past. No force override. Ownership-filtered (RequireOwnedPet → 404 PetNotFound for unknown pet, 403 Forbidden for someone else's) — which also means a second DELETE 404s rather than reporting wasAlreadyDeleted; the sproc's idempotency is a safety property for direct callers and races.
POST   /pets/{petId}/profile-image                                               multipart form-data { file }. The pet's SINGLE primary/profile photo (distinct from the gallery below). Same validations as the gallery upload (file required, <=3 MB, image/jpeg|png|webp). Uploads to the [PetProfilePhotos] folder ("pet-profile-photos/<petId>/<guid>.<ext>") and stores the URL on Parent.Pets.ProfilePhotoUrl via Parent.UpdatePetProfilePhoto. Returns { petId, profilePhotoUrl, updatedAtUtc }. Mirror of the parent-profile-photo endpoint. 400 InvalidFile / ImageTooLarge / UnsupportedImageFormat; 404 PetNotFound (sproc 51220). Surfaced as profilePhotoUrl on every pet read (GET/list, add, patch). Ownership-filtered.
POST   /pets/{petId}/photos                                                      multipart form-data { file }. Same per-file validations as the profile-photo endpoint: file required, <=3 MB, content type ∈ { image/jpeg, image/png, image/webp }. Uploads to the shared blob container under the [PetPhotos] folder ("pet-photos/<petId>/<guid>.<ext>") and inserts a row into Parent.PetPhotos via Parent.AddPetPhoto. One row per upload — a pet can have many photos (client makes N calls for N photos). 400 InvalidFile / ImageTooLarge / UnsupportedImageFormat; 404 PetNotFound (sproc 51204). Parent.PetPhotos.PetId has ON DELETE CASCADE so deleting a pet removes its photo rows (blobs not cleaned up — future job).
DELETE /pets/{petId}/photos/{photoId}                                            removes one photo from a pet's gallery (scoped by PetId + PetPhotoId) via Parent.DeletePetPhoto + best-effort blob delete (the SQL row is the source of truth; a storage failure is swallowed). Returns { petPhotoId, petId, photoUrl, deletedAtUtc }. 404 PetPhotoNotFound (sproc 51215). Ownership-filtered (RequireOwnedPet → 404 PetNotFound / 403 Forbidden on the pet).

POST   /pet-parents/{petParentId}/identity                                       multipart form-data { file, identityType }. Validations: file required, <=3 MB, content-type ∈ { image/jpeg, image/png, image/webp }; identityType ∈ { Passport, DriverLicense, NationalId, ResidencePermit }. Uploads to the shared blob container under [PetParentIdentities] folder ("pet-parent-identities/<petParentId>/<guid>.<ext>") and upserts a row in Parent.ParentIdentities via Parent.UpsertPetParentIdentity (one identity per parent — re-uploading replaces). 400 InvalidFile / ImageTooLarge / UnsupportedImageFormat / UnsupportedIdentityType / InvalidRequest; 404 PetParentNotFound (sproc 51206).
GET    /pet-parents/{petParentId}/identity                                       reads the parent's single identity row (one per parent) with complete details + the document blob URL. Returns { parentIdentityId, petParentId, identityType, identityPhotoUrl, createdAtUtc, updatedAtUtc }. Backed by Parent.GetPetParentIdentity (empty result → 404 ParentIdentityNotFound, since the ownership filter already guarantees the parent exists). Fetch the document bytes via POST /blob-images with the identityPhotoUrl. Ownership-filtered.
DELETE /pet-parents/{petParentId}/identity                                       removes the parent's single identity row via Parent.DeletePetParentIdentity (THROW 51209 → 404 ParentIdentityNotFound) AND best-effort deletes the blob itself (identity docs are sensitive — first real use of the new IPawfrontBlobStorage.DeleteAsync; a storage failure is swallowed, the SQL row is the source of truth). Returns { parentIdentityId, petParentId, identityType, identityPhotoUrl, deletedAtUtc }. Onboarding-status identity stage reverts to Remaining. Ownership-filtered.
POST   /pet-parents/{petParentId}/photos                                         (multipart { file }) general pet-parent photo gallery — uploads to [PetParentPhotos] blob folder, inserts a row in Parent.PetParentPhotos { PetParentPhotoId, PetParentId, PhotoUrl, CreatedAtUtc }. <=3 MB, JPEG/PNG/WebP. 404 PetParentNotFound (sproc 51212); 400 InvalidFile/ImageTooLarge/UnsupportedImageFormat. Ownership-filtered.
GET    /pet-parents/{petParentId}/photos                                         list the parent's gallery photos, oldest-first ([] when none). Ownership-filtered.
DELETE /pet-parents/{petParentId}/photos/{photoId}                               removes one photo (scoped by PetParentId + PhotoId) via Parent.DeletePetParentPhoto + best-effort blob delete. 404 PetParentPhotoNotFound (sproc 51213). Ownership-filtered.
GET    /pet-parents/{petParentId}/onboarding-status                              orchestrator over a single sproc (Parent.GetPetParentOnboardingStatus, three result sets). Returns { basicInfo, profilePhoto, pets, petMedicalInfo, identity, verification, isFullyOnboarded }. basicInfo is a sentinel (always Complete when the endpoint resolves). profilePhoto/pets/petMedicalInfo/identity each carry { status: Complete|Remaining }. petMedicalInfo also lists per-pet `{ petId, petName, isMedicalInfoComplete }` (2-field check: vaccination + sterilization; temperament and medical-history are optional and do NOT gate completion). identity also carries `identityType` (null when Remaining). verification = { isEmailVerified, isMobileVerified }. isFullyOnboarded = basicInfo + pets + petMedicalInfo + identity + emailVerified + mobileVerified (profilePhoto is informational, NOT gating). 404 PetParentNotFound when the parent row is missing.

POST   /pet-parents/{petParentId}/mobile-verification/otp                        generates a 6-digit OTP, stores SHA-256 hash + last-2-digits hint with 10-minute expiry in Parent.ParentMobileOtps via Parent.CreateMobileVerificationOtp, dispatches the raw code via IPetParentMobileOtpSender (NoOp today — real SMS provider TBD). Returns { parentMobileOtpId, petParentId, mobileCountryCode, mobileNumber, dateSentUtc, expiresAtUtc }. 404 PetParentNotFound (sproc 51210).
POST   /pet-parents/{petParentId}/mobile-verification/otp/{otpId}/verify         body { otpCode }. ALWAYS returns 200 — client branches on { isValidated, validationStatus: Validated|Invalid|Expired|Pending }. On the first successful verification, Parent.PetParents.MobileVerifiedAtUtc is set (COALESCE, so re-verification doesn't bump it). 404 ParentMobileOtpNotFound (sproc 51211); 400 InvalidRequest for empty otpCode.

GET    /events                                                                   [?eventCategory= &eventType= &startDate= &endDate= &isChildFriendly= &amenities=... &title=] — same provider-agnostic catalog listing as the provider host; duplicated on the parent host because the two hosts authenticate against different Firebase projects. Backed by the shared IEventService — no new business logic. `title` is an optional case-insensitive "contains" search on the event title (LIKE %term%, LIKE-metacharacters escaped). Each event hydrates Cosmos physical details + carries pawPrints, bookings { maxBookings, totalBookings }, and isBookable (false once the caller already holds non-cancelled tickets, matched by JWT email).
GET    /events/trending                                                          [?take=] top-N trending events, most engaging first. Trending score = ViewCount + ShareCount + non-cancelled (Confirmed) ticket bookings. take defaults to 20, clamped 1..100 by the sproc. Same provider-agnostic shape + Cosmos hydration + isBookable (JWT email) as GET /events; no filters. Backed by IEventService.ListTrendingAsync → Event.ListTrendingEvents (same two result sets as Event.ListEvents). Mirror of the provider host.
GET    /events/{eventId}                                                         single event detail (SQL + Cosmos for physical). Includes pawPrints { viewCount, shareCount, inquiryCount }, bookings { maxBookings, totalBookings }, isBookable (false once the caller already holds non-cancelled tickets), paymentOptions [Cash|Digital], attendees [{ attendeeName, ticketNumber }] (names only), eventLink (online joining URL; null for physical).
POST   /events/{eventId}/views                                                   public engagement counter (parent host copy).
POST   /events/{eventId}/shares                                                  public engagement counter (parent host copy).
POST   /events/{eventId}/inquiries                                               public engagement counter (parent host copy).
POST   /events/{eventId}/payout-methods                                          body { payoutMethods: ["Cash"|"Digital", ...] } — organiser payout method(s) for ticket proceeds (parent host copy). 404 EventNotFound; 400 FreeEventNoPayout (free event) / InvalidRequest.

POST   /events/{eventId}/bookings                                                body { bookerName, bookerEmail, bookerMobile?, attendeeNames[], paymentMethod }. Same shared IEventBookingService as the provider host; parent app uses its own auth. Physical events accept any number of tickets (capacity-gated); online events accept exactly ONE ticket per booking. 404 EventNotFound; 400 OnlineEventSingleTicket (>1 attendee on an online event); 409 EventSoldOut on capacity exhaustion (physical only).
GET    /event-bookings/{bookingId}                                               booking + one entry per ticket (parent host copy).
DELETE /event-bookings/{bookingId}                                               soft-cancels the caller's own booking (booker matched by JWT email claim) → flips Status to Cancelled + frees the seat capacity; returns the cancelled booking + tickets. 403 EmailClaimMissing; 404 EventBookingNotFound (unknown or not the caller's); 409 EventBookingAlreadyCancelled.

POST   /pet-parents/{petParentId}/events/banner-image                            (multipart) → { url }. Ownership-filtered. Uploads to the shared blob container under [EventBanners] folder. Mirror of the provider banner upload (no 1 MB cap — banners can be larger than profile photos).
POST   /pet-parents/{petParentId}/events                                         body identical to the provider create-event (8 SQL fields + physical:{maximumCapacity,isPaid,price?}). Ownership-filtered. Creates a parent-organised event via Event.CreatePetParentEvent — row goes into Event.Events with PetParentId set, ProviderId NULL. Physical events also write the Cosmos extension doc. Returns the EventResponse (now carries nullable providerId + nullable petParentId; exactly one set). 404 PetParentNotFound (sproc 51207); 400 InvalidRequest.
PUT    /pet-parents/{petParentId}/events/{eventId}                              full-replace edit of a parent-organised event (every field). Ownership-filtered; sproc Event.UpdatePetParentEvent re-checks the event belongs to the parent (THROW 51217 → 404 EventNotFound). Body identical to create. Reconciles the Cosmos physical doc; returns full detail (incl. paymentOptions + attendees). 400 InvalidRequest on validation.
PATCH  /pet-parents/{petParentId}/events/{eventId}                              partial edit — body fields are all Optional<T>; only supplied fields change (explicit null clears, e.g. bannerImageUrl). Reads current event, merges, reuses the same full-replace update path (full re-validation). Ownership-filtered. 404 EventNotFound; 400 InvalidRequest.
GET    /pet-parents/{petParentId}/events                                         lists the events this parent has organised (the parent-host mirror of GET /providers/{providerId}/events). Ownership-filtered. Backed by Event.ListEventsByPetParent; now hydrates Cosmos physical details per event. Each event includes pawPrints { viewCount, shareCount, inquiryCount } + bookings { maxBookings, totalBookings }. Ordered StartDate/StartTime DESC; [] when the parent has organised none.

GET    /providers                                                                [?petId= &providerType= &date= &startTime= &endTime= &city= &serviceLocation= &skip= &take=] — parent-facing provider discovery / booking search. ALL filters optional and combinable. petId: must belong to the caller (ownership enforced inline — 403 Forbidden / 403 ParentProfileNotCompleted / 404 PetNotFound, same codes as OwnedPetFilter); the pet's PetType becomes the animal filter (replaces the old raw ?animals= param). providerType ∈ { PetSitter, PetGroomer, PetTrainer, Vet } — 400 UnsupportedProviderType otherwise (PetAdoptionAndSale is NOT a valid filter value, but unfiltered browsing still includes it). date+startTime+endTime travel as a trio (400 InvalidRequest if partial, or startTime >= endTime); when set, each candidate runs through IProviderWindowAvailabilityChecker — a full free-slot check (working hours, closures, confirmed bookings vs capacity via the shared slot service). Window semantics: fixed-duration services (TrainingSession/VetAppointment/grooming item) match when a free slot of that duration fits anywhere inside the window; min-duration services (DayCare/NightStay) match when the FULL window is bookable and >= the offering minimum; PetGroomer probes the shortest active menu item that fits (capacity is shop-wide, so that's sufficient). Pagination applies AFTER availability filtering when a window is set. city: case-insensitive exact match on the Cosmos doc's City. serviceLocation ∈ { ParentsPlace, ProvidersPlace } (400 UnsupportedServiceLocation) — mapped per category onto the offering's stored values (CustomerPlace/CustomerLocation vs PetHotel/GroomerShop/VetClinic/TrainerLocation/TrainingSchool; stored "Both" matches either; trainer's NatureOrParks/UrbanOrCity count as neither). PetAdoptionAndSale providers are excluded when an animal OR serviceLocation filter is set (no offering = no data). Returns slim summary cards: { providerId, serviceCategory, subCategory, displayName (business name; null for freelancers), imageUrl, city, about (Description/AboutYou), animalsHandled }. Take defaults to 50, max 200. **Inactive providers are excluded** (2026-08-05) — see the note under the five searches. Backed by IProviderDiscoveryService → ActiveOnlyProviderDiscoveryService wrapping CosmosProviderDiscoveryService (static filters; Cosmos only, in-memory predicates because the relevant paths vary per category) + IProviderWindowAvailabilityChecker (Application orchestrator over IProviderServiceCatalog + IProviderOfferingResolver + IProviderAvailabilitySlotService + IPetGroomerServiceRegistry). BREAKING: the old ?serviceCategory= and ?animals= params were removed.
GET    /providers/search/day-care                                                 [?petId= &date= &startTime= &endTime= &city= &serviceLocation= &skip= &take=] — per-service booking search #1 (PetSitter/DayCare). All filters optional. date+startTime+endTime trio; when set, a provider matches only if the FULL window is bookable on the DayCare service (window >= offering minimum, exact-start free slot, capacity/closures/bookings respected). Charges = PricePerHour.
GET    /providers/search/night-stay                                               [?petId= &startDate= &pickupDate= &city= &serviceLocation= &skip= &take=] — per-service booking search #2 (PetSitter/NightStay). startDate+pickupDate pair (400 if partial, startDate >= pickupDate, or span > 30 nights); pickupDate is checkout, NOT a stayed night. Provider matches only if EVERY night from startDate to pickupDate-1 has free NightStay capacity. Charges = PricePerHour.
GET    /providers/search/groomers                                                 [?petId= &date= &serviceItemCode= &city= &serviceLocation= &skip= &take=] — per-service booking search #3 (PetGroomer/GroomingSession). serviceItemCode validated against the canonical 18-code catalog (400 UnsupportedServiceItemCode); when set, only providers with that item ACTIVE match and charges = the item's price (chargesUnit PerService); when omitted, any groomer with >=1 active menu item matches and charges is null. With a code the card also carries that item's `description` (the groomer's blurb, null when they left it blank); without one it is null, since no single item is being described. date → any free slot of the item's duration that day (no code → probes the shortest active item; capacity is shop-wide so that suffices).
GET    /providers/search/vets                                                     [?petId= &date= &city= &serviceLocation= &skip= &take=] — per-service booking search #4 (Vet/VetAppointment). date → any free appointment slot that day. Charges = PricePerAppointment (chargesUnit PerAppointment).
GET    /providers/search/trainers                                                 [?petId= &date= &city= &serviceLocation= &skip= &take=] — per-service booking search #5 (PetTrainer/TrainingSession). Mirror of the vets search: a training session is a single fixed-duration booking, so date → any free slot of the session's duration that day. Charges = PricePerSession (chargesUnit PerSession). The card's `description` is the offering's privateTrainingDescription — the trainer's equivalent of a groomer's per-item blurb.

> All five searches: petId is ownership-enforced inline (same codes as OwnedPetFilter) and the pet's PetType becomes the animal filter; city is case-insensitive; serviceLocation ∈ { ParentsPlace, ProvidersPlace }. A provider must have the matching ACTIVE ProviderServices row + configured offering to appear at all — **and, since 2026-08-05, must be ACTIVE at the provider level too** (Provider.Providers.IsActive = 1 AND IsDeleted = 0). That flag lives in SQL while discovery reads Cosmos, so it could not be pushed into the query; it is applied by the ActiveOnlyProviderDiscoveryService decorator that wraps IProviderDiscoveryService — ONE place, so GET /providers, all five searches, and anything added later inherit it. Without it the parent apps listed providers who had explicitly switched themselves off, and every booking attempt against them failed with 409 ProviderInactive. Paging happens AFTER the filter (the decorator asks the inner reader for everything and pages itself; free, since the Cosmos reader already fetches the whole category partition), otherwise an inactive provider inside a page would leave a hole. GetSummaryAsync — the point read behind a booking's providerDetails — is deliberately NOT filtered: a parent with an existing booking must still see who it is with. Backed by the batched IProviderActiveStatusReader (SqlProviderActiveStatusReader; NullProviderActiveStatusReader reports everything active in the in-memory dev config, which has no IsActive concept). Note GET /providers/{providerId} (the public profile) is likewise unfiltered — an inactive provider is still viewable by deep link, they just can't be booked. Response per hit: { providerId, serviceId, subCategory, businessName (business name for shops; the provider's personal name for freelancers), completedBookings, charges, chargesUnit (PerHour|PerService|PerAppointment|PerSession), serviceItemCode, description, imageUrl, bannerImageUrl }. description (2026-07-29) = what the provider says about the searched service — the menu item's blurb for a groomers search WITH a serviceItemCode, the offering's privateTrainingDescription for trainers; null on the other three searches and on a code-less groomers search (no per-service text / no single item). imageUrl = the service image the provider uploaded for this offering (the same image the discovery card shows — sourced from the Cosmos offering's imageUrl via the shared ProviderSummary; null when unset). bannerImageUrl = the wide banner the provider uploaded for THIS service via POST /providers/{id}/services/{serviceId}/banner-image (Provider.ProviderServiceBanners, keyed by ServiceId; batch-hydrated per paged result via IProviderServiceBannerService.GetByServiceIdsAsync), **falling back to the provider-level banner** (Provider.Providers.BannerImageUrl, set at registration via POST /providers/{id}/banner-image, batch-hydrated via IProviderBannerImageService.GetByProviderIdsAsync) when no per-service banner is set; null when neither is set. completedBookings = bookings that are explicitly COMPLETED OR whose window has elapsed (non-cancelled/non-no-show/non-expired) across ALL the provider's services, any category incl. freelance (SqlProviderBookingStatsReader, batched — UNIONs single-day `Booking.Bookings` and multi-night `Booking.NightStayBookings`). The explicit-COMPLETED arm was added 2026-07-19 — without it a future-dated booking already marked COMPLETED (e.g. a freelance vet appointment) was undercounted to 0; the night-stay UNION was added the same day — without it a PetSitter whose only completed jobs are boarding stays showed 0 (night-stay "window elapsed" = `CheckOutDate < today`, since checkout day isn't a stayed night). Pagination applies after availability filtering. Backed by IProviderSearchService → ProviderSearchService (Application orchestrator over IProviderDiscoveryService + IProviderServiceCatalog + IProviderOfferingResolver + IProviderAvailabilitySlotService + IPetGroomerServiceRegistry + IProviderBookingStatsReader + IProviderServiceBannerService). OfferingResolution.Resolved now carries Price (PricePerHour / PerSession / PerAppointment; null for grooming).

GET    /providers/{providerId}                                                   parent-facing provider profile. Composes the registration row (category, sub-category, lat/lng), the category-specific offering (one of petSitter / petGroomer / petTrainer / petAdoptionSale / vet — exactly one populated), workingHours (7 days), timeOff (future closures across all the provider's services), the advertised booking policy (minimumHoursBeforeCancellation: null|24|48|72|96, and acceptedPaymentMethods: ["Cash"|"Digital", ...] — the provider's payout-method set, empty when unset), completedBookings (bookings explicitly COMPLETED or already served — window ended — not cancelled/no-show/expired, across all the provider's services regardless of category/freelance, spanning both single-day and multi-night boarding stays; same IProviderBookingStatsReader figure as the search cards), top-level `description` (the freelancer's about-you text; null for business sub-categories) + `servicesDescription` (the business branch's description — shop/hotel/clinic/school/shelter; null for freelancers) (2026-07-17; both lifted from the category offering so mobile doesn't dig into the nested block), `serviceDescription` (2026-07-29; the bookable SERVICE's own description, lifted the same way — **PetTrainer only**, from its offering's privateTrainingDescription, for both sub-categories. Null elsewhere: a groomer's blurbs are per menu item, inside petGroomer…offering.session.services[].description, and the other three categories have no per-service text), `bannerImageUrl` (2026-07-25; the provider-level banner from Provider.Providers.BannerImageUrl — null until the provider uploads one), top-level `email` + `mobileCountryCode` + `mobileNumber` (2026-07-27 — see below), `reviewSummary` (2026-08-08; { reviewCount, averageRating, fiveStar..oneStar } over EVERY review the provider has received — averageRating is null, not 0.0, when nobody has reviewed them), and `reviews` (the newest **10**, a PREVIEW for this screen — the full sorted/paged list is GET /providers/{providerId}/reviews). Provider personal info (name, DOB) intentionally omitted — parents see business-facing data only. Backed by IProviderPublicProfileService, which fans out to the existing per-category registries, IProviderAvailabilityService, IProviderClosureService, IProviderPolicyService (cancellation + payout), IProviderBannerImageService, IProviderBookingStatsReader, IProviderContactReader, and IBookingReviewService. 404 ProviderNotRegistered when no service registration row exists.

> **Contact block (2026-07-27).** `email` / `mobileCountryCode` / `mobileNumber` sit at the top level, lifted the same way `description` / `servicesDescription` were, so mobile doesn't have to dig into the category branch — where a FREELANCER has nothing to find. Business sub-categories (hotel/shop/school/clinic/shelter) report the email + telephone captured on their registration; freelancers register neither (they ARE the business), so theirs falls back to the provider's own account — `ProviderAuthIdentities.Email` + the verified `Provider.Providers` mobile, read by the new narrow `IProviderContactReader` (`SqlProviderContactReader`, one indexed point read; `NullProviderContactReader` in the in-memory dev config). Since telephone became optional on business registration, a business that left it blank falls back the same way (blank counts as not-supplied, not as a value). Null only when neither source has anything.
GET    /providers/{providerId}/agenda                                            ?serviceId= &date= — the provider's day for ONE service, laid out as a contiguous timeline of blocks so the parent can see the shape of the day and pick a slot. All three params required. Needs NO duration (unlike /availability/slots), which is the point: the parent browses first, then asks for slots once they know what they want. Returns { providerId, serviceId, date, serviceCategory, subCategory, serviceType, capacity, isOpen, isClosedForDay, openingTime, closingTime, entries: [{ startTime, endTime, entryType, status, jobId, bookingId, remainingCapacity, isBookable }] }. `entries` covers the working hours only, never overlaps, and merges neighbours that read alike (an untouched morning is ONE block). entryType ∈ Free | Booked | Break | Closed — branch on this, not on `status`. **Other parents' jobs are masked:** `status` carries the real lifecycle status (CONFIRMED, IN_PROGRESS, …) + jobId "PF-000123" + bookingId ONLY for a booking belonging to the caller (PetParentId resolved from the JWT, never the route); anyone else's reads `status: "BOOKED"` with null ids, and a Custom walk-in (no PetParentId) always masks. remainingCapacity = capacity − overlapping active bookings, the same overlap count the race-safe create sproc uses — so a Booked block with capacity left is still bookable (isBookable). isOpen false (entries empty) when the weekday is closed or an all-day closure covers the date; isClosedForDay distinguishes "away" from "not a working day". NightStay is date-granular: the whole day is ONE block, openingTime/closingTime null (a stay is booked against nights, not clock time — mirrors the create path). Backed by IProviderDailyAgendaService (Application) over the same offering/weekly-hours/closure readers as the slot service + the new IDailyAgendaReader → sproc Booking.GetAgendaForDate. 400 InvalidServiceId / OfferingNotConfigured / InvalidRequest; 404 ServiceNotRegistered. Not ownership-filtered (it's someone else's calendar) — same posture as the rest of /providers/*. Not mirrored on the provider host, which already has GET /providers/{id}/bookings?date= for its own day view.
GET    /providers/{providerId}/availability/slots                                ?serviceId= &date= [&durationHours= | &serviceItemCode=] [&granularityMinutes=] — parent-facing free-slot query (mirror of the provider host). Backed by the shared IProviderAvailabilitySlotService. PetGroomer uses ?serviceItemCode= (duration resolved server-side from the menu item); other categories use ?durationHours=. Closures, capacity, and overlapping confirmed bookings are subtracted per-service. Same error map as provider host (InvalidServiceId, ServiceNotRegistered, OfferingNotConfigured, ServiceItemCodeRequired/NotOffered/Inactive, InvalidBookingDuration, InvalidRequest). Save/get weekly-hours endpoints on the same group (`POST /` and `GET /`) are intentionally **NOT** mirrored on the parent host — those are organiser-only; the 7-day shape is already returned by GET /providers/{providerId} under workingHours.
POST   /blob-images                                                              body: { blobUrl } — streams bytes from the private blob container (mirror of the provider host's universal fetch endpoint; duplicated because the hosts use different Firebase projects). 404 BlobNotFound; 400 InvalidRequest.
```

> Note: the gateway webhook `POST /event-bookings/{bookingId}/payment-confirmation` is intentionally **not** mirrored on the parent host — already reachable on the provider host, and a Firebase-JWT-gated webhook only needs one entry point.

### Chat host (`Pawfront.ChatApi`)

Third host, and the only one that accepts **both** Firebase projects — a
conversation spans a provider and a pet parent, and one hub has to serve both ends
of it. All under `/api/v1` behind the `ChatUser` policy (schemes
`ProviderFirebase` + `ParentFirebase`). The caller's participant id is ALWAYS
resolved from the token, never from a route or body. Full mobile contract:
[`docs/chat.md`](docs/chat.md).

```
GET    /health
GET    /me                                            who the server thinks you are → { participantType, participantId, firebaseUserId, canChat, userId }. canChat false = valid token, no completed profile; every chat route then answers 403 ChatProfileNotCompleted.

POST   /conversations                                 body { counterpartyId } — get-or-create the caller's thread with somebody. The counterparty's TYPE is not sent: a thread always runs provider <-> parent, so the server derives it from whichever app validated the token. Idempotent by construction (UNIQUE (ProviderId, PetParentId)). Rate limited to 10/hour — opening threads with strangers is the actual spam vector, not volume within one. 404 ProviderNotFound / PetParentNotFound; 409 ProviderAccountDeleted; 403 ConversationBlocked.
GET    /conversations                                 [?skip= &take=] inbox, most recently active first; never-used threads sort last. take caps at 20. Counterparty name joined LIVE, so a deleted account reads "Deleted Provider" / "Deleted User". counterpartyPhotoUrl is populated for a pet parent only — a provider's image lives in their Cosmos offering doc, not the SQL row this joins.
GET    /conversations/unread-summary                  { unreadMessageCount, unreadConversationCount } — two figures because the badge wants one and "3 conversations need you" wants the other. A muted thread still counts: muting silences the push, it does not mark anything read.
GET    /conversations/{conversationId}                thread header + your own read state. 404 ConversationNotFound for BOTH unknown and not-yours, so an id cannot be probed.
GET    /conversations/{conversationId}/messages       [?beforeSequence= &take=] history, newest first, take caps at 50. Ordered by SEQUENCE not timestamp — two messages can share a millisecond, never a sequence. Page back with the response's nextBeforeSequence; null means the start of the thread.
POST   /conversations/{conversationId}/messages       body { clientMessageId?, kind?, text?, attachment? }. Socket-less send path — identical server logic to the hub's SendMessage. ALWAYS send clientMessageId: it becomes the message id (and the Cosmos document id), so a retry after a dropped response returns the original instead of posting twice. Returns 200 not 201, because a replay legitimately returns an existing message. Rate limited to 30/min. 400 UnsupportedMessageKind.
DELETE /conversations/{conversationId}/messages/{messageId}   SOFT delete — the message keeps its place and its sequence, only its content goes, and it reads back isDeleted: true. Removing it would leave a hole in the thread and rewrite what the other party already saw. Scoped to the sender; "not yours" and "no such message" are one answer.
POST   /conversations/{conversationId}/read           body { upToSequence } — clamped to the thread's own last sequence and never moves backwards, so a stale client cannot un-read a thread. Unread only reaches zero when the caller has fully caught up.
POST   /conversations/{conversationId}/attachments    multipart { file } — <=5 MB, JPEG/PNG/WebP, uploaded to chat-attachments/<conversationId>/. Participant-authorised BEFORE the blob write. Returns the attachment block to put straight on the message you then send with kind "Image". 400 InvalidFile / ImageTooLarge / UnsupportedImageFormat.

POST   /blocks                                        body { counterpartyId, reason? } — idempotent. Stops new messages BOTH ways and stops the thread reopening; existing history stays readable, because it is part of both parties' record and is what a blocked user would need in order to report the exchange.
GET    /blocks                                        only blocks the caller PLACED. Blocks against them are never returned — telling someone they have been blocked confirms the other party acted.
DELETE /blocks/{chatBlockId}                          lifts one the caller placed. 404 ChatBlockNotFound (unknown and not-yours are one case).

POST   /blob-images                                   body { blobUrl } — the same private-container fetch both other hosts carry.

HUB    /hubs/chat                                     Client->server: JoinConversation, LeaveConversation, SendMessage, MarkRead, SetTyping, Heartbeat. Server->client: MessageReceived, MessageRead, TypingChanged, ConversationUpdated, UnreadCountChanged. Token via ?access_token= (a WebSocket handshake cannot carry a header) — honoured on /hubs/* ONLY. JoinConversation authorises before joining the group, so a thread cannot be subscribed to by guessing an id, and it sets the ActiveConversationId that SUPPRESSES the push — a client that never calls LeaveConversation stops notifying its user. Typing is broadcast only, never stored. Heartbeat every ~60s or the 3-minute sweep purges the connection.
```

### Provider host (`Pawfront.Api`)

```
GET    /health
GET    /metadata                                                                 static reference vocabularies for mobile pickers → { animals: [{code, displayName}], behaviours: [{code, displayName}] }. Derived from the Pawfront.Domain.Vocabularies enums (Animal/Behaviour).

POST   /provider-onboarding/firebase-auth
POST   /provider-onboarding/profile
GET    /provider-onboarding/me                                                   resolves caller's Firebase uid → { providerAuthIdentityId, providerId?, hasProfile, onboardingStatus? } — used by mobile after reinstall to recover ProviderId

POST   /device-tokens                                                            body { fcmToken, deviceId?, devicePlatform? } — registers/refreshes the caller's FCM token. Mirror of the parent host's endpoint (see there for the full rationale): FCM tokens rotate, the sign-in flow only runs at sign in, so the app calls this on every launch AND from onTokenRefresh. Owner from the JWT → ProviderAuthIdentityId. NOT scoped under /providers/{id} — the token binds to the auth identity, which exists before the profile (ProviderId back-filled on profile completion). Supplying `deviceId` retires every other active token for that device (incl. another account's), which is what makes a reinstall clean up immediately. Sproc `Provider.SaveProviderDeviceToken` (THROW 51004). 404 ProviderAuthIdentityNotFound; 400 InvalidRequest (blank token / unsupported devicePlatform).
POST   /device-tokens/deactivate                                                  body { fcmToken } — sign-out counterpart; IsActive = 0, scoped to the caller's own auth identity. Sproc `Provider.DeactivateProviderDeviceToken` (THROW 51005). 404 DeviceTokenNotFound (unknown and not-yours are one case). POST rather than DELETE for the same reasons as the parent host's copy (no inferred body on DELETE; token must not go in the URL; stripped DELETE bodies would fail silently).

POST   /providers/                                            (legacy in-memory)
GET    /providers/                                            (legacy in-memory)

GET    /providers/{providerId}/profile                                           personal info (name, gender, mobile, DOB, …)
PATCH  /providers/{providerId}/profile                                           body { firstName, lastName, gender, dateOfBirth } — edits the "Edit Profile" subset via Provider.UpdateProviderProfile (THROW 51113). Mobile number/country code (must re-verify via OTP), banner image (own endpoint), onboardingStatus/isActive (own flows) are deliberately NOT editable here. Returns the same ProviderProfileResponse as the GET. 404 ProviderProfileNotFound; 409 ProviderAccountDeleted; 400 UnsupportedGender / InvalidRequest.
DELETE /providers/{providerId}                                                   Account delete = ANONYMISE + permanently DISABLE, NOT a row delete. Scrubs the personal fields on Provider.Providers (name → "Deleted Provider", gender/DOB/mobile replaced, banner + mobileVerifiedAtUtc cleared), sets IsActive=0 + IsDeleted=1 + DeletedAtUtc, severs the Firebase auth identity (freeing the real uid AND phone number for a fresh sign-up), deactivates the ProviderServices rows, and deletes only operational config + media (device tokens, mobile OTPs, photos, service banners, availability, closures, cancellation policy, payout methods, service registration) — one transaction via Provider.DeleteProvider (THROW 51114). Then deletes the provider's Cosmos offering doc (the public listing, so they leave discovery) + best-effort their blobs. **RETAINED untouched:** bookings + night-stay bookings with all their children, organised events + ticket bookings, and the Booking.BookingPayments ledger — deleting them would destroy both parties' history. Orchestrated by IProviderAccountService. Idempotent. Returns { providerId, deletedAtUtc, wasAlreadyDeleted, deactivatedServiceCount, retained*Count… }. The caller's ProviderId is resolved from the JWT and must match the route → 403 Forbidden otherwise (the only ownership-enforced route on this host). 404 ProviderProfileNotFound.
GET    /providers/{providerId}/services                                          [?includeInactive=]

POST   /providers/{providerId}/bookings                                          body carries serviceId; PetGroomer also requires serviceItemCode (one of the 18 canonical codes from the GET /pet-groomer response). App bookings enforce the 2-hour booking lead time → 409 BookingLeadTimeTooShort; the Custom walk-in create is exempt.
GET    /providers/{providerId}/bookings                                          [?date=YYYY-MM-DD] — day-view filter
POST   /providers/{providerId}/bookings/{bookingId}/accept                        provider accepts → CONFIRMED; audited.
POST   /providers/{providerId}/bookings/{bookingId}/decline                       provider rejects → PROVIDER_DECLINED (terminal, frees capacity); audited.
POST   /providers/{providerId}/bookings/{bookingId}/start-job                     provider taps "Start Job" → START_JOB + issues the parent's start-OTP. Two gates: (1) today must BE the booking's service date — BookingDate here, CheckInDate on the night-stay twin (409 BookingNotOnServiceDate, THROW 51144 / night-stay 51264); (2) the provider must be inside their own weekly working hours (Provider.ProviderWeeklyAvailability for today, UTC — closed day or now outside StartTime..EndTime is rejected; break not consulted; unset hours = ungated → 409 OutsideWorkingHours, THROW 51137 / night-stay 51257). The scheduled start TIME does NOT gate it. 404 BookingNotFound, 403 Forbidden, 409 BookingNotStartable (wrong from-state).
POST   /providers/{providerId}/bookings/{bookingId}/start-job/verify              body { otpCode } — provider enters the parent's start-code → IN_PROGRESS. 6 wrong attempts cancel the job (OTP_MAX_ATTEMPTS_EXCEEDED). 400 InvalidRequest (blank), 409 BookingNotStartable (not START_JOB), 400 InvalidStartOtp, 409 StartOtpExpired, 409 OtpAttemptsExceeded.
POST   /providers/{providerId}/bookings/{bookingId}/complete                      body OPTIONAL { nextConsultationDate?, prescription? } — provider marks the job done → COMPLETED (from IN_PROGRESS; no OTP). 409 BookingNotCompletable (not IN_PROGRESS); next-consultation/prescription validated first.
POST   /providers/{providerId}/bookings/{bookingId}/paid                          body { paymentMethod: "Cash"|"Digital" } — the parent has paid the provider → PAID (from COMPLETED) + writes a Booking.BookingPayments ledger row (Amount/PawfrontFee from the price-locked total). App bookings only. 400 InvalidRequest (no method) / UnsupportedPaymentMethod / PaymentNotAppBooking (Custom walk-in), 403 Forbidden, 404 BookingNotFound, 409 BookingNotPayable (not COMPLETED) / BookingAlreadyPaid / BookingNotPriceable. Night-stay twin: POST /providers/{providerId}/night-stay-bookings/{bookingId}/paid.
POST   /providers/{providerId}/bookings/{bookingId}/status                       body { status, note? } — LEGACY back-compat shim. provider sets CONFIRMED|COMPLETED|APPROVAL_NEEDED|PROVIDER_CANCELLED|PARENT_NO_SHOW; audited. COMPLETED here mirrors /complete (from IN_PROGRESS) but skips the next-consultation/prescription extras; cancel blocked once underway (409 BookingInProgress). 403 Forbidden, 400 BookingStatusNotAllowed, 409 BookingStatusTerminal|BookingStatusUnchanged
POST   /providers/{providerId}/bookings/{bookingId}/no-show                      provider reports the PARENT (pet) never showed up → sets PARENT_NO_SHOW (terminal, frees capacity, audited). Allowed from a confirmed-equivalent state or START_JOB, and only 30+ minutes after the booking's scheduled start (BookingDate + StartTime, UTC). Reporting is optional: if nobody reports and the job is still unstarted at the end of the PROVIDER'S WORKING DAY on the booking date (their closing time from ProviderWeeklyAvailability, or the booking's own EndTime if that is later; midnight UTC when no hours are saved), the scheduled external job settles it automatically (START_JOB → PARENT_NO_SHOW, confirmed-equivalent → PROVIDER_NO_SHOW) — this used to be JOB_EXPIRED, and until 2026-08-02 fired at the booking's own end time. Night-stay twin: POST /providers/{providerId}/night-stay-bookings/{bookingId}/no-show — gated on CheckInDate + DropOffTime + **2 HOURS** (night-stay's own window, not 30 min), and auto-settled by the scheduled external job at midnight UTC on the check-in day if the stay is still unstarted (START_JOB → PARENT_NO_SHOW, confirmed-equivalent → PROVIDER_NO_SHOW). 404 BookingNotFound, 403 Forbidden, 409 BookingNotStartable (wrong from-state), 409 NoShowTooEarly (THROW 51128 / 51248), 409 BookingStatusTerminal
POST   /providers/{providerId}/bookings/{bookingId}/rating                      body { rating } — the provider's rating of the PET PARENT on a finished job. **Rating only, 1..5** — no comment, no photos (enforced three times: no comment field here, the service drops one from a direct caller, and CK_BookingReviews_ProviderRatingHasNoComment refuses to store one). Same gate as the parent's review: COMPLETED or PAID, App bookings only. Upsert — 201 on first rating, 200 on a correction. **The caller's ProviderId is resolved from the JWT and must match the route → 403 Forbidden otherwise** (one of the few ownership-enforced routes on this host, alongside earnings and account-delete): the sproc's party check compares the actor to the booking's ProviderId, so trusting the route would let anyone knowing a provider id and one of their booking ids write a rating AS that provider. 404 BookingNotFound; 409 BookingNotReviewable; 400 ReviewNotAppBooking / InvalidRequest. Night-stay twin: POST /providers/{providerId}/night-stay-bookings/{bookingId}/rating.
GET    /providers/{providerId}/reviews                                          [?sortBy=Date|Rating &sortDirection=Asc|Desc &skip= &take=] the reviews parents have left for this provider, with the whole-population summary — the provider's "my reviews" screen. Identical shape and behaviour to the parent host's copy (see there). Deliberately NOT ownership-checked: these are public data, served unfiltered on the parent host too, so restricting it here would only stop the provider seeing what every parent already can.
POST   /providers/{providerId}/bookings/{bookingId}/prescription               body { prescriptionText?, isPetVaccinated, vaccinations: [...] } — vet records/edits the visit prescription (upsert). Vet bookings only, provider-only, only from IN_PROGRESS/COMPLETED. Returns the saved prescription block. 404 BookingNotFound, 403 Forbidden, 400 PrescriptionNotVetBooking, 409 PrescriptionNotAllowed. Also settable via the `prescription` block on .../complete.
GET    /providers/{providerId}/bookings/{bookingId}/terms-changes                terms-changes` — the provider's terms that changed since the booking was created (price, cancellation policy, drop-off/pick-up, selected-location address) plus rule-violation rows (fixed duration / minimum duration / minimum nights) checked against the booked window. Always 200 → { bookingId, hasChanges, changes: [{ field, changeType, bookedValue, currentValue, message }] }; hasChanges false = no confirmation sheet. Feeds `acknowledgeTermsChanges` on POST .../modifications (409 BookingTermsChanged without it once drifted). 404 BookingNotFound when the booking isn't this provider's. Night-stay twin: GET /providers/{providerId}/night-stay-bookings/{bookingId}/terms-changes.
GET    /providers/{providerId}/bookings/{bookingId}/status-history               full status audit trail, oldest-first (404 if not this provider's booking)
GET    /providers/{providerId}/earnings/overview                                 the earnings landing screen in one call: lifetime totals + thisWeek/thisMonth/thisYear, all resolved from ONE instant (four separate calls could straddle midnight UTC and mix two weeks into one screen). Each block carries counts (completed / paid / awaitingPayment / unpriced) and money three ways — grossAmount (what parents pay, the provider holds it), pawfrontFee (commission collected on Pawfront's behalf, owed back), netAmount (gross − fee, the headline) — each split into received* (marked PAID) and awaiting* (job COMPLETED, cash not yet recorded). privateJobCount/privateJobAmount report Custom walk-ins, which are off-platform and excluded from every other figure. **Ownership-enforced from the JWT → 403 Forbidden on a mismatch** (revenue data; unlike the rest of this host it does not trust the route id).
GET    /providers/{providerId}/earnings                                          [?period=Weekly|Monthly|Quarterly|Yearly|AllTime] the same totals scoped to ONE calendar period (default AllTime), plus the resolved periodStart/periodEnd so the client can label the screen without redoing calendar maths. Periods are calendar-aligned in UTC and cover the WHOLE period, not truncated at today (a booking can be marked COMPLETED ahead of its service date). 400 InvalidRequest on an unknown period; 403 Forbidden.
GET    /providers/{providerId}/earnings/bookings                                 [?period= &from= &to= &sortBy=Date|Earnings &sortDirection=Asc|Desc &skip= &take=] which jobs produced the money — single-day and night-stay merged, bookingType discriminates. Explicit from/to override period; take is capped at 20 server-side; sort defaults to Date/Desc with a BookingId tie-break (without it OFFSET paging repeats or skips rows sharing a date or amount). Rows carry jobId (PF-000123), payoutId (PO-000123) + payoutStatus, serviceDate, grossAmount/pawfrontFee/netAmount, isPaid, paidAtUtc, paymentMethod, and the customer + pet name. Reconciles exactly with the summary over the same range because both read Booking.BookingAmounts. Custom walk-ins ARE listed but flagged isPrivate — they are real work, just not part of the platform totals. 400 InvalidRequest (bad period/sortBy/sortDirection, or from > to); 403 Forbidden.

GET    /bookings/{bookingId}
POST   /bookings/{bookingId}/cancel                                              parent cancel → sets PARENT_CANCELLED + audit
GET    /pet-parents/{petParentId}/bookings

GET    /pet-parents/{petParentId}/details                              provider-facing customer card (PetParentLookupEndpoints.cs): { petParentId, profileImageUrl, rating, ratingCount, name, gender, age (full years, computed from DOB at UTC today), dateOfBirth, address { addressLine, city, zipCode, latitude, longitude }, aboutParent, email, mobileCountryCode, mobileNumber, pets: [{ petId, name, petType, breed, gender, age { years, months }, profileImageUrl }] }. `rating` (2026-08-08) is the parent's average as rated BY providers on completed jobs — null when nobody has rated them yet, since no ratings is a different statement from a bad average; `ratingCount` travels with it because "5.0" off one rating and "4.6" off forty are very different claims. Composed from IParentOnboardingService.GetProfileAsync + IParentPetService.GetPetsAsync + IPetParentRatingReader. 404 PetParentNotFound. NOT ownership-scoped — any signed-in provider can read any parent by GUID (same posture as the booking-detail parentDetails block).
GET    /pets/{petId}                                                   provider-facing full pet profile (PetParentLookupEndpoints.cs): { petId, petParentId, profileImageUrl, name, microchipId, petType, gender, weight, age { years, months }, dateOfBirth, breed, aboutPet (description), healthOfPet (medical history), vaccinationStatus, sterilizationStatus, vaccinationType, vaccinationDose, prescription, temperament, photos: [url, ...] (gallery, oldest-first) }. Backed by IParentPetService.GetPetAsync. 404 PetNotFound. NOT ownership-scoped (same posture as above).

POST   /providers/{providerId}/mobile-verification/otp
POST   /providers/{providerId}/mobile-verification/otp/{otpId}/verify

POST   /providers/{providerId}/policy/payout-methods
POST   /providers/{providerId}/policy/cancellation
GET    /providers/{providerId}/policy

POST   /providers/{providerId}/active-status                                     body { isActive, acknowledgeExistingBookings? } — master switch. Discriminated 200: Updated | BookingsExist (lists future confirmed bookings). `acknowledgeExistingBookings: true` (2026-08-09) is "Honor Bookings & Deactivate": the provider has seen that list and commits to serving those jobs, so the switch flips for NEW bookings only — they leave discovery and every create is refused with 409 ProviderInactive, while the listed jobs stay startable/completable/payable (no lifecycle sproc reads IsActive). Response carries `honouredBookingCount` and a confirming `warningMessage`. Omit it and the historic refusal is unchanged, which is what lets the app offer the choice. Still no force override — nothing cancels a customer's booking for the provider. NOTE the conflict list covers single-day Booking.Bookings only, not night-stay boarding.

POST   /providers/{providerId}/banner-image                                      (multipart { file }) the provider's single provider-level banner — the wide picture asked for at registration next to the profile photo, shown on their card in the parent-facing searches. <=5 MB, JPEG/PNG/WebP. Uploads to the [ProviderBanners] blob folder ("provider-banners/<providerId>/<guid>.<ext>") and overwrites Provider.Providers.BannerImageUrl via Provider.UpdateProviderBannerImage (sproc 51112). Returns { providerId, bannerImageUrl, updatedAtUtc }. Upload-only — the URL is read back as `bannerImageUrl` on GET /providers/{id}/profile and on the parent host's GET /providers/{providerId}. 400 InvalidFile/ImageTooLarge/UnsupportedImageFormat; 404 ProviderNotFound. Distinct from the per-service banner below, which is keyed by ServiceId and can only be set once an offering exists.

POST   /providers/{providerId}/photos                                            (multipart { file }) general provider photo gallery — uploads to [ProviderPhotos] blob folder, inserts a row in Provider.ProviderPhotos { ProviderPhotoId, ProviderId, PhotoUrl, CreatedAtUtc }. <=3 MB, JPEG/PNG/WebP. 404 ProviderNotFound (sproc 51110); 400 InvalidFile/ImageTooLarge/UnsupportedImageFormat.
GET    /providers/{providerId}/photos                                            list the provider's gallery photos, oldest-first ([] when none).
DELETE /providers/{providerId}/photos/{photoId}                                  removes one photo (scoped by ProviderId + PhotoId) + best-effort blob delete. 404 ProviderPhotoNotFound (sproc 51111).

POST   /providers/{providerId}/availability
GET    /providers/{providerId}/availability
GET    /providers/{providerId}/availability/slots                      ?serviceId= &date= [&durationHours= | &serviceItemCode=] [&granularityMinutes=] — PetGroomer uses serviceItemCode (duration resolved server-side from the menu item); other categories use durationHours

POST   /providers/{providerId}/closures                                 body carries serviceIds[]
GET    /providers/{providerId}/closures                                [?serviceId= &from= &to=]
DELETE /providers/{providerId}/closures/{closureId}

GET    /providers/{providerId}/onboarding-status

POST   /providers/{providerId}/services/pet-sitter/profile-image       (multipart)
POST   /providers/{providerId}/services/pet-sitter/service-image       (multipart)
POST   /providers/{providerId}/services/pet-sitter/pet-hotel
POST   /providers/{providerId}/services/pet-sitter/freelance
POST   /providers/{providerId}/services/pet-sitter/pet-hotel/offering
POST   /providers/{providerId}/services/pet-sitter/freelance/offering
GET    /providers/{providerId}/services/pet-sitter

POST   /providers/{providerId}/services/pet-groomer/profile-image      (multipart)
POST   /providers/{providerId}/services/pet-groomer/service-image      (multipart)
POST   /providers/{providerId}/services/pet-groomer/groomer-shop
POST   /providers/{providerId}/services/pet-groomer/freelance
POST   /providers/{providerId}/services/pet-groomer/groomer-shop/offering
POST   /providers/{providerId}/services/pet-groomer/freelance/offering
GET    /providers/{providerId}/services/pet-groomer

POST   /providers/{providerId}/services/pet-trainer/profile-image      (multipart)
POST   /providers/{providerId}/services/pet-trainer/service-image      (multipart)
POST   /providers/{providerId}/services/pet-trainer/training-school
POST   /providers/{providerId}/services/pet-trainer/freelance
POST   /providers/{providerId}/services/pet-trainer/training-school/offering
POST   /providers/{providerId}/services/pet-trainer/freelance/offering
GET    /providers/{providerId}/services/pet-trainer

POST   /providers/{providerId}/services/pet-adoption-sale/profile-image (multipart)
POST   /providers/{providerId}/services/pet-adoption-sale/service-image (multipart)
POST   /providers/{providerId}/services/pet-adoption-sale/pet-shelter
POST   /providers/{providerId}/services/pet-adoption-sale/pet-shop
POST   /providers/{providerId}/services/pet-adoption-sale/freelance
GET    /providers/{providerId}/services/pet-adoption-sale

POST   /providers/{providerId}/services/vet/profile-image              (multipart)
POST   /providers/{providerId}/services/vet/service-image              (multipart)
POST   /providers/{providerId}/services/vet/vet-clinic
POST   /providers/{providerId}/services/vet/freelance
POST   /providers/{providerId}/services/vet/vet-clinic/offering
POST   /providers/{providerId}/services/vet/freelance/offering
GET    /providers/{providerId}/services/vet

POST   /providers/{providerId}/events/banner-image                     (multipart)
POST   /providers/{providerId}/events
PUT    /providers/{providerId}/events/{eventId}                        full-replace edit (every field). 404 EventNotFound (not found/not owned), 400 InvalidRequest. Reconciles Cosmos physical doc; returns full detail.
PATCH  /providers/{providerId}/events/{eventId}                        partial edit — body fields are all Optional<T>; only supplied fields change (explicit null clears, e.g. bannerImageUrl). Reads current event, merges, reuses the full-replace update path (full re-validation). 404 EventNotFound; 400 InvalidRequest.
GET    /providers/{providerId}/events                                  each event includes pawPrints + bookings { maxBookings, totalBookings }
GET    /events                                                         [?eventCategory= &eventType= &startDate= &endDate= &isChildFriendly= &amenities=... &title=] provider-agnostic catalog listing. title = optional case-insensitive "contains" search on the event title (LIKE %term%, metacharacters escaped). Each event carries pawPrints, bookings { maxBookings, totalBookings }, and isBookable (false once the caller already holds non-cancelled tickets, matched by JWT email).
GET    /events/trending                                                [?take=] top-N trending events, most engaging first. Trending score = ViewCount + ShareCount + non-cancelled (Confirmed) ticket bookings. take defaults to 20, clamped 1..100. Same shape + Cosmos hydration + isBookable as GET /events; no filters. Backed by IEventService.ListTrendingAsync → Event.ListTrendingEvents. Mirrored on the parent host.
GET    /events/{eventId}                                               includes pawPrints, bookings { maxBookings, totalBookings }, isBookable (false once the caller already holds non-cancelled tickets), paymentOptions [Cash|Digital], attendees [{ attendeeName, ticketNumber }], eventLink (online joining URL; null for physical)

POST   /events/{eventId}/bookings                                      body: { bookerName, bookerEmail, bookerMobile?, attendeeNames[], paymentMethod }
GET    /event-bookings                                                 the caller's own event-ticket bookings ("my booked events") — slim summary cards with the joined event (title, category, eventType, start date/time, banner URL, venue eventLocation for physical / null for online). Booker matched by the JWT email claim; most-recent first, cancelled included. Mirror of the parent host's GET /pet-parents/{petParentId}/event-bookings. 403 EmailClaimMissing.
GET    /event-bookings/{bookingId}                                     returns booking + one entry per ticket
DELETE /event-bookings/{bookingId}                                     soft-cancels the caller's own booking (booker matched by JWT email claim) → flips Status to Cancelled + frees the seat. 403 EmailClaimMissing; 404 EventBookingNotFound; 409 EventBookingAlreadyCancelled
POST   /event-bookings/{bookingId}/payment-confirmation                gateway callback → flips PaymentStatus to Paid|Failed

POST   /events/{eventId}/views                                         public engagement counter
POST   /events/{eventId}/shares                                        public engagement counter
POST   /events/{eventId}/inquiries                                     public engagement counter
POST   /events/{eventId}/payout-methods                               body { payoutMethods: ["Cash"|"Digital", ...] } — organiser payout method(s) for ticket proceeds. Replaces the set. 404 EventNotFound; 400 FreeEventNoPayout (free event) / InvalidRequest (empty or unknown method)

GET    /providers/{providerId}/events/{eventId}/attendees              organiser only — one entry per ticket
GET    /providers/{providerId}/events/{eventId}/metrics                organiser only — views, shares, inquiries, confirmedAttendees, earnings

POST   /blob-images                                                    body: { blobUrl } — streams bytes from the private blob container
```

## Working agreements

- **Always wrap responses with `ApiResults.*`.** Don't reintroduce
  `Results.*` calls in endpoint handlers.
- **System.Text.Json everywhere.** All Cosmos docs use
  `[JsonPropertyName]`; do not switch back to Newtonsoft on Cosmos.
- **Cross-cutting vocabularies live in `Pawfront.Domain.Vocabularies` as
  enums** — `Animal` and `Behaviour` are the single source of truth for the
  animals-handled / pets-trained / animals-treated lists (all categories) and
  the pet-temperament / provider `DogTemperaments` lists. Registries validate
  against `VocabularyCatalog.AnimalCodes` / `BehaviourCodes` (derived from the
  enums); values are stored as the enum NAME (`"Dog"`) in Cosmos + SQL and
  surfaced to mobile via `GET /metadata` on both hosts. Add a value to the enum
  to extend it everywhere at once. **Do not reintroduce per-category copies of
  these sets.** (PetTrainer's richer training-temperament list, and per-category
  sets that genuinely differ — `AddOns`, `ServiceLocation` — still live in the
  impls.)
- **Other allowed-enum string sets stay in the Cosmos/SQL layer impls** —
  not in domain enums. (Domain enums name the categories; impls own the
  string constants used in JSON/SQL.)
- **When adding new validation helpers, mirror the existing
  `NormalizeSet` / `NormalizeOne` / `Required` / `Trim` shape** so the
  per-category impls stay readable.
- **Add new Cosmos containers** by extending
  `CosmosContainerOptions`, `CosmosBootstrapper.BuildSpecs`, and creating
  a `IXxxContainerAccessor` + `XxxContainerAccessor`.
- **Add new endpoint files** as `Endpoints/XxxEndpoints.cs`, register
  with one line in `Program.cs`. Don't bloat `Program.cs`.
- **Add new SQL** by writing the file under `database/Pawfront.Database/`
  AND mirroring the change in `Deployment/DeployAll.sql` (idempotent
  blocks). Both must stay in sync.

## Build / run

```powershell
# Build
dotnet build .\Pawfront.slnx --configfile .\NuGet.Config

# Run
dotnet run --project .\src\Pawfront.Api\Pawfront.Api.csproj --configfile .\NuGet.Config

# Deploy SQL (re-runnable, idempotent)
sqlcmd -S littersoftdb.database.windows.net -d littersoft-dev `
       -U littersoftadmin -P "<password>" `
       -i .\database\Pawfront.Database\Deployment\DeployAll.sql
```

## Get a fresh Firebase ID token (for manual testing)

```powershell
$apiKey   = "<firebase-web-api-key>"
$body = @{ email = "..."; password = "..."; returnSecureToken = $true } | ConvertTo-Json
$auth = Invoke-RestMethod `
  -Method Post `
  -Uri "https://identitytoolkit.googleapis.com/v1/accounts:signInWithPassword?key=$apiKey" `
  -ContentType "application/json" -Body $body
$auth.idToken    # paste after "Bearer " in Authorization header
```
