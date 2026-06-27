# Pawfront Database

SQL Server schema for Pawfront's structured (relational) data. Per-category
service offering details and event extension data live in **Cosmos DB**, not
here — see the API project's `docs/architecture.md` for the split.

Tables are organised into four schemas:

| Schema      | Purpose |
|-------------|---------|
| `Provider`  | Provider identity, profile, OTPs, devices, services, availability, closures, policies |
| `Parent`    | Pet-parent identity, Firebase auth identities, device tokens, `PetParents`, `Pets` |
| `Event`     | Provider-created events + amenities junction + ticket bookings |
| `Booking`   | Real bookings (per-service capacity-checked) |

> The legacy `Customer` schema (originally hosting `PetParents` and `Pets`)
> has been retired. `DeployAll.sql` transfers any existing `Customer.*`
> tables into `Parent.*` on first run and drops the empty schema.

Deployment is a single idempotent script:
[`Deployment/DeployAll.sql`](Deployment/DeployAll.sql). Re-runnable; uses
`IF NOT EXISTS` guards on tables/indexes and `CREATE OR ALTER` on sprocs.

## ER diagram

```mermaid
erDiagram
    PROVIDER_AUTH_IDENTITIES ||--o| PROVIDERS                    : "step 1 -> step 2"
    PROVIDER_AUTH_IDENTITIES ||--o{ PROVIDER_DEVICE_TOKENS       : "registers"
    PROVIDERS                |o--o{ PROVIDER_DEVICE_TOKENS       : "owns (after step 2)"
    PARENT_AUTH_IDENTITIES   ||--o| PET_PARENTS                  : "links once profile completes"
    PARENT_AUTH_IDENTITIES   ||--o{ PARENT_DEVICE_TOKENS         : "registers"
    PET_PARENTS              |o--o{ PARENT_DEVICE_TOKENS         : "owns (after profile)"
    PROVIDERS                ||--o{ PROVIDER_MOBILE_OTPS         : "verifies via"
    PROVIDERS                ||--o| PROVIDER_SERVICE_REGISTRATIONS : "registers (1 category max)"
    PROVIDERS                ||--o{ PROVIDER_SERVICES            : "offers (1 row per ServiceType)"
    PROVIDERS                ||--o{ PROVIDER_WEEKLY_AVAILABILITY : "schedules (0..7 rows)"
    PROVIDERS                ||--o{ PROVIDER_CLOSURES            : "closes"
    PROVIDER_SERVICES        ||--o{ PROVIDER_CLOSURES            : "scoped by"
    PROVIDERS                ||--o{ PROVIDER_PAYOUT_METHODS      : "configures"
    PROVIDERS                ||--o| PROVIDER_CANCELLATION_POLICIES : "configures"
    PROVIDERS                ||--o{ EVENTS                       : "creates"
    EVENTS                   ||--o{ EVENT_AMENITIES              : "lists"
    EVENTS                   ||--o{ EVENT_BOOKINGS               : "sells tickets for"
    EVENT_BOOKINGS           ||--o{ EVENT_BOOKING_TICKETS        : "issues (1..N attendees)"
    EVENTS                   ||--o{ EVENT_BOOKING_TICKETS        : "denormalised"
    PROVIDERS                ||--o{ BOOKINGS                     : "fulfills"
    PET_PARENTS              ||--o{ BOOKINGS                     : "places"
    PROVIDER_SERVICES        ||--o{ BOOKINGS                     : "targets"
    PET_PARENTS              ||--o{ PETS                         : "has"
    PETS                     ||--o{ PET_PHOTOS                   : "has (ON DELETE CASCADE)"

    PROVIDER_AUTH_IDENTITIES {
        UNIQUEIDENTIFIER ProviderAuthIdentityId PK
        UNIQUEIDENTIFIER ProviderId            FK "nullable until step 2 completes"
        NVARCHAR         FirebaseUserId        UK
        NVARCHAR         AuthProvider             "Google|Apple|EmailPassword"
        NVARCHAR         Email
        BIT              IsEmailVerified
        NVARCHAR         SignUpStatus             "FirebaseAuthenticated|ProviderProfileCompleted"
        DATETIME2        LastSignedInAtUtc
    }

    PROVIDERS {
        UNIQUEIDENTIFIER ProviderId             PK
        UNIQUEIDENTIFIER ProviderAuthIdentityId FK "UNIQUE; links to auth identity"
        NVARCHAR         FirstName
        NVARCHAR         LastName
        NVARCHAR         Gender                    "Male|Female|NonBinary|Other|PreferNotToSay"
        NVARCHAR         MobileCountryCode         "UNIQUE with MobileNumber"
        NVARCHAR         MobileNumber
        DATE             DateOfBirth
        DATETIME2        MobileVerifiedAtUtc       "nullable"
        NVARCHAR         OnboardingStatus          "MobileVerificationPending|MobileVerified"
    }

    PROVIDER_DEVICE_TOKENS {
        UNIQUEIDENTIFIER ProviderDeviceTokenId  PK
        UNIQUEIDENTIFIER ProviderAuthIdentityId FK
        UNIQUEIDENTIFIER ProviderId             FK "nullable; back-filled after step 2"
        NVARCHAR         FcmToken               UK
        NVARCHAR         DeviceId
        NVARCHAR         DevicePlatform           "Android|iOS"
        BIT              IsActive
        DATETIME2        LastSeenAtUtc
    }

    PROVIDER_MOBILE_OTPS {
        UNIQUEIDENTIFIER ProviderMobileOtpId PK
        UNIQUEIDENTIFIER ProviderId          FK
        NVARCHAR         MobileCountryCode
        NVARCHAR         MobileNumber
        VARBINARY        OtpCodeHash            "SHA-256"
        NVARCHAR         OtpCodeLastTwo
        NVARCHAR         ValidationStatus       "Pending|Validated|Expired"
        INT              FailedAttemptCount
        DATETIME2        DateSentUtc
        DATETIME2        DateValidatedUtc       "nullable"
        DATETIME2        ExpiresAtUtc
    }

    PROVIDER_SERVICE_REGISTRATIONS {
        UNIQUEIDENTIFIER ProviderServiceRegistrationId PK
        UNIQUEIDENTIFIER ProviderId                    FK "UNIQUE; one row per provider"
        NVARCHAR         ServiceCategory                     "PetSitter|PetGroomer|PetTrainer|PetAdoptionAndSale|Vet"
        NVARCHAR         SubCategory
        DECIMAL          Latitude                            "-90..90"
        DECIMAL          Longitude                           "-180..180"
    }

    PROVIDER_SERVICES {
        UNIQUEIDENTIFIER ServiceId       PK
        UNIQUEIDENTIFIER ProviderId      FK   "ON DELETE CASCADE"
        NVARCHAR         ServiceCategory      "PetSitter|PetGroomer|PetTrainer|Vet"
        NVARCHAR         SubCategory
        NVARCHAR         ServiceType          "DayCare|NightStay|GroomingSession|TrainingSession|VetAppointment"
        BIT              IsActive             "soft-deactivate; never deleted"
    }

    PROVIDER_WEEKLY_AVAILABILITY {
        UNIQUEIDENTIFIER ProviderId      PK "also FK; ON DELETE CASCADE"
        TINYINT          DayOfWeek       PK "0=Sunday..6=Saturday"
        BIT              IsOpen
        TIME             StartTime          "nullable when closed"
        TIME             EndTime            "nullable when closed"
        TIME             BreakStartTime     "nullable; must fit window"
        TIME             BreakEndTime       "nullable"
    }

    PROVIDER_CLOSURES {
        UNIQUEIDENTIFIER ClosureId   PK
        UNIQUEIDENTIFIER ProviderId  FK    "ON DELETE CASCADE; denormalised for fast filter"
        UNIQUEIDENTIFIER ServiceId   FK    "closures are PER-SERVICE"
        DATE             StartDate
        DATE             EndDate
        TIME             StartTime         "nullable; full-day if NULL"
        TIME             EndTime           "nullable"
        NVARCHAR         Reason            "nullable; <=500 chars"
    }

    PROVIDER_PAYOUT_METHODS {
        UNIQUEIDENTIFIER ProviderId    PK "also FK"
        NVARCHAR         PayoutMethod  PK "Cash|Digital"
    }

    PROVIDER_CANCELLATION_POLICIES {
        UNIQUEIDENTIFIER ProviderId                    PK "also FK"
        INT              MinimumHoursBeforeCancellation    "null|24|48|72|96"
    }

    PARENT_AUTH_IDENTITIES {
        UNIQUEIDENTIFIER ParentAuthIdentityId PK
        UNIQUEIDENTIFIER PetParentId          FK "nullable until profile completes; UNIQUE when set"
        NVARCHAR         FirebaseUserId       UK
        NVARCHAR         AuthProvider             "Google|Apple|EmailPassword"
        NVARCHAR         Email
        BIT              IsEmailVerified
        NVARCHAR         SignUpStatus             "FirebaseAuthenticated|ParentProfileCompleted"
        DATETIME2        LastSignedInAtUtc
    }

    PARENT_DEVICE_TOKENS {
        UNIQUEIDENTIFIER ParentDeviceTokenId  PK
        UNIQUEIDENTIFIER ParentAuthIdentityId FK
        UNIQUEIDENTIFIER PetParentId          FK "nullable; back-filled after profile"
        NVARCHAR         FcmToken             UK
        NVARCHAR         DeviceId
        NVARCHAR         DevicePlatform           "Android|iOS"
        BIT              IsActive
        DATETIME2        LastSeenAtUtc
    }

    PET_PARENTS {
        UNIQUEIDENTIFIER PetParentId          PK
        UNIQUEIDENTIFIER ParentAuthIdentityId FK "UNIQUE; links to auth identity"
        NVARCHAR         FirstName
        NVARCHAR         LastName
        NVARCHAR         Gender                    "Male|Female|NonBinary|Other|PreferNotToSay"
        NVARCHAR         MobileCountryCode         "UNIQUE with MobileNumber"
        NVARCHAR         MobileNumber
        DATE             DateOfBirth
        NVARCHAR         AddressLine
        DECIMAL          Latitude                  "-90..90"
        DECIMAL          Longitude                 "-180..180"
        NVARCHAR         ZipCode
        NVARCHAR         City
        NVARCHAR         Description               "free-text profile blurb"
        NVARCHAR         ProfilePhotoUrl           "nullable; blob URL"
        DATETIME2        MobileVerifiedAtUtc       "nullable; reserved for OTP flow"
    }

    PETS {
        UNIQUEIDENTIFIER PetId       PK
        UNIQUEIDENTIFIER PetParentId FK
        NVARCHAR         PetType         "Dog|Cat|Hamster|GuineaPig"
        NVARCHAR         PetName
        NVARCHAR         Breed
        NVARCHAR         Gender          "Male|Female"
        DATE             DateOfBirth
        DECIMAL          Weight          "> 0"
        NVARCHAR         MicrochipId         "nullable; UNIQUE when set"
        NVARCHAR         Description         "nullable"
        NVARCHAR         VaccinationStatus   "nullable; Vaccinated|NotVaccinated"
        NVARCHAR         SterilizationStatus "nullable; Sterilized|Intact"
        NVARCHAR         MedicalHistory      "nullable; free text"
        NVARCHAR         Temperament         "nullable; Anxious|Friendly|Aggressive"
    }

    PET_PHOTOS {
        UNIQUEIDENTIFIER PetPhotoId PK
        UNIQUEIDENTIFIER PetId      FK    "ON DELETE CASCADE"
        NVARCHAR         PhotoUrl
    }

    EVENTS {
        UNIQUEIDENTIFIER EventId         PK
        UNIQUEIDENTIFIER ProviderId      FK
        NVARCHAR         EventCategory       "8 values; see CHECK"
        BIT              IsChildFriendly
        NVARCHAR         Title
        NVARCHAR         Description
        NVARCHAR         BannerImageUrl      "nullable"
        NVARCHAR         EventType           "Physical|Online"
        DATE             StartDate
        DATE             EndDate
        TIME             StartTime
        TIME             EndTime
        BIT              IsPaid              "default 0; all event types"
        DECIMAL          Price               "nullable; required when IsPaid"
        INT              ViewCount           "default 0"
        INT              ShareCount          "default 0"
        INT              InquiryCount        "default 0"
    }

    EVENT_AMENITIES {
        UNIQUEIDENTIFIER EventId PK "also FK; ON DELETE CASCADE"
        NVARCHAR         Amenity PK "8 values; None cannot coexist"
    }

    EVENT_PAYOUT_METHODS {
        UNIQUEIDENTIFIER EventId      PK "also FK; ON DELETE CASCADE"
        NVARCHAR         PayoutMethod PK "Cash | Digital; paid events only"
    }

    EVENT_BOOKINGS {
        UNIQUEIDENTIFIER BookingId        PK
        UNIQUEIDENTIFIER EventId          FK
        NVARCHAR         BookerName            "no FK; free text"
        NVARCHAR         BookerEmail
        NVARCHAR         BookerMobile          "nullable"
        INT              TicketCount           ">= 1"
        NVARCHAR         PaymentMethod         "CreditCard|Twint|Cash|Free"
        NVARCHAR         PaymentStatus         "Pending|Paid|Failed"
        NVARCHAR         PaymentReference      "nullable; gateway ref"
        DECIMAL          TotalAmount           ">= 0; price*count snapshot"
        NVARCHAR         Status                "Confirmed|Cancelled"
        DATETIME2        CancelledAtUtc        "nullable"
    }

    EVENT_BOOKING_TICKETS {
        UNIQUEIDENTIFIER TicketId      PK
        UNIQUEIDENTIFIER BookingId     FK "ON DELETE CASCADE"
        UNIQUEIDENTIFIER EventId       FK "denormalised"
        INT              TicketNumber     "1..N; UNIQUE with BookingId"
        NVARCHAR         AttendeeName     "no FK; free text"
    }

    BOOKINGS {
        UNIQUEIDENTIFIER BookingId       PK
        UNIQUEIDENTIFIER ProviderId      FK
        UNIQUEIDENTIFIER PetParentId     FK
        UNIQUEIDENTIFIER ServiceId       FK   "scopes capacity + closures"
        NVARCHAR         ServiceCategory      "denormalised snapshot"
        NVARCHAR         SubCategory          "denormalised snapshot"
        DATE             BookingDate
        TIME             StartTime
        TIME             EndTime
        NVARCHAR         Status               "Confirmed|Cancelled|Completed|NoShow"
        DATETIME2        CancelledAtUtc       "nullable; required when Status=Cancelled"
    }
```

