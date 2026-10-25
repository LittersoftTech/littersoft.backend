# Provider trust badge

The parent API now returns `badge` at the top level of the provider profile and on every booking-search result:

- `GET /api/v1/providers/{providerId}`: `data.badge`.
- `GET /api/v1/providers/search/day-care`, `/night-stay`, `/groomers`, `/trainers`, `/vets`: `data[i].badge`.

The exact values are `"DocumentsAdded"`, `"MedRegListed"`, or explicit JSON `null`. Only these two public response contracts include the badge. The provider object never includes the document, document URL, document type, review reason, or MedReg lookup reference.

## Rules

| Current category and review | badge |
| --- | --- |
| PetSitter, PetGroomer or PetTrainer: uploaded document accepted by admin | DocumentsAdded |
| Vet: admin confirmed the current uploaded evidence against MedReg | MedRegListed |
| No upload, InReview, NotApproved, replacement awaiting review | null |
| PetAdoptionAndSale | null |

Email/mobile verification has no effect. Vets cannot receive DocumentsAdded, including through the generic Accept action. Replacing a vet document also clears the MedReg badge until the replacement is reviewed. The badge uses the current registered category; changing category cannot carry over an earlier category's acceptance. Deleted providers never receive badges.

Search pages resolve badges in one batch, with repeated provider IDs deduplicated. Values are read live rather than saved against the public profile or offering. No parent app release is required to read the new field.

## Provider's private upload

The repository had a placeholder for provider documents but no implemented document review store. The new SQL `Provider.ProviderTrust` table is the source of review state.

- `POST /api/v1/providers/{providerId}/trust-document`: multipart `file`, PDF/JPEG/PNG/WebP, up to 10 MB. The authenticated caller must own the provider. One current document per provider; replacement resets to InReview, clears prior reasons and MedReg references, and generates a new `revisionId`.
- `GET /api/v1/providers/{providerId}/trust-document`: owner-only evidence and review state. No evidence returns 404 `TrustDocumentNotFound`.

Uploads are supported for sitters, groomers, trainers and vets. The provider cannot submit an acceptance or a MedReg result. Private evidence responses have `Cache-Control: no-store`.

## Admin review

These routes are on the provider API host and require a Firebase ID token with the server-issued custom claim `admin: true`. Raw Google tokens and ordinary provider/parent tokens are rejected.

- `GET /api/v1/admin/provider-trust`: oldest 100 pending documents.
- `GET /api/v1/admin/provider-trust/{providerId}`: current evidence and state.
- `POST /api/v1/admin/provider-trust/{providerId}/review`:

```json
{
  "revisionId": "<current upload revision UUID>",
  "decision": "Accept",
  "rejectionReason": null
}
```

Use `Reject` with a nonblank reason of at most 2000 characters to reject a document. A vet upload can be rejected here; vet acceptance must use the MedReg route.

- `POST /api/v1/admin/provider-trust/{providerId}/medreg`:

```json
{
  "revisionId": "<current vet upload revision UUID>",
  "listed": true,
  "reference": "Admin's MedReg lookup reference"
}
```

The team performs the lookup manually and records its result. The backend does not infer MedReg membership from a document or contact verification. A nonblank lookup reference is required, at most 1000 characters, and stays private. `listed: false` rejects the lookup and removes an existing MedReg badge. Both lookup outcomes require an uploaded vet document with a matching revision.

Missing or changed evidence returns 409 `ProviderTrustConflict`; invalid category/decision/evidence returns 400 `InvalidRequest`. Refresh the evidence before reviewing again. Revision checks prevent accepting an older file after replacement. Upload and review writes serialize per provider. Admin evidence responses have `Cache-Control: no-store`.

## Deployment and checks

Deploy `database/Pawfront.Database/Deployment/DeployAll.sql`, then both API builds. The script creates the table idempotently and installs `Provider.ChangeProviderTrust`. It does not auto-accept existing providers. The new provider-document blob kind uses a `provider-documents` folder in the existing private blob container. Account deletion removes the private SQL trust record alongside the service registration.

Run `dotnet run --project tests/Pawfront.ProviderTrust.Checks`. For SQL checks, deploy the schema to the dedicated `PawfrontIdentityChecks` LocalDB database with the documented `sqlcmd -I -b` options and run with `-- --sql`. Checks cover public wire fields, null badges, batching, admin authorization, all supported categories, replacements, rejected evidence, stale revisions, category changes and independence from email/mobile verification.
