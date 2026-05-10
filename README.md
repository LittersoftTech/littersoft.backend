# Pawfront Backend

Pawfront is a .NET API backend for a provider-facing mobile application in the pet-services industry.

## Stack

- .NET 10
- SQL Server for structured operational data
- Azure Cosmos DB for flexible document data
- Minimal APIs with a layered project structure

## Projects

- `src/Pawfront.Api`
- `src/Pawfront.Application`
- `src/Pawfront.Contracts`
- `src/Pawfront.Domain`
- `src/Pawfront.Infrastructure.Azure`
- `src/Pawfront.Infrastructure.Sql`
- `src/Pawfront.Infrastructure.Cosmos`
- `database/Pawfront.Database`

## Run

```powershell
dotnet run --project .\src\Pawfront.Api\Pawfront.Api.csproj --configfile .\NuGet.Config
```

Set `Firebase:ProjectId` before running against real mobile clients. Every `/api/v1` endpoint requires:

```http
Authorization: Bearer <Firebase ID token>
```

Google ID tokens can also be accepted for Google sign-in when their OAuth client IDs are listed in `Firebase:GoogleClientIds`.

## Azure Key Vault

Secrets are resolved through `IPawfrontSecretProvider`, registered as a singleton by `Pawfront.Infrastructure.Azure`.

Configured vault:

- `https://littersoft.vault.azure.net/`

Configured secrets:

- SQL Server connection string: `SQLKey`
- Cosmos DB key: `CosmosKey`
- Blob Storage key: `BlobStorageKey`

The SQL onboarding implementation uses `ConnectionStrings:SqlServer` when present, otherwise it retrieves `SQLKey` from Key Vault.

## Starter Endpoints

- `GET /api/v1/health`
- `POST /api/v1/providers`
- `GET /api/v1/providers`
- `POST /api/v1/provider-onboarding/firebase-auth`
- `POST /api/v1/provider-onboarding/profile`
- `POST /api/v1/providers/{providerId}/mobile-verification/otp`
- `POST /api/v1/providers/{providerId}/mobile-verification/otp/{providerMobileOtpId}/verify`
- `POST /api/v1/providers/{providerId}/services`
- `GET /api/v1/providers/{providerId}/services`
- `POST /api/v1/providers/{providerId}/bookings`
- `GET /api/v1/providers/{providerId}/bookings`