> The diagram renders inline on GitHub, Azure DevOps, GitLab, and any
> Mermaid-aware markdown viewer (VS Code with the Markdown All in One
> extension, IntelliJ, Obsidian, etc.). It is intentionally text-based so
> the schema stays version-controlled with the SQL.

## Provider schema

### `Provider.ProviderAuthIdentities`
Firebase-authenticated sign-up row, created BEFORE the provider has filled
in personal details. The Pawfront API verifies the Firebase ID token and
upserts on `FirebaseUserId`.

- `ProviderAuthIdentityId` — PK for step 1.
- `ProviderId` — nullable until step 2 (`Provider.CompleteProviderProfile`) runs.
- `FirebaseUserId` — Firebase UID, globally unique.
- `AuthProvider` — `Google`, `Apple`, or `EmailPassword`.
- `FirebaseProviderId`, `FirebaseTenantId` — optional raw Firebase metadata.
- `Email`, `IsEmailVerified`, `DisplayName`, `FirebasePhoneNumber`, `PhotoUrl`.
- `SignUpStatus` — `FirebaseAuthenticated` or `ProviderProfileCompleted`.
- `LastSignedInAtUtc`, `CreatedAtUtc`, `UpdatedAtUtc`.

### `Provider.Providers`
The main provider entity. `ProviderId` is the primary correlation key used
throughout the application.

