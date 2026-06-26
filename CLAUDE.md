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
  with `UPDLOCK, HOLDLOCK` capacity check, scoped by `ServiceId`)

### Stored procedures live in `[Provider]`, `[Event]`, and `[Booking]`
See `database/Pawfront.Database/StoredProcedures/`. Naming pattern:
`Provider.SaveXxx`, `Provider.GetXxx`, `Event.CreateEvent`, etc.
Custom `THROW` codes used for typed errors:
- `51001` provider auth identity not found
- `51002` provider profile not found (OTP create)
- `51003` provider mobile OTP not found
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
- `51100` provider profile not found (active-status toggle)
- `51110` provider not found (provider photo add)
- `51111` provider photo not found (provider photo delete)
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
- `51214` pet not found (pet delete)
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

`BlobUploadKind` enum: `ProfilePhoto`, `ServicePhoto`, `EventBanner`,
`PetParentProfilePhoto`, `PetPhoto`, `PetParentIdentity`, `PetParentPhoto`,
`ProviderPhoto`, `PetProfilePhoto`. The `IPawfrontBlobStorage.UploadAsync`
parameter is `ownerId` (generic) — it's a ProviderId for provider kinds,
a PetParentId for pet-parent kinds, a PetId for pet kinds.

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
   `dateOfBirth`, `mobileVerifiedAtUtc`, `onboardingStatus`, timestamps).
   Backed by `Provider.GetProviderProfile` sproc against `Provider.Providers`.
   Returns 404 `ProviderProfileNotFound` if the row is missing. Same
   `ProviderProfileResponse` shape that step 2 returns.

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
  `{ code, price, durationMinutes (5–480), isActive }`. Each provider sets their
  own price AND duration for each service they offer; isActive lets them
  temporarily disable a single service without dropping the whole offering.
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
- `POST /providers/{id}/active-status` body `{ isActive: bool }`. Always
  returns 200 + envelope; the response payload is **discriminated**:
  - `status: "Updated"` → flag flipped; `isActive` + `updatedAtUtc` populated.
  - `status: "BookingsExist"` → only emitted on deactivation; flag NOT
    flipped; `conflictingBookings` lists every future confirmed booking on
    any of the provider's services (with `serviceCategory`, `subCategory`,
    `bookingDate`, etc.) plus a `warningMessage`. Provider must move/cancel
    these and retry. **There is no `force` override**.
- Sproc `Provider.SetProviderActiveStatus` holds `UPDLOCK + HOLDLOCK` on
  the provider row + on the Bookings overlap-count query, so concurrent
  `Booking.CreateBooking` on any of the provider's services serialises
  behind it (race-safe). Activation is always applied immediately, no
  conflict check.
- "Future booking" = `BookingDate > today` OR (`BookingDate = today` AND
  `EndTime > now`). The provider's already-served bookings on today are
  not considered conflicts.
