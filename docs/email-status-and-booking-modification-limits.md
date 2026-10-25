# Parent email status and booking updates

The parent API exposes anonymous `POST /auth/email-status`, with `/api/v1/auth/email-status` as an alias. Both routes accept:

```json
{ "email": "user@example.com" }
```

Successful responses use the standard envelope:

```json
{ "success": true, "data": { "registered": true }, "error": null }
```

`registered` is true only when the email belongs to an existing, non-deleted pet-parent profile. Auth identities without a parent profile and provider-only accounts return false. Lookup trims surrounding whitespace and compares case-insensitively, including on databases with case-sensitive defaults. No Firebase lookup or email delivery is performed.

Invalid or missing email values return HTTP 400 (`InvalidEmail`). Both route aliases share a fixed-window limit of 10 calls per IP per hour; changing the submitted email does not reset it. Excess calls return HTTP 429 (`RateLimitExceeded`) and `Retry-After`. There is no request queue. The IP comes from the server connection, rather than a client-supplied device ID or untrusted forwarding header. Counters are held in each API instance and reset on restart; a multi-instance deployment needs a shared gateway limit for a deployment-wide cap. Responses specify `Cache-Control: no-store`. On any failure, clients should use the neutral sign-in text.

Night-stay creation accepts dates without handover times. Fixed provider drop-off and pick-up times are still snapshotted and take precedence over request times. When the provider has no fixed times, optional request times are retained; omitted values are stored and returned as null in create, list and detail responses. Internal date calculations use midnight when a time is absent; this does not populate the booking's handover fields.

Both APIs' service and night-stay booking detail responses now include:

| Field | Becomes true when history contains |
| --- | --- |
| `isParentModificationDone` | `PROVIDER_ACCEPTED_MODIFICATION` |
| `isProviderModificationDone` | `PARENT_ACCEPTED_MODIFICATION` |
| `isModificationDone` | Either accepted status, preserving existing behavior |

Each party may have one of their own proposals accepted. Declined, pending and expired proposals do not consume that allowance. Accepting the other party's proposal does not consume the responder's allowance. A party whose change was already accepted receives HTTP 409 (`ModificationLimitReached`) on another modification request. Enforcement reads history while holding the booking row lock. The existing `providerModificationUsed` field now follows accepted provider changes as well, so it agrees with `isProviderModificationDone`.

The database retains limit error numbers 51990 (service) and 51991 (night stay). Error 51152 already identifies an expired service modification response and is unchanged.

Deploy `database/Pawfront.Database/Deployment/DeployAll.sql` before publishing both APIs. The script makes handover columns nullable on existing installations and updates the detail projections and modification guards. The email lookup requires no new database object. These changes have been verified locally; this document does not confirm a production deployment.

Regression checks:

```powershell
dotnet run --project tests/Pawfront.Modification.Checks
dotnet run --project tests/Pawfront.EmailBooking.Checks
# Optional SQL checks after deploying to the disposable PawfrontEmailBookingChecks LocalDB database:
dotnet run --project tests/Pawfront.EmailBooking.Checks -- --sql
```