- `ProviderId` — PK.
- `ProviderAuthIdentityId` — UNIQUE FK back to step 1.
- `FirstName`, `LastName`, `Gender` (CHECK list).
- `MobileCountryCode`, `MobileNumber` — UNIQUE together.
- `DateOfBirth`.
- `MobileVerifiedAtUtc` — populated after OTP validation.
- `OnboardingStatus` — `MobileVerificationPending` → `MobileVerified`.

### `Provider.ProviderPhotos`
General provider photo gallery (not tied to a service). One row per uploaded
image — a provider can have many photos. Inserted by
`POST /api/v1/providers/{providerId}/photos` (multipart) via sproc
`Provider.AddProviderPhoto`; listed via `Provider.ListProviderPhotos`; removed
one-by-one via `Provider.DeleteProviderPhoto`.

- `ProviderPhotoId` — PK.
- `ProviderId` — FK → `Provider.Providers`, `ON DELETE CASCADE` (deleting a
  provider removes its photo rows; blobs themselves cleaned up best-effort).
- `PhotoUrl` — NVARCHAR(1000), the blob URL in the `provider-photos` folder
  of the shared `provider-images` container.
- `CreatedAtUtc` — upload timestamp.

### `Provider.ProviderDeviceTokens`
FCM device tokens captured at sign-in. Linked to the auth identity first;
back-filled with `ProviderId` once the profile is completed.

