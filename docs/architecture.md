# Pawfront Backend Architecture

Pawfront is a provider-facing mobile application for pet-industry businesses such as groomers, boarders, walkers, trainers, and clinics. The API is built as a .NET backend with SQL Server for structured operational data and Azure Cosmos DB for flexible documents.

## Project Layout

- `Pawfront.Api`: HTTP API and route registration.
- `Pawfront.Contracts`: request and response DTOs shared by API endpoints.
- `Pawfront.Application`: use-case interfaces and orchestration contracts.
- `Pawfront.Domain`: core business entities and enums.
- `Pawfront.Infrastructure.Sql`: SQL-backed implementations. The starter uses in-memory services until EF Core is added.
- `Pawfront.Infrastructure.Cosmos`: Cosmos document models and future Cosmos-backed implementations.
- `Pawfront.Infrastructure.Azure`: Azure Key Vault secret access and Azure service registration.
- `Pawfront.Database`: SQL Server schema project for structured tables.

## Data Ownership

SQL Server should own records that need constraints, joins, reporting, and transactional consistency:

- service providers and staff accounts
- Firebase-authenticated provider accounts
- services, pricing, add-ons, and working hours
- customers, pets, and normalized search fields
- bookings, invoices, payments, and audit records

Cosmos DB should own flexible or high-variance data:

- rich pet profiles, preferences, behavior notes, and medical note snapshots
- visit notes, intake forms, attachments metadata, and timelines
- provider documents, policy documents, onboarding evidence, and mobile settings

Recommended Cosmos partition keys:

- `pet-profiles`: `/customerId`
- `visit-notes`: `/providerId`
- `provider-documents`: `/providerId`

## Initial API Surface

- `GET /api/v1/health`
- `POST /api/v1/providers`
- `GET /api/v1/providers`
- `POST /api/v1/providers/{providerId}/services`
- `GET /api/v1/providers/{providerId}/services`
- `POST /api/v1/providers/{providerId}/bookings`
- `GET /api/v1/providers/{providerId}/bookings`

## Provider Authentication Start

The provider app signs in with Firebase using Google, Apple, or email/password. Pawfront should treat Firebase as the identity authority and store its own provider account mapping in SQL Server.

All `/api/v1` endpoints require token authentication. The preferred path is Firebase Auth ID tokens: the API validates issuer, audience, lifetime, and signing key using `Firebase:ProjectId`, then requires a Firebase UID claim (`user_id` or `sub`) before executing endpoint handlers.

For Google sign-in token compatibility, the API also supports Google ID token validation using `GoogleJsonWebSignature` and the configured `Firebase:GoogleClientIds` audiences.

Initial provider SQL tables live in the `Provider` schema:

- `Provider.ProviderAuthIdentities`
- `Provider.ProviderDeviceTokens`
- `Provider.Providers`
- `Provider.ProviderMobileOtps`

Lookup key:

- `FirebaseUserId`

The backend should verify the Firebase ID token before trusting the UID, then create or update the matching provider auth row. The Firebase/FCM login step can also upsert the device FCM token into `Provider.ProviderDeviceTokens`. When the provider submits personal details, Pawfront creates the main `ProviderId` in `Provider.Providers` and writes that `ProviderId` back to `Provider.ProviderAuthIdentities` and related device-token rows.

Provider onboarding endpoints:

- `POST /api/v1/provider-onboarding/firebase-auth`
- `POST /api/v1/provider-onboarding/profile`
- `POST /api/v1/providers/{providerId}/mobile-verification/otp`
- `POST /api/v1/providers/{providerId}/mobile-verification/otp/{providerMobileOtpId}/verify`

Provider stored procedures:

- `Provider.SaveProviderAuthIdentity`
- `Provider.CompleteProviderProfile`
- `Provider.CreateMobileVerificationOtp`
- `Provider.VerifyMobileVerificationOtp`

## Azure Secrets

The API registers `IPawfrontSecretProvider` as a singleton. The implementation uses Azure Key Vault at `https://littersoft.vault.azure.net/` with `DefaultAzureCredential`.

Secret names:

- `SQLKey`: SQL Server connection string.
- `CosmosKey`: Cosmos DB key.
- `BlobStorageKey`: Blob Storage key.

SQL infrastructure falls back to Key Vault when `ConnectionStrings:SqlServer` is empty. Cosmos and Blob Storage consumers should use `IPawfrontSecretProvider.GetCosmosKeyAsync` and `GetBlobStorageKeyAsync`.

## Next Implementation Milestones

1. Add authentication using JWT bearer tokens and provider/staff roles.
2. Replace in-memory services with EF Core SQL Server repositories.
3. Add Cosmos SDK repositories for pet profiles, visit notes, documents, and mobile settings.
4. Add validation, error contracts, pagination, and idempotency keys for booking/payment flows.
5. Add integration tests against SQL Server and Cosmos emulator containers.