- `IsActive` is surfaced on `GET /providers/{id}/profile` and `GET
  /provider-onboarding/me` (in the latter as nullable, since a pre-profile
  auth identity has no IsActive).

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
     durationHours, capacity, granularityMinutes, slots: [...] }`.
  - **Duration rule per service type:** PetSitter (`DayCare`, `NightStay`) and
    PetGroomer (`GroomingSession`) require `durationHours >= offering minimum`;
    PetTrainer (`TrainingSession`) and Vet (`VetAppointment`) require
    `durationHours == offering fixed duration`; PetAdoptionAndSale has no
    `ProviderServices` row and therefore can't be queried here.
  - **Capacity comes from the offering** (`maxPetsAtOneTime` /
    `maxConcurrentSessions` / `maxConcurrentConsultations`) but is scoped by
    ServiceId — DayCare and NightStay each get their own capacity bucket.
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

### Booking status lifecycle + audit
`Booking.Bookings.Status` is a 6-state lifecycle (stored as literal
uppercase): `CREATED` (parent booked) → `CONFIRMED` (provider accepted) →
`COMPLETED` (both agree it's done); `APPROVAL_NEEDED` is the transient state
when either party needs a schedule change; `PROVIDER_CANCELLED` /
`PARENT_CANCELLED` are the two terminal cancellation states. **A booking
holds its capacity slot in every status except the two cancelled ones** — the
"active booking" predicate `Status NOT IN ('PROVIDER_CANCELLED',
'PARENT_CANCELLED')` is what every capacity/closure-conflict/active-status/slot
query keys off (replacing the old `Status = 'Confirmed'`). New parent/provider
app bookings default to `CREATED`; provider-added custom walk-ins start
`CONFIRMED`.
- **Status-change API (both hosts, role-guarded):**
  `POST /providers/{providerId}/bookings/{bookingId}/status` (actor = Provider,
  may set CONFIRMED/COMPLETED/APPROVAL_NEEDED/PROVIDER_CANCELLED) and
  `POST /pet-parents/{petParentId}/bookings/{bookingId}/status` (actor = Parent,
  may set APPROVAL_NEEDED/COMPLETED/PARENT_CANCELLED). Body
  `{ status, note? }`. The actor + their id come from the authenticated route,
  never the body. Backed by `Booking.UpdateBookingStatus` (UPDLOCK+HOLDLOCK):
  enforces the actor is a party to the booking, the status is settable by that
  actor, the booking isn't already terminal, and the status actually changes.
- **Audit table `Booking.BookingStatusHistory`** (append-only,
  `ON DELETE CASCADE`): one row per transition — `FromStatus` (null for the
  seeded creation entry), `ToStatus`, `ChangedByActor`
  (`Provider`/`Parent`/`System`), `ChangedByActorId`, `Note`, `ChangedAtUtc`.
  Seeded on create (`System`/'Booking created'), and written by both the
  status-change API and the legacy `Booking.CancelBooking` (now sets
  `PARENT_CANCELLED`). Read via
  `GET /{providers/{providerId}|pet-parents/{petParentId}}/bookings/{bookingId}/status-history`
  (oldest-first; the endpoint verifies the caller is a party → 404 otherwise).

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
  (refund policy) — **required for every event type**, one of
  `FullRefundUpTo4Hours` / `FullRefundUpTo2Hours` / `NoRefund` (→ `400
  InvalidRequest` otherwise). Stored on the SQL `Event.Events` row and
  returned on every event read. (The refund *execution* flow isn't built
  yet — this just captures the advertised policy.)
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
  (`IsPaid`/`Price`) + `CancellationPolicy`; Cosmos has physical capacity +
  venue location. Online events have only the SQL row.
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
  `paymentMethod` is `CreditCard` or `Twint`. Returns the booking + ticket
  rows; booking is created in `PaymentStatus = Pending`.
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

## In progress / next step

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
4. **Online events** have no Cosmos doc / extension fields yet.
5. **No DELETE for events** — create + full-replace edit (`PUT`) exist; there
   is no delete/cancel-an-event flow yet.
6. **No pet-parent auth.** All endpoints share the provider's
   `FirebaseUser` policy. When the consumer app launches it'll need its
   own auth (separate Firebase project or role claim differentiation).
7. **FCM push notifications not wired.** `NoOpProviderMobileOtpSender`
   is the only `IProviderMobileOtpSender`. The Firebase Admin service
   account file exists but isn't loaded. Natural follow-up when adding
   notifications.
8. **`/api/v1/events/{eventId}` is provider-agnostic** — for future
   pet-parent discovery. No discovery search yet (filter by category,
   date, geo).
9. **`/health` is behind the `FirebaseUser` policy** — fine for now but
   may need to be open for load balancer probes later.

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
POST   /parent-onboarding/profile                                                body { firstName, lastName, gender, mobileCountryCode, mobileNumber, dateOfBirth, addressLine, latitude, longitude, zipCode, city, description }. **The owning auth identity is resolved server-side from the JWT sub/user_id claim** — the body intentionally has no parentAuthIdentityId field, so a caller cannot complete another parent's profile by guessing the id. Creates Parent.PetParents, flips ParentAuthIdentities.SignUpStatus → ParentProfileCompleted, back-fills PetParentId on device tokens. Idempotent (returns existing row if already linked). Sproc `Parent.CompletePetParentProfile` takes `@FirebaseUserId` and resolves the auth identity row under `UPDLOCK + HOLDLOCK`. 409 MobileNumberAlreadyExists on UNIQUE violation; 400 UnsupportedGender / InvalidRequest; 404 ParentAuthIdentityNotFound when no auth identity exists for the Firebase user (caller must hit `firebase-auth` first).
GET    /parent-onboarding/me                                                     resolves the caller's Firebase uid (sub/user_id claim) → { parentAuthIdentityId, petParentId?, firebaseUserId, email, isEmailVerified, displayName, signUpStatus, hasProfile, mobileVerifiedAtUtc? }. Mirror of the provider host's `/provider-onboarding/me` — used by mobile after a reinstall (which wipes local storage but Firebase keeps the session) to recover the PetParentId. PetParentId / HasProfile / MobileVerifiedAtUtc are only populated once `POST /parent-onboarding/profile` has run. Backed by `Parent.GetPetParentByFirebaseUid` (LEFT JOIN PetParents). 404 ParentAuthIdentityNotFound when no auth identity exists yet for this Firebase user (caller must hit `firebase-auth` first).

GET    /pet-parents/{petParentId}/profile                                        full profile read-back: firstName, lastName, gender, email + isEmailVerified (JOINed from Parent.ParentAuthIdentities), mobileCountryCode/mobileNumber, dateOfBirth, addressLine, latitude, longitude, zipCode, city, description (About Me), profilePhotoUrl, mobileVerifiedAtUtc, timestamps. Backed by Parent.GetPetParentProfile (empty result → 404 PetParentNotFound). Ownership-filtered.
PATCH  /pet-parents/{petParentId}/profile                                        body { firstName, lastName, gender, dateOfBirth, addressLine, zipCode, city, description } — edits the basic-profile subset via Parent.UpdatePetParentProfile (THROW 51208). Deliberately NOT editable here: mobile number (must re-verify via OTP), latitude/longitude (no coordinates accompany an address edit — they go stale until a future geocoding pass), profile photo (own endpoint). Returns the same full read-back shape as the GET. 404 PetParentNotFound; 400 UnsupportedGender / InvalidRequest. Ownership-filtered.
POST   /pet-parents/{petParentId}/profile-image                                  multipart form-data { file }. Validations: file required, <=3 MB, content type ∈ { image/jpeg, image/png, image/webp }. Uploads to the shared blob container under the [PetParentProfilePhotos] folder and saves the resulting URL on Parent.PetParents.ProfilePhotoUrl via Parent.UpdatePetParentProfilePhoto. 400 InvalidFile / ImageTooLarge / UnsupportedImageFormat; 404 PetParentNotFound (sproc 51201).
POST   /pet-parents/{petParentId}/pets                                           body { petType, petName, breed, gender, dateOfBirth, weight, microchipId?, description? }. Inserts into Parent.Pets via Parent.AddPetParentPet. PetType ∈ {Dog, Cat, Hamster, GuineaPig}; Gender ∈ {Male, Female}; Weight DECIMAL(5,2) > 0. MicrochipId is globally UNIQUE (filtered) — collision returns 409 MicrochipIdAlreadyExists. 404 PetParentNotFound (sproc 51202); 400 UnsupportedPetType / UnsupportedPetGender / InvalidRequest. Response carries medical-info fields too — all null until PATCH below runs.
GET    /pet-parents/{petParentId}/pets                                           returns every pet on file for the parent with the full medical-info snapshot and embedded photo gallery. Backed by Parent.ListPetParentPets (two result sets: pets + their photos, joined in C# by PetId). Photos within each pet are ordered oldest-first. Empty array when the parent has no pets (or doesn't exist) — list semantics, no 404. Distinct response type PetParentPetWithPhotosResponse so AddPet / PATCH medical-info responses stay unchanged.
GET    /pet-parents/{petParentId}/event-bookings                                 returns the caller's event-ticket bookings — slim summary cards with the joined event (title, category, eventType, start date/time, banner URL, and venue `eventLocation` for physical events — null for online) so the mobile "My Bookings" screen can render without a follow-up fetch. Backed by Event.ListEventBookingsByBookerEmail (SQL) + a per-booking Cosmos point read that hydrates the venue location (physical events only, fanned out in parallel; a failed read returns that card with a null location). **Booker identity on Event.EventBookings is free text (no FK to PetParents), so the filter matches on the caller's Firebase email claim** — the route's petParentId is verified by the ownership filter, then the JWT email is used as the SQL filter. Ordered most-recent first; cancelled bookings included. Mobile drills into GET /event-bookings/{bookingId} for the full shape with attendee names. 403 EmailClaimMissing when the JWT carries no email claim (rare).
GET    /pet-parents/{petParentId}/bookings                                       the parent's own SERVICE bookings ("my bookings"), most-recent first (BookingDate/StartTime DESC), cancelled included. Ownership-filtered (petParentId from JWT), so a caller only sees their own. [] when none — no 404. Backed by IBookingService.ListByPetParentAsync (sproc Booking.ListBookingsByPetParent). Same BookingResponse shape as the create/status endpoints. (Mirror of the provider host's GET /pet-parents/{petParentId}/bookings, which is unscoped on that host.)
POST   /pet-parents/{petParentId}/bookings                                       body { petId, serviceId, bookingDate, startTime, endTime, serviceItemCode? } — parent-initiated SERVICE booking ("book now" from a search result/slot). Booker = route petParentId (ownership-filtered; never from body). Provider resolved server-side from serviceId. petId must be one of the caller's pets (404 PetNotFound / 403 Forbidden inline; sproc re-checks via THROW 51068 → 400 InvalidPetId). Same shared IBookingService.CreateAsync + race-safe Booking.CreateBooking sproc as the provider host — full validation chain (working hours, closures → 409 ServiceClosed, duration rules, groomer serviceItemCode, capacity → 409 CapacityExceeded, 409 ProviderInactive). Booking.Bookings now carries nullable PetId (FK → Parent.Pets), surfaced as petId on every booking read.
POST   /pet-parents/{petParentId}/bookings/{bookingId}/status                    body { status, note? } — parent sets APPROVAL_NEEDED|COMPLETED|PARENT_CANCELLED on their own booking; audited. Actor=Parent, actorId=route petParentId. 403 Forbidden (not the parent's booking), 400 BookingStatusNotAllowed, 409 BookingStatusTerminal|BookingStatusUnchanged. Shared Booking.UpdateBookingStatus sproc with the provider host.
GET    /pet-parents/{petParentId}/bookings/{bookingId}/status-history            full status audit trail, oldest-first (404 if not the parent's booking). Ownership-filtered group; bookingId re-checked against the booking's PetParentId.
GET    /pets/{petId}                                                             single pet profile — full basic-info + medical-info snapshot + embedded photo gallery (oldest-first), the same PetParentPetWithPhotosResponse shape as the list endpoint. Backed by Parent.GetPetParentPet (two result sets: pet + photos). Ownership-filtered (RequireOwnedPet → 404 PetNotFound for unknown pet, 403 Forbidden for someone else's). The handler also maps an empty result set to 404 defensively.
PATCH  /pets/{petId}                                                             body { petType, petName, breed, gender, dateOfBirth, weight, microchipId?, description? } — same shape as AddPet. Updates the basic-info subset via Parent.UpdatePetParentPet; medical-info columns are deliberately untouched (use PATCH /medical-info). Same validations and error map as AddPet: 404 PetNotFound (sproc 51205); 409 MicrochipIdAlreadyExists; 400 UnsupportedPetType / UnsupportedPetGender / InvalidRequest.
PATCH  /pets/{petId}/medical-info                                                body { vaccinationStatus, sterilizationStatus, medicalHistory?, temperament? }. Fills in medical fields on an existing pet via Parent.UpdatePetMedicalInfo. VaccinationStatus ∈ {Vaccinated, NotVaccinated}; SterilizationStatus ∈ {Sterilized, Intact}; Temperament ∈ {Anxious, Friendly, Aggressive} but OPTIONAL — omit/empty to store null (a pet can be added without a known temperament); MedicalHistory is free text and nullable. 404 PetNotFound (sproc 51203); 400 UnsupportedVaccinationStatus / UnsupportedSterilizationStatus / UnsupportedTemperament (only when a non-empty invalid value is sent) / InvalidRequest.
DELETE /pets/{petId}                                                             permanently removes the pet via Parent.DeletePetParentPet (THROW 51214 → 404 PetNotFound). Photo rows (Parent.PetPhotos) cascade with the pet; photo blobs are left for a future sweep. Bookings that referenced the pet are detached (Booking.Bookings.PetId set null — the booking rows keep their denormalised snapshots) so the FK doesn't block deletion. Returns { petId, petParentId, deletedAtUtc }. Ownership-filtered (RequireOwnedPet → 404 PetNotFound for unknown pet, 403 Forbidden for someone else's).
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
GET    /events/{eventId}                                                         single event detail (SQL + Cosmos for physical). Includes pawPrints { viewCount, shareCount, inquiryCount }, bookings { maxBookings, totalBookings }, isBookable (false once the caller already holds non-cancelled tickets), paymentOptions [Cash|Digital], attendees [{ attendeeName, ticketNumber }] (names only).
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

GET    /providers                                                                [?petId= &providerType= &date= &startTime= &endTime= &city= &serviceLocation= &skip= &take=] — parent-facing provider discovery / booking search. ALL filters optional and combinable. petId: must belong to the caller (ownership enforced inline — 403 Forbidden / 403 ParentProfileNotCompleted / 404 PetNotFound, same codes as OwnedPetFilter); the pet's PetType becomes the animal filter (replaces the old raw ?animals= param). providerType ∈ { PetSitter, PetGroomer, PetTrainer, Vet } — 400 UnsupportedProviderType otherwise (PetAdoptionAndSale is NOT a valid filter value, but unfiltered browsing still includes it). date+startTime+endTime travel as a trio (400 InvalidRequest if partial, or startTime >= endTime); when set, each candidate runs through IProviderWindowAvailabilityChecker — a full free-slot check (working hours, closures, confirmed bookings vs capacity via the shared slot service). Window semantics: fixed-duration services (TrainingSession/VetAppointment/grooming item) match when a free slot of that duration fits anywhere inside the window; min-duration services (DayCare/NightStay) match when the FULL window is bookable and >= the offering minimum; PetGroomer probes the shortest active menu item that fits (capacity is shop-wide, so that's sufficient). Pagination applies AFTER availability filtering when a window is set. city: case-insensitive exact match on the Cosmos doc's City. serviceLocation ∈ { ParentsPlace, ProvidersPlace } (400 UnsupportedServiceLocation) — mapped per category onto the offering's stored values (CustomerPlace/CustomerLocation vs PetHotel/GroomerShop/VetClinic/TrainerLocation/TrainingSchool; stored "Both" matches either; trainer's NatureOrParks/UrbanOrCity count as neither). PetAdoptionAndSale providers are excluded when an animal OR serviceLocation filter is set (no offering = no data). Returns slim summary cards: { providerId, serviceCategory, subCategory, displayName (business name; null for freelancers), imageUrl, city, about (Description/AboutYou), animalsHandled }. Take defaults to 50, max 200. Backed by IProviderDiscoveryService → CosmosProviderDiscoveryService (static filters; Cosmos only, in-memory predicates because the relevant paths vary per category) + IProviderWindowAvailabilityChecker (Application orchestrator over IProviderServiceCatalog + IProviderOfferingResolver + IProviderAvailabilitySlotService + IPetGroomerServiceRegistry). BREAKING: the old ?serviceCategory= and ?animals= params were removed.
GET    /providers/search/day-care                                                 [?petId= &date= &startTime= &endTime= &city= &serviceLocation= &skip= &take=] — per-service booking search #1 (PetSitter/DayCare). All filters optional. date+startTime+endTime trio; when set, a provider matches only if the FULL window is bookable on the DayCare service (window >= offering minimum, exact-start free slot, capacity/closures/bookings respected). Charges = PricePerHour.
GET    /providers/search/night-stay                                               [?petId= &startDate= &pickupDate= &city= &serviceLocation= &skip= &take=] — per-service booking search #2 (PetSitter/NightStay). startDate+pickupDate pair (400 if partial, startDate >= pickupDate, or span > 30 nights); pickupDate is checkout, NOT a stayed night. Provider matches only if EVERY night from startDate to pickupDate-1 has free NightStay capacity. Charges = PricePerHour.
GET    /providers/search/groomers                                                 [?petId= &date= &serviceItemCode= &city= &serviceLocation= &skip= &take=] — per-service booking search #3 (PetGroomer/GroomingSession). serviceItemCode validated against the canonical 18-code catalog (400 UnsupportedServiceItemCode); when set, only providers with that item ACTIVE match and charges = the item's price (chargesUnit PerService); when omitted, any groomer with >=1 active menu item matches and charges is null. date → any free slot of the item's duration that day (no code → probes the shortest active item; capacity is shop-wide so that suffices).
GET    /providers/search/vets                                                     [?petId= &date= &city= &serviceLocation= &skip= &take=] — per-service booking search #4 (Vet/VetAppointment). date → any free appointment slot that day. Charges = PricePerAppointment (chargesUnit PerAppointment).
GET    /providers/search/trainers                                                 [?petId= &date= &city= &serviceLocation= &skip= &take=] — per-service booking search #5 (PetTrainer/TrainingSession). Mirror of the vets search: a training session is a single fixed-duration booking, so date → any free slot of the session's duration that day. Charges = PricePerSession (chargesUnit PerSession).

> All five searches: petId is ownership-enforced inline (same codes as OwnedPetFilter) and the pet's PetType becomes the animal filter; city is case-insensitive; serviceLocation ∈ { ParentsPlace, ProvidersPlace }. A provider must have the matching ACTIVE ProviderServices row + configured offering to appear at all. Response per hit: { providerId, serviceId, subCategory, businessName (null for freelancers), completedBookings, charges, chargesUnit (PerHour|PerService|PerAppointment|PerSession), serviceItemCode, imageUrl }. imageUrl = the service image the provider uploaded for this offering (the same image the discovery card shows — sourced from the Cosmos offering's imageUrl via the shared ProviderSummary; null when unset). completedBookings = past non-cancelled/non-no-show bookings across ALL the provider's services (SqlProviderBookingStatsReader, batched). Pagination applies after availability filtering. Backed by IProviderSearchService → ProviderSearchService (Application orchestrator over IProviderDiscoveryService + IProviderServiceCatalog + IProviderOfferingResolver + IProviderAvailabilitySlotService + IPetGroomerServiceRegistry + IProviderBookingStatsReader). OfferingResolution.Resolved now carries Price (PricePerHour / PerSession / PerAppointment; null for grooming).

GET    /providers/{providerId}                                                   parent-facing provider profile. Composes the registration row (category, sub-category, lat/lng), the category-specific offering (one of petSitter / petGroomer / petTrainer / petAdoptionSale / vet — exactly one populated), workingHours (7 days), timeOff (future closures across all the provider's services), the advertised booking policy (minimumHoursBeforeCancellation: null|24|48|72|96, and acceptedPaymentMethods: ["Cash"|"Digital", ...] — the provider's payout-method set, empty when unset), and reviews (ALWAYS an empty array for now — review feature not built yet; the field is wired so mobile can bind ahead of time). Provider personal info (name, mobile, DOB) intentionally omitted — parents see business-facing data only. Backed by IProviderPublicProfileService, which fans out to the existing per-category registries, IProviderAvailabilityService, IProviderClosureService, and IProviderPolicyService (cancellation + payout). 404 ProviderNotRegistered when no service registration row exists.
GET    /providers/{providerId}/availability/slots                                ?serviceId= &date= [&durationHours= | &serviceItemCode=] [&granularityMinutes=] — parent-facing free-slot query (mirror of the provider host). Backed by the shared IProviderAvailabilitySlotService. PetGroomer uses ?serviceItemCode= (duration resolved server-side from the menu item); other categories use ?durationHours=. Closures, capacity, and overlapping confirmed bookings are subtracted per-service. Same error map as provider host (InvalidServiceId, ServiceNotRegistered, OfferingNotConfigured, ServiceItemCodeRequired/NotOffered/Inactive, InvalidBookingDuration, InvalidRequest). Save/get weekly-hours endpoints on the same group (`POST /` and `GET /`) are intentionally **NOT** mirrored on the parent host — those are organiser-only; the 7-day shape is already returned by GET /providers/{providerId} under workingHours.
POST   /blob-images                                                              body: { blobUrl } — streams bytes from the private blob container (mirror of the provider host's universal fetch endpoint; duplicated because the hosts use different Firebase projects). 404 BlobNotFound; 400 InvalidRequest.
```