- `ProviderDeviceTokenId` — PK.
- `ProviderAuthIdentityId` — required FK.
- `ProviderId` — nullable FK.
- `FcmToken` — UNIQUE.
- `DeviceId`, `DevicePlatform` (`Android`|`iOS`), `IsActive`, `LastSeenAtUtc`.

### `Provider.ProviderMobileOtps`
Mobile OTP send + validation log. OTP codes are stored as SHA-256 hashes.

- `ProviderMobileOtpId` — PK returned to the client.
- `ProviderId` — FK.
- `MobileCountryCode`, `MobileNumber` — snapshot at send time.
- `OtpCodeHash` — `VARBINARY(32)`.
- `OtpCodeLastTwo` — display hint.
- `ValidationStatus` — `Pending`, `Validated`, or `Expired`.
- `FailedAttemptCount`.
- `DateSentUtc`, `DateValidatedUtc`, `ExpiresAtUtc`.

### `Provider.ProviderServiceRegistrations`
Geo-indexed registration row pinning the provider to **one** service
category. A `UNIQUE (ProviderId)` constraint enforces the
"one-service-per-provider" rule — attempting to register a second category
throws `THROW 51011` (mapped to `409 ServiceCategoryConflict`).

- `ProviderServiceRegistrationId` — PK.
- `ProviderId` — UNIQUE FK.
- `ServiceCategory` — `PetSitter`, `PetGroomer`, `PetTrainer`,
  `PetAdoptionAndSale`, or `Vet`.
- `SubCategory` — category-specific (e.g. `PetHotel`, `FreelancePetSitter`).
- `Latitude`, `Longitude` — geo filter index for discovery.

### `Provider.ProviderServices`
The per-service catalog — **one row per (provider, ServiceType)**.
Upserted automatically when a provider saves an offering: PetSitter with
both DayCare and NightStay produces two rows. Closures, bookings, and slot
queries all reference a specific `ServiceId` from this table. Rows are
**soft-deactivated** (`IsActive = 0`) rather than deleted, so historical
closures/bookings retain referential integrity.

- `ServiceId` — PK; minted on first upsert.
- `ProviderId` — FK with `ON DELETE CASCADE`.
- `ServiceCategory` — `PetSitter`, `PetGroomer`, `PetTrainer`, or `Vet`
  (PetAdoptionAndSale has no offering and no rows here).
- `SubCategory` — denormalised for read convenience.
- `ServiceType` — one of `DayCare`, `NightStay`, `GroomingSession`,
  `TrainingSession`, `VetAppointment`. Compatible with `ServiceCategory`
  via a check constraint.
- `IsActive`.
- UNIQUE `(ProviderId, ServiceType)`.

### `Provider.ProviderWeeklyAvailability`
Seven-day recurring schedule. Composite PK `(ProviderId, DayOfWeek)`.
Cascading delete from `Providers`.

- `DayOfWeek` — `TINYINT 0..6` (`0 = Sunday`).
- `IsOpen` — when `false`, all time columns must be NULL (CHECK).
- `StartTime`, `EndTime` — required when `IsOpen = true`.
- `BreakStartTime`, `BreakEndTime` — optional single break, must lie inside
  `[StartTime, EndTime]`.

### `Provider.ProviderClosures`
Per-service vacation / sick-leave windows. A closure on DayCare does **not**
block NightStay slots/bookings.

- `ClosureId` — PK.
- `ProviderId` — FK with `ON DELETE CASCADE` (denormalised for fast
  filter; the source of truth is `ServiceId`).
- `ServiceId` — FK → `ProviderServices`.
- `StartDate`, `EndDate` — `EndDate >= StartDate`.
- `StartTime`, `EndTime` — both NULL = full-day closure across the range;
  both set requires `StartDate = EndDate` (partial-day on one day).
- `Reason` — optional, ≤500 chars.

### `Provider.ProviderPayoutMethods`
Junction. A provider can enable `Cash`, `Digital`, both, or neither.

- PK `(ProviderId, PayoutMethod)`.
- `PayoutMethod` — `Cash` or `Digital` (CHECK).

### `Provider.ProviderCancellationPolicies`
One row per provider. Nullable cancellation window.

- `ProviderId` — PK + FK.
- `MinimumHoursBeforeCancellation` — `NULL`, `24`, `48`, `72`, or `96` (CHECK).

## Parent schema

### `Parent.ParentAuthIdentities`
Pet-parent Firebase login identity. One row per Firebase user. Created on
the first call to `POST /api/v1/parent-onboarding/firebase-auth`; the row
is updated on every subsequent login (refreshes display name, photo, etc.)
via `Parent.SaveParentAuthIdentity` (`UPDLOCK + HOLDLOCK` on the lookup).

- `ParentAuthIdentityId` — PK.
- `PetParentId` — nullable FK → `Parent.PetParents`. Stays null until the
  parent completes the profile step (not yet built). Unique when set.
