# Pawfront Backend — Project Memory

Working notes for Claude. Read this first when resuming work.

## What this is

Pawfront is a provider-facing mobile-app backend for pet-services businesses
(groomers, sitters, trainers, vets, adoption/sales). Built on **.NET 10**,
**Azure SQL** (structured data), **Azure Cosmos DB** (per-category service
details and event extension data), **Azure Blob Storage** (images), and
**Firebase Auth** (identity).

A parallel pet-parent (consumer) app exists in plans only — backend has minimal
scaffolding for it (`Customer.PetParents`, `Customer.Pets` tables) but no APIs.

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
- `BlobStorage:Container = "provider-images"`, with
  `Folders.{ProfilePhotos,ServicePhotos,EventBanners}`.
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

### Schemas
- `Provider.*` — provider profile, auth identity, OTP, device tokens,
  policies, service-registration index
- `Customer.*` — `PetParents`, `Pets` (scaffolding only, no APIs)
- `Event.*` — `Events`, `EventAmenities` (event subscriptions designed but
  not yet built)
- `Booking.*` — `Bookings` (real, race-safe via `Booking.CreateBooking` sproc
  with `UPDLOCK, HOLDLOCK` capacity check)

### Stored procedures live in `[Provider]` and `[Event]`
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
- `51062` no remaining capacity for slot (booking create)
- `51063` booking not found (booking cancel)
- `51064` only the booker can cancel
- `51065` booking already cancelled

## Cosmos

- Database: `pawfront`
- Active container: **`ProviderServices`**, partition `/serviceCategory`,
  document id = providerId. Houses per-category details for all five
  service categories.
- Active container: **`Events`**, partition `/eventCategory`, document id =
  EventId. Houses physical-event details (capacity + ticketing). Online
  events get **no** Cosmos doc today.
- Bootstrapper (`CosmosBootstrapper`, `IHostedService`) auto-creates DB +
  containers on startup. Add new specs in
  `CosmosBootstrapper.BuildSpecs(...)` as features land.
- Cosmos client uses **System.Text.Json** serializer (configured in
  `CosmosClientFactory`), so all docs respect `[JsonPropertyName]`
  attributes. **Do not revert** to default Newtonsoft.
- The `pet-profiles`, `visit-notes`, `provider-documents` containers are
  reserved but not yet used; their bootstrap entries are commented out.

## Blob

Container: `provider-images`. Folders:
- `profile-photos/<providerId>/<guid>.<ext>`
- `service-photos/<providerId>/<guid>.<ext>`
- `events/<providerId>/<guid>.<ext>`

`BlobUploadKind` enum: `ProfilePhoto`, `ServicePhoto`, `EventBanner`.

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

### Service categories (5 of them)

Each category has a **basic registration** + **offering** (where applicable):

| Category | Sub-categories | Offering built? | Cosmos doc shape |
|---|---|---|---|
| Pet Sitter | `PetHotel`, `FreelancePetSitter` | ✅ Day/Night branches | Both sub-types share `PetSitterLicense` + `PetSitterOffering` with `BoardingOffering` for `dayCare`/`nightStay` |
| Pet Groomer | `GroomerShop`, `FreelanceGroomer` | ✅ single session | `PetGroomerLicense` + `PetGroomerOffering` with `GroomingOffering` |
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
- `GET /providers/{id}/availability/slots?date=YYYY-MM-DD&durationHours=2&granularityMinutes=30`
  — Round 2 endpoint. Reads provider's single service registration (SQL),
  pulls the offering from Cosmos for capacity + duration rule, then walks
  the working windows for that date (minus break) at the requested
  granularity. Returns `{ slots: [{ startTime, endTime }, ...] }`.
  - **Duration rule per category:** PetSitter/PetGroomer require
    `durationHours >= offering minimum`; PetTrainer/Vet require
    `durationHours == offering fixed duration`; PetAdoptionAndSale rejects
    (no offering structure built).
  - **Capacity comes from the offering** (`maxPetsAtOneTime` /
    `maxConcurrentSessions` / `maxConcurrentConsultations`); response
    surfaces it as `capacity`.
  - **TODO Round 3:** slot list is currently capacity-naive (no real
    bookings table yet). When `Event.Bookings` lands, the orchestrator
    will subtract slots whose overlapping-booking count has reached
    `capacity`. The orchestrator and endpoint are ready; only the
    booking-reader call needs to be wired in.
- **Round 3 shipped.** Real bookings table `Booking.Bookings`. Capacity
  check is race-safe via `Booking.CreateBooking` sproc with
  `UPDLOCK, HOLDLOCK` on the overlap-count query, so two concurrent
  POSTs serialise and the second is rejected once capacity is full.
  - `IProviderOfferingResolver` extracted as the shared "look up
    provider's category, capacity, duration rule" reader — used by
    both the slot service and the booking service.
  - `BookingService` implements both `IBookingService` (Create / Get /
    Cancel / list-by-provider / list-by-parent) and
    `IDailyBookingReader` (used by the slot service to subtract
    overlapping bookings against capacity). Single registration via
    `BookingService` resolves both abstractions.
  - Booking validation in C# rejects out-of-hours windows, break
    overlaps, and duration mismatches BEFORE hitting SQL. The SQL
    sproc still has the capacity check as the race-safe last line of
    defense.
  - Sub-categories carried as a denormalised snapshot on the
    booking row (so historical bookings keep meaning even if the
    provider deregisters).
