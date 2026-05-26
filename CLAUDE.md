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

A full **Mermaid ER diagram** + per-table descriptions live in
[`database/Pawfront.Database/README.md`](database/Pawfront.Database/README.md).
Keep that file in sync whenever a table or relationship changes.

### Schemas
- `Provider.*` — provider profile, auth identity, OTP, device tokens,
  policies, service-registration index, **per-service catalog
  (`ProviderServices`) and closures**
- `Customer.*` — `PetParents`, `Pets` (scaffolding only, no APIs)
- `Event.*` — `Events`, `EventAmenities` (event subscriptions designed but
  not yet built)
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

Container: `provider-images` (private). Folders:
- `profile-photos/<providerId>/<guid>.<ext>`
- `service-photos/<providerId>/<guid>.<ext>`
- `events/<providerId>/<guid>.<ext>`

`BlobUploadKind` enum: `ProfilePhoto`, `ServicePhoto`, `EventBanner`.

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

### Event ticket bookings
Anyone with a Firebase login can buy tickets for a physical event. **No FK
to PetParents** — attendee names are free text and never validated against
any user table. Bookings are **per ticket**: 4 attendees → 4 child ticket
rows under one parent booking.

- `POST /events/{eventId}/bookings` — body:
  `{ bookerName, bookerEmail, bookerMobile?, attendeeNames: [...], paymentMethod }`.
  `paymentMethod` is `CreditCard` or `Twint`. Returns the booking + ticket
  rows; booking is created in `PaymentStatus = Pending`.
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
  doc is written** — the event Cosmos doc is read for `maximumCapacity` and
  `price` but never mutated, so per-event ETag contention is avoided.
- Capacity is enforced inside `Event.CreateEventBooking` by SUMming
  `TicketCount` over confirmed rows for the event under `UPDLOCK +
  HOLDLOCK`, then rejecting when the requested ticket count would push the
  total past `@MaximumCapacity`. Concurrent buyers serialise and the
  (N+1)-th seat is rejected once the event is full. Maps to **409
  EventSoldOut** (`51091`).
- `TotalAmount` is a snapshot of `price × ticketCount` at booking time.
  For free events (`Physical.IsPaid = false`) it is 0.
- Online events have no `Physical` block (no capacity) and therefore can't
  be ticketed. Booking attempts return **400 EventNotBookable**.
- Cancellation / refund flow is **not** built yet — only the
  `Status = Cancelled` column shape exists for it.

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

**Inquiries are currently a counter only.** If a richer "inquiry has
content (text, contact info, reply thread)" model is needed later, it
would add an `Event.EventInquiries` table; the counter on `Event.Events`
would then become a denormalised cache (or be replaced by a JOIN).

## In progress / next step

**Event ticket cancellation / refund flow is not built yet.** The
`Event.EventBookings.Status = Cancelled` column + `CancelledAtUtc` exist
for the eventual implementation, but there are no endpoints, no sproc, and
no capacity-release logic. When this lands it must:

- Decide who can cancel (the booker is identified only by free-text email,
  not a Firebase UID — policy decision needed).
- Flip `Status` to `Cancelled` + set `CancelledAtUtc`, which releases
  capacity automatically (the `CreateEventBooking` capacity SUM filters
  `Status = N'Confirmed'`).
- Handle the external refund leg if `PaymentStatus = Paid`.

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

GET    /providers/{providerId}/profile                                           personal info (name, gender, mobile, DOB, …)
GET    /providers/{providerId}/services                                          [?includeInactive=]

POST   /providers/{providerId}/bookings                                          body now carries serviceId
GET    /providers/{providerId}/bookings                                          [?date=YYYY-MM-DD] — day-view filter
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
GET    /providers/{providerId}/availability/slots                      ?serviceId= &date= &durationHours= [&granularityMinutes=]

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
GET    /providers/{providerId}/events
GET    /events/{eventId}

POST   /events/{eventId}/bookings                                      body: { bookerName, bookerEmail, bookerMobile?, attendeeNames[], paymentMethod }
GET    /event-bookings/{bookingId}                                     returns booking + one entry per ticket
POST   /event-bookings/{bookingId}/payment-confirmation                gateway callback → flips PaymentStatus to Paid|Failed

POST   /events/{eventId}/views                                         public engagement counter
POST   /events/{eventId}/shares                                        public engagement counter
POST   /events/{eventId}/inquiries                                     public engagement counter

GET    /providers/{providerId}/events/{eventId}/attendees              organiser only — one entry per ticket
GET    /providers/{providerId}/events/{eventId}/metrics                organiser only — views, shares, inquiries, confirmedAttendees, earnings

POST   /blob-images                                                    body: { blobUrl } — streams bytes from the private blob container
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