- `FirebaseUserId` — UNIQUE; from the `user_id`/`sub` claim.
- `AuthProvider` — `Google`, `Apple`, or `EmailPassword` (CHECK).
- `Email` (always populated from the JWT), `IsEmailVerified`, `DisplayName`,
  `FirebasePhoneNumber`, `PhotoUrl`, `FirebaseTenantId`, `FirebaseProviderId`.
- `SignUpStatus` — `FirebaseAuthenticated` → `ParentProfileCompleted`
  (CHECK). Flipped when the parent profile endpoint lands.
- `LastSignedInAtUtc`, `CreatedAtUtc`, `UpdatedAtUtc`.

### `Parent.ParentDeviceTokens`
FCM tokens per device for the pet-parent app. Many tokens per parent (one
per device). Same upsert path as `Provider.ProviderDeviceTokens`: the same
`FcmToken` value updates the row in place; a new value inserts.

- `ParentDeviceTokenId` — PK.
- `ParentAuthIdentityId` — FK → `Parent.ParentAuthIdentities`.
- `PetParentId` — nullable FK → `Parent.PetParents`. Backfilled when the
  parent's profile is created.
- `FcmToken` — UNIQUE; up to 2048 chars.
- `DeviceId`, `DevicePlatform` (`Android` or `iOS`, CHECK).
- `IsActive`, `LastSeenAtUtc`, timestamps.

### `Parent.PetParents`
Pet-parent profile row. Created by `POST /api/v1/parent-onboarding/profile`
(sproc `Parent.CompletePetParentProfile`), one per `ParentAuthIdentityId`.
Idempotent — if the auth identity already has a `PetParentId`, the sproc
returns the existing row without re-inserting. Booking creation FKs to
this table, so a profile must exist before any booking can be placed.

- `PetParentId` — PK.
- `ParentAuthIdentityId` — UNIQUE + FK → `Parent.ParentAuthIdentities`.
  Links the profile back to the Firebase login.
- `FirstName`, `LastName`, `Gender` (5-value CHECK: `Male`, `Female`,
  `NonBinary`, `Other`, `PreferNotToSay`).
- `MobileCountryCode` + `MobileNumber` — UNIQUE composite. Plus a nullable
  `MobileVerifiedAtUtc` reserved for the eventual OTP flow.
- `DateOfBirth`.
- `AddressLine`, `Latitude`, `Longitude` (CHECK -90..90 / -180..180,
  `DECIMAL(9,6)`), `ZipCode`, `City`.
- `Description` (NVARCHAR(2000)) — free-text profile blurb captured at
  registration.
- `ProfilePhotoUrl` — nullable, NVARCHAR(1000). Set by
  `POST /api/v1/pet-parents/{petParentId}/profile-image` via the sproc
  `Parent.UpdatePetParentProfilePhoto`. The blob itself lives under
  `pet-parent-profile-photos/<petParentId>/<guid>.<ext>` in the shared
  `provider-images` container.
- Timestamps.

> Legacy `Parent.PetParents` rows (predating the profile schema) survive
> the migration with nullable profile columns. New rows always have every
> field populated because the sproc requires them.

### `Parent.Pets`
Pets owned by a parent. One row per pet; a parent can have many. Inserted
by `POST /api/v1/pet-parents/{petParentId}/pets` (sproc
`Parent.AddPetParentPet`).

- `PetId` — PK.
- `PetParentId` — FK → `Parent.PetParents`.
- `PetType` — `Dog`, `Cat`, `Hamster`, or `GuineaPig` (CHECK).
- `PetName`, `Breed`.
- `Gender` — `Male` or `Female` (CHECK).
- `DateOfBirth`.
- `Weight` — `DECIMAL(5,2)`, CHECK > 0. Unit is implicit (kg).
- `MicrochipId` — nullable. UNIQUE filtered index (NULLs allowed to coexist).
  Microchip ids are globally unique per ISO 11784/11785, so collisions
  across pet parents return 409 `MicrochipIdAlreadyExists`.
- `Description` — nullable free-text.
- `VaccinationStatus` — nullable, `Vaccinated | NotVaccinated` (CHECK).
  Populated by `PATCH /pets/{petId}/medical-info`.
- `SterilizationStatus` — nullable, `Sterilized | Intact` (CHECK). Same
  PATCH endpoint. "Sterilized" covers both neuter (male) and spay (female);
  the mobile client renders it as "Neutered/Spayed".
- `MedicalHistory` — nullable NVARCHAR(MAX). Free text.
- `Temperament` — nullable, `Anxious | Friendly | Aggressive` (CHECK).

### `Parent.PetPhotos`
Pet photos (gallery). One row per uploaded image — a pet can have many
photos. Inserted by `POST /api/v1/pets/{petId}/photos` (multipart) via
sproc `Parent.AddPetPhoto`.

- `PetPhotoId` — PK.
- `PetId` — FK → `Parent.Pets`, `ON DELETE CASCADE` (deleting a pet
  removes its photo rows; blobs themselves are not cleaned up).
- `PhotoUrl` — NVARCHAR(1000), the blob URL in the `pet-photos`
  folder of the shared `provider-images` container.
- Timestamps.

### `Parent.PetParentPhotos`
General pet-parent photo gallery (not tied to a pet). One row per uploaded
image — a parent can have many photos. Inserted by
`POST /api/v1/pet-parents/{petParentId}/photos` (multipart) via sproc
`Parent.AddPetParentPhoto`; listed via `Parent.ListPetParentPhotos`; removed
one-by-one via `Parent.DeletePetParentPhoto`.