> Note: the gateway webhook `POST /event-bookings/{bookingId}/payment-confirmation` is intentionally **not** mirrored on the parent host — already reachable on the provider host, and a Firebase-JWT-gated webhook only needs one entry point.

### Provider host (`Pawfront.Api`)

```
GET    /health
GET    /metadata                                                                 static reference vocabularies for mobile pickers → { animals: [{code, displayName}], behaviours: [{code, displayName}] }. Derived from the Pawfront.Domain.Vocabularies enums (Animal/Behaviour).

POST   /provider-onboarding/firebase-auth
POST   /provider-onboarding/profile
GET    /provider-onboarding/me                                                   resolves caller's Firebase uid → { providerAuthIdentityId, providerId?, hasProfile, onboardingStatus? } — used by mobile after reinstall to recover ProviderId

POST   /providers/                                            (legacy in-memory)
GET    /providers/                                            (legacy in-memory)

GET    /providers/{providerId}/profile                                           personal info (name, gender, mobile, DOB, …)
GET    /providers/{providerId}/services                                          [?includeInactive=]

POST   /providers/{providerId}/bookings                                          body carries serviceId; PetGroomer also requires serviceItemCode (one of the 18 canonical codes from the GET /pet-groomer response)
GET    /providers/{providerId}/bookings                                          [?date=YYYY-MM-DD] — day-view filter
POST   /providers/{providerId}/bookings/{bookingId}/status                       body { status, note? } — provider sets CONFIRMED|COMPLETED|APPROVAL_NEEDED|PROVIDER_CANCELLED; audited. 403 Forbidden (not this provider's booking), 400 BookingStatusNotAllowed, 409 BookingStatusTerminal|BookingStatusUnchanged
GET    /providers/{providerId}/bookings/{bookingId}/status-history               full status audit trail, oldest-first (404 if not this provider's booking)
GET    /bookings/{bookingId}
POST   /bookings/{bookingId}/cancel                                              parent cancel → sets PARENT_CANCELLED + audit
GET    /pet-parents/{petParentId}/bookings

POST   /providers/{providerId}/mobile-verification/otp
POST   /providers/{providerId}/mobile-verification/otp/{otpId}/verify

POST   /providers/{providerId}/policy/payout-methods
POST   /providers/{providerId}/policy/cancellation
GET    /providers/{providerId}/policy

POST   /providers/{providerId}/active-status                                     body { isActive } — master switch. Discriminated 200: Updated | BookingsExist (lists future confirmed bookings).

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
GET    /events/{eventId}                                               includes pawPrints, bookings { maxBookings, totalBookings }, isBookable (false once the caller already holds non-cancelled tickets), paymentOptions [Cash|Digital], attendees [{ attendeeName, ticketNumber }]

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