- `GET /providers/{id}/policy` — returns both.

### Onboarding status orchestrator
- `GET /providers/{id}/onboarding-status` — single endpoint that returns:
  `basicInfo`, `serviceSelection`, `selectedServiceDetails` (one entry per
  registered category, checks both license + offering in Cosmos),
  `payoutAndCancellation`, `verification` (email + mobile), and
  `isFullyOnboarded` roll-up.
- Backed by `Provider.GetProviderOnboardingStatus` sproc (4 result sets in
  one round-trip) + a fan-out of point reads to each registered category's
  Cosmos registry.

### Events (provider-created, parents subscribe)
- `POST /providers/{id}/events/banner-image` (multipart) → URL
- `POST /providers/{id}/events` — creates event. Body has 8 SQL fields +
  `physical: { maximumCapacity, isPaid, price? }` for physical events.
- `GET /providers/{id}/events` — list this provider's events (SQL only).
- `GET /events/{eventId}` — single event detail. SQL + Cosmos if physical.
- Storage split: SQL has bulk + amenities junction; Cosmos has physical
  capacity + ticketing. Online events have only SQL row.
- Categories (8): `AdoptionAndRescue`, `PetTraining`, `Charity`,
  `Volunteering`, `HealthAndWellness`, `SocialAndCultural`,
  `OutdoorActivities`, `ParentEducation`.
- Amenities (8): `FreeParking`, `PaidParking`, `Restrooms`, `DrinkingWater`,
  `FoodAndBeverage`, `SeatingAreas`, `FirstAidBooth`, `None`. `None` can't
  coexist with others (enforced in validation).

## In progress / next step

**Event subscriptions are designed but not built.** Last conversation
ended with a proposal awaiting confirmation:

- New SQL tables: `Event.EventSubscriptions` + `Event.EventSubscriptionPets`
- UNIQUE(EventId, PetParentId) — re-subscription flips status, no row
  proliferation
- Capacity enforcement via stored proc with `UPDLOCK, HOLDLOCK` to
  serialize concurrent subscribes; API fetches `MaximumCapacity` from
  Cosmos and passes it to the proc
- 4 endpoints: subscribe / cancel / list-by-event / list-by-parent
- Self-cancellation only; provider moderation deferred
- Payment deferred (placeholder `IsPaid` + `PaymentReference` fields)
- No Cosmos doc for subscriptions

Six decision points still open — see the last reply in chat history.
User said "save this for later," so design is pending confirmation
before build.

## Deferred / known issues — pull forward when relevant

1. **Credentials in `appsettings*.json` files** — SQL password, Cosmos
   AccountKey, Blob AccountKey are all in committed config. **Rotate
   these.** Move to user-secrets or Key Vault before any production
   exposure.
2. **`Provider.Providers`, `Services`, `Bookings` services** are still
   in-memory placeholders (`InMemoryProviderService`, etc.). The
   "legacy" `/providers POST/GET` and `/providers/{id}/services` and
   `/providers/{id}/bookings` endpoints don't persist across restarts.
3. **Pet Adoption & Sale offering** not built — only basic registration.
4. **Online events** have no Cosmos doc / extension fields yet.
5. **No PUT / DELETE for events** — only create.
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

All under `/api/v1`, all require `FirebaseUser` policy.

```
GET    /health

POST   /provider-onboarding/firebase-auth
POST   /provider-onboarding/profile

POST   /providers/                                            (legacy in-memory)
GET    /providers/                                            (legacy in-memory)
POST   /providers/{providerId}/services                       (legacy in-memory)
GET    /providers/{providerId}/services                       (legacy in-memory)

POST   /providers/{providerId}/bookings
GET    /providers/{providerId}/bookings
GET    /bookings/{bookingId}
POST   /bookings/{bookingId}/cancel
GET    /pet-parents/{petParentId}/bookings

POST   /providers/{providerId}/mobile-verification/otp
POST   /providers/{providerId}/mobile-verification/otp/{otpId}/verify

POST   /providers/{providerId}/policy/payout-methods
POST   /providers/{providerId}/policy/cancellation
GET    /providers/{providerId}/policy

POST   /providers/{providerId}/availability
GET    /providers/{providerId}/availability
GET    /providers/{providerId}/availability/slots                      ?date= &durationHours= [&granularityMinutes=]

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
GET    /providers/{providerId}/events
GET    /events/{eventId}
```

## Working agreements

- **Always wrap responses with `ApiResults.*`.** Don't reintroduce
  `Results.*` calls in endpoint handlers.
- **System.Text.Json everywhere.** All Cosmos docs use
  `[JsonPropertyName]`; do not switch back to Newtonsoft on Cosmos.
- **All allowed-enum string sets stay in the Cosmos/SQL layer impls** —
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