- `PetParentPhotoId` — PK.
- `PetParentId` — FK → `Parent.PetParents`, `ON DELETE CASCADE` (deleting a
  parent removes its photo rows; blobs themselves cleaned up best-effort).
- `PhotoUrl` — NVARCHAR(1000), the blob URL in the `pet-parent-photos` folder
  of the shared `provider-images` container.
- `CreatedAtUtc` — upload timestamp.

## Event schema

### `Event.Events`
Provider-created events (adoption drives, training sessions, charity,
etc.). Physical-event extension data (**capacity only**) lives in
**Cosmos** (`Events` container), keyed by the same `EventId`. Online events
have no Cosmos doc. **Ticketing (`IsPaid`/`Price`) lives on this SQL row,
not Cosmos**, so it's returned for every event type — online events can be
paid too.

- `EventId` — PK.
- `ProviderId` — FK.
- `EventCategory` — one of 8 values: `AdoptionAndRescue`, `PetTraining`,
  `Charity`, `Volunteering`, `HealthAndWellness`, `SocialAndCultural`,
  `OutdoorActivities`, `ParentEducation`.
- `IsChildFriendly`.
- `Title`, `Description` (NVARCHAR(MAX)), `BannerImageUrl` (nullable).
- `EventType` — `Physical` or `Online`.
- `StartDate <= EndDate` (CHECK), `StartTime`, `EndTime`.
- `IsPaid` (default 0) / `Price` (nullable DECIMAL(18,2)) — ticketing for any
  event type. `CK_Events_Ticketing` enforces `Price IS NULL` for free events
  and a non-negative `Price` for paid ones.
- `ViewCount`, `ShareCount`, `InquiryCount` — engagement counters
  ("PawPrints"). Default 0; atomically incremented by
  `Event.IncrementEventCounter` from the three public increment
  endpoints. Read in `Event.GetEventMetrics`, and surfaced read-only on
  every event read (all five event-returning sprocs append them to
  result set 1) under a `pawPrints` object on the API's `EventResponse`.

### `Event.EventAmenities`
Junction listing the venue amenities for an event.

- PK `(EventId, Amenity)` with `ON DELETE CASCADE`.
- `Amenity` — one of `FreeParking`, `PaidParking`, `Restrooms`,
  `DrinkingWater`, `FoodAndBeverage`, `SeatingAreas`, `FirstAidBooth`,
  `None`. The C# layer rejects `None` together with any other amenity.

### `Event.EventPayoutMethods`
Junction listing how the event organiser wants ticket proceeds paid out.
Written by `Event.SaveEventPayoutMethods` (`POST /events/{eventId}/payout-methods`).

- PK `(EventId, PayoutMethod)` with `ON DELETE CASCADE`.
- `PayoutMethod` — `Cash` or `Digital` (one or more rows per event).
- **Paid events only** — the sproc throws `51099` for a free event
  (`IsPaid = 0`) and `51098` when the event id is unknown.

### `Event.EventBookings`
Ticket purchases against a physical event. **Booker identity is free text**
— anyone with a Firebase login can buy tickets for any names. There is no
FK to `Parent.PetParents`. Capacity is enforced inside
`Event.CreateEventBooking` by SUMming `TicketCount` over confirmed rows for
the event under `UPDLOCK + HOLDLOCK`.

- `BookingId` — PK.
- `EventId` — FK → `Event.Events`.
- `BookerName`, `BookerEmail`, `BookerMobile` — free text; the contact for
  the buyer of the tickets.
- `TicketCount` — denormalised count of child ticket rows; feeds the
  race-safe capacity check.
- `PaymentMethod` — `CreditCard`, `Twint`, `Cash`, or `Free` (CHECK).
  `CreditCard`/`Twint` settle on an external gateway; `Cash` is collected
  in person and `Free` carries no charge.
- `PaymentStatus` — `Pending`, `Paid`, or `Failed`. Created as `Pending`;
  flipped by the gateway callback (`Event.ConfirmEventBookingPayment`).
- `PaymentReference` — external gateway reference, populated on callback.
- `TotalAmount` — snapshot of `price × TicketCount` at booking time; 0 for
  free events.
- `Status` — `Confirmed` or `Cancelled`. `Cancelled` requires
  `CancelledAtUtc` (CHECK). Cancellation/refund flow is not built yet.

### `Event.EventBookingTickets`
One row per attendee / printed ticket. The GET endpoint returns one entry
per row here — if 4 tickets were bought, 4 rows come back.

- `TicketId` — PK.
- `BookingId` — FK with `ON DELETE CASCADE`.
- `EventId` — denormalised so per-event listings don't need a join back.
- `TicketNumber` — 1..N within the booking; UNIQUE with `BookingId`.
- `AttendeeName` — free text, not validated against any user table.

## Booking schema

### `Booking.Bookings`
Confirmed booking records, scoped by `ServiceId`. Capacity check + insert
is race-safe inside `Booking.CreateBooking` (UPDLOCK + HOLDLOCK on the
overlap-count query).

- `BookingId` — PK.
- `ProviderId` — FK.
- `PetParentId` — FK.
- `ServiceId` — FK → `ProviderServices`. **All capacity and closure logic
  is scoped by this column.** DayCare and NightStay each get an
  independent capacity bucket.
- `ServiceCategory`, `SubCategory` — denormalised snapshot so historical
  bookings remain meaningful even if the provider deregisters or changes
  sub-category.
- `BookingDate`, `StartTime`, `EndTime` — `StartTime < EndTime` (CHECK).
- `Status` — 6-state lifecycle: `CREATED` → `CONFIRMED` → `COMPLETED`, with
  `APPROVAL_NEEDED` (schedule change pending) and the two terminal cancel
  states `PROVIDER_CANCELLED` / `PARENT_CANCELLED` (which require
  `CancelledAtUtc`, CHECK). New app bookings default to `CREATED`; custom
  walk-ins start `CONFIRMED`. **A booking holds its capacity slot in every
  status except the two cancelled ones** — that's the predicate every
  capacity / closure-conflict / active-status / slot query uses.

### `Booking.BookingStatusHistory`
Append-only audit trail of every status change on a booking. One row per
transition (plus a seeded creation row with `FromStatus = NULL`). Written
atomically with the status update by `Booking.UpdateBookingStatus`,
`Booking.CancelBooking`, and the two create sprocs.

- `BookingStatusHistoryId` — PK.
- `BookingId` — FK → `Booking.Bookings`, `ON DELETE CASCADE`.
- `FromStatus` — NULL only for the creation entry.
- `ToStatus` — the new status.
- `ChangedByActor` — `Provider`, `Parent`, or `System` (CHECK).
- `ChangedByActorId` — the ProviderId / PetParentId; NULL for System rows.
- `Note` — optional free-text reason.
- `ChangedAtUtc` — when the change happened.

## User-defined types

### `Provider.ServiceIdList` (table type)
A single-column TVP used by `Provider.CreateClosures` to accept an array
of `ServiceId`s in one call. Sent over from .NET as a `SqlParameter` with
`TypeName = "Provider.ServiceIdList"` and `SqlDbType.Structured`.

### `Event.EventBookingAttendeeNames` (table type)
TVP used by `Event.CreateEventBooking` to receive the attendee-name list
in one round-trip. Columns: `TicketNumber INT PK`, `AttendeeName NVARCHAR(200)`.
Sent from .NET as `SqlParameter` with
`TypeName = "Event.EventBookingAttendeeNames"` and `SqlDbType.Structured`.

## Stored procedures

Naming pattern: `<Schema>.<Verb><Noun>`. All sprocs use `CREATE OR ALTER`
so the deploy script always reflects the latest version.

| Sproc | Notes |
|-------|-------|
| `Provider.SaveProviderAuthIdentity` | Step 1: upsert Firebase identity + optional device token. |
| `Provider.CompleteProviderProfile`  | Step 2: create `Providers` row, link back, promote `SignUpStatus`. |
| `Provider.GetProviderProfile`       | Read-back of the persisted personal info. |
| `Provider.CreateMobileVerificationOtp` | Generate a hashed OTP and persist with expiry. |
| `Provider.VerifyMobileVerificationOtp` | Validate OTP; flip `MobileVerifiedAtUtc` + `OnboardingStatus`. |
| `Provider.SaveProviderServiceRegistration` | Insert/update the one-per-provider category registration. Throws `51011` on category conflict. |
| `Provider.UpsertProviderService`    | Mint a `ServiceId` (or reactivate an existing row) for a given (ProviderId, ServiceType). |
| `Provider.DeactivateProviderService`| Flip `IsActive = 0` for one (ProviderId, ServiceType). |
| `Provider.ListProviderServices`     | Returns active rows; `@IncludeInactive = 1` returns all. |
| `Provider.GetProviderService`       | Point-read by `ServiceId`. |
| `Provider.SaveProviderWeeklyAvailability` | Atomic replace of all 7 day rows. |
| `Provider.GetProviderWeeklyAvailability`  | Read all rows for a provider. |
| `Provider.SaveProviderPayoutMethods` | Replace the junction rows. |
| `Provider.SaveProviderCancellationPolicy` | Upsert one row. |
| `Provider.GetProviderPolicy`        | Returns payout + cancellation in one round-trip (two result sets). |
| `Provider.GetProviderOnboardingStatus` | Four-result-set aggregate consumed by the onboarding-status orchestrator. |
| `Parent.GetPetParentOnboardingStatus` | Two-result-set aggregate (parent + auth flags; per-pet medical-completion) consumed by the parent onboarding-status orchestrator. |
| `Parent.CreateMobileVerificationOtp` | Inserts a hashed OTP row in `Parent.ParentMobileOtps` (10-minute expiry). |
| `Parent.VerifyMobileVerificationOtp` | Validates the OTP, flips `Parent.PetParents.MobileVerifiedAtUtc` on success. Always returns a row with `IsValidated` + `ValidationStatus`. |
| `Provider.CreateClosures`           | All-or-nothing batch insert. Takes a `Provider.ServiceIdList` TVP; race-safe conflict check vs `Booking.Bookings`. Throws `51070`/`51072`/`51075`. |
| `Provider.ListClosures`             | Optional `@ServiceId`, `@From`, `@To` filters. |
| `Provider.DeleteClosure`            | Reopen a single closure row. Throws `51071` if missing. |
| `Provider.GetActiveClosuresForDate` | Per-service lookup used by the slot service and booking validator. |
| `Booking.CreateBooking`             | Validates ServiceId belongs to provider + active; race-safe capacity check per service; insert. |
| `Booking.GetBooking`                | Point-read by `BookingId`. |
| `Booking.CancelBooking`             | Booker-only cancellation; throws `51063/51064/51065`. |
| `Booking.ListBookingsByProvider`    | Optional `@ServiceId` + `@BookingDate` filters. |
| `Booking.ListBookingsByPetParent`   | Full history for a pet parent. |
| `Booking.GetBookingsForDate`        | Used by the slot service to subtract overlaps per service. |
| `Booking.UpdateBookingStatus`       | Role-guarded status change + audit insert in one transaction. Throws `51120`–`51125`. |
| `Booking.ListBookingStatusHistory`  | Full status audit trail for a booking, oldest-first. |
| `Event.CreateEvent`                 | Inserts the SQL row + amenities junction (Cosmos write happens in the API layer for physical events). |
| `Event.GetEvent`                    | Single event + amenities. Result set 1 includes the `ViewCount`/`ShareCount`/`InquiryCount` "PawPrints". |
| `Event.ListEventsByProvider`        | Provider's events. |
| `Event.CreateEventBooking`          | Race-safe ticket purchase. Takes the `EventBookingAttendeeNames` TVP; capacity check + insert booking + insert N ticket rows in one transaction. Throws `51090/51091/51094`. |
| `Event.GetEventBooking`             | Booking row + all tickets (two result sets). |
| `Event.ConfirmEventBookingPayment`  | External gateway callback. Sets `PaymentStatus` + `PaymentReference`. Idempotent for redelivery; throws `51092/51093/51094`. |
| `Event.IncrementEventCounter`       | Atomic `+1` on `ViewCount` / `ShareCount` / `InquiryCount`. Returns the updated row. Throws `51096/51097`. |
| `Event.GetEventMetrics`             | Organiser-only. Counters + confirmed-paid booking aggregates (`ConfirmedAttendees`, `Earnings`). Throws `51095` on ownership miss. |
| `Event.ListEventAttendees`          | Organiser-only. One row per ticket joined to the parent booking; excludes Cancelled bookings. Throws `51095` on ownership miss. |
| `Provider.AddProviderPhoto`         | Insert one general provider gallery photo row. Throws `51110` if the provider is missing. |
| `Provider.ListProviderPhotos`       | List the provider's gallery photos, oldest-first. |
| `Provider.DeleteProviderPhoto`      | Delete one photo scoped by `ProviderId` + `ProviderPhotoId`; returns the URL for blob cleanup. Throws `51111` if missing. |
| `Parent.AddPetParentPhoto`          | Insert one general pet-parent gallery photo row. Throws `51212` if the parent is missing. |
| `Parent.ListPetParentPhotos`        | List the parent's gallery photos, oldest-first. |
| `Parent.DeletePetParentPhoto`       | Delete one photo scoped by `PetParentId` + `PetParentPhotoId`; returns the URL for blob cleanup. Throws `51213` if missing. |

Custom THROW codes used by sprocs:

| Code  | Meaning |
|-------|---------|
| 51001 | Provider auth identity not found. |
| 51002 | Provider profile not found (OTP create). |
| 51003 | Provider mobile OTP not found. |
| 51010 | Provider profile not found (service registration). |
| 51011 | Provider already registered under a different category → API `409 ServiceCategoryConflict`. |
| 51020 / 51021 | Provider profile not found (payout / cancellation policy). |
| 51030 | Provider profile not found (event create). |
| 51050 | Provider profile not found (weekly availability save). |
| 51060 | Pet parent not found (booking create). |
| 51061 | Provider not found (booking create). |
| 51062 | No remaining capacity for slot (scoped by ServiceId). |
| 51063 | Booking not found (cancel). |
| 51064 | Only the booker can cancel. |
| 51065 | Booking already cancelled. |
| 51066 | ServiceId is unknown, inactive, or not owned by the provider (booking create). |
| 51070 | Provider profile not found (closure create). |
| 51071 | Provider closure not found (delete). |
| 51072 | One or more ServiceIds unknown/inactive/not owned (closure batch create). |
| 51075 | Empty ServiceId list (closure batch create). |
| 51080 | Provider profile not found (provider service upsert). |
| 51090 | Event not found (event booking create). |
| 51091 | Event is sold out / not enough remaining capacity → API `409 EventSoldOut`. |
| 51092 | Event booking not found (payment confirmation). |
| 51093 | Event booking payment already confirmed with a different result. |
| 51094 | Invalid request (empty attendee list, invalid PaymentStatus). |
| 51095 | Event not found for the requesting provider (organiser dashboard reads). |
| 51096 | Event not found (counter increment). |
| 51097 | Invalid counter type (must be `View`, `Share`, or `Inquiry`). |
| 51110 | Provider not found (provider photo add). |
| 51111 | Provider photo not found (provider photo delete). |
| 51120 | Booking not found (status update). |
| 51121 | Caller is not a party to the booking → API `403 Forbidden`. |
| 51122 | Status not permitted for this actor → API `400 BookingStatusNotAllowed`. |
| 51123 | Booking is terminal, no further changes → API `409 BookingStatusTerminal`. |
| 51124 | Booking already in the requested status → API `409 BookingStatusUnchanged`. |
| 51125 | Invalid actor or status value (status update). |
| 51212 | Pet parent not found (parent photo add). |
| 51213 | Pet parent photo not found (parent photo delete). |

## Deployment

Re-run safely after any change:

```powershell
sqlcmd -S <server> -d <database> -U <user> -P "<password>" `
       -i Deployment\DeployAll.sql
```

Or open `Deployment/DeployAll.sql` in SSMS / Azure Data Studio and Execute.

When **adding or modifying** a table or sproc:

1. Edit the standalone file under `Tables/` or `StoredProcedures/` (the
   "fresh install" definition).
2. Mirror the change inside `Deployment/DeployAll.sql` so re-runs against
   existing dev databases pick it up idempotently. For schema migrations
   that require destructive operations (adding a NOT NULL FK column,
   etc.), wrap the migration in an idempotent guard — e.g. check
   `sys.columns` before `ALTER TABLE ... ADD`.

Both files must stay in sync.
