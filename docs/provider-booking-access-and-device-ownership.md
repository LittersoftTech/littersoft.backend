# Provider booking access and push ownership

Modification limits apply independently to each party and count only accepted changes. This applies to single-day and night-stay bookings. Pending, declined and expired proposals do not consume the sender's allowance. `409 ModificationLimitReached` means that party already had one of their proposals accepted. The existing `providerModificationUsed` flag now follows `PARENT_ACCEPTED_MODIFICATION` on booking details, booking lists **and jobs lists**. Parent detail flags follow `PROVIDER_ACCEPTED_MODIFICATION`.

Both provider token registration paths use the same database operation:

- `POST /api/v1/device-tokens`
- `POST /api/v1/provider-onboarding/firebase-auth`, when `fcmToken` is supplied

Send the same stable `deviceId` on both paths. A token is reassigned to the signed-in provider, and registrations for that device under other provider accounts are removed. Rotated tokens under the same owner are deactivated. Other devices belonging to the previous account remain registered. `POST /api/v1/device-tokens/deactivate` still affects only the signed-in account's token, so a delayed logout cannot disable a token that has transferred to a different account.

Provider account deletion removes all tokens identified by either the provider ID or its auth identity. Repeating deletion also removes stale token rows. Deleted profiles cannot register tokens. Push delivery revalidates a provider token's active ownership immediately before calling FCM; tokens captured earlier by an outbox or chat worker are filtered if they have since moved or been deactivated. An unavailable ownership check fails delivery for retry without sending the push.

Provider booking routes now resolve the caller's provider ID from the authenticated Firebase identity. They check both the URL's `providerId` and the booking's stored provider before allowing reads or actions. Another provider's booking returns `403 Forbidden`; an unknown booking returns 404. This covers booking lists, details, status history, terms, lifecycle actions, modifications, payments, prescriptions, evidence, locations, ratings and invoices.

Detail routes include:

- `GET /api/v1/bookings/{bookingId}`
- `GET /api/v1/night-stay-bookings/{bookingId}` (new convenience alias)
- `GET /api/v1/providers/{providerId}/night-stay-bookings/{bookingId}`

Bulk cancellation checks ownership of the complete batch before modifying any booking. A foreign booking causes 403 for the request; ordinary per-booking business failures retain the existing result format. The legacy provider-host `POST /api/v1/bookings/{bookingId}/cancel` now records a provider cancellation using the signed-in provider, ignoring the legacy body's `petParentId`. Provider-host parent-booking lookup returns only the caller's own jobs with that parent.

Deployment repairs existing shared-device registrations by keeping the most recently seen active registration and removing associations belonging to other accounts. It adds a filtered unique index allowing one active token per known device. Registration and deletion use a transaction-owned database lock to serialize account switches and deletion.

Deploy `database/Pawfront.Database/Deployment/DeployAll.sql`, then publish the provider API, Chat API and Functions app. These changes have only been tested locally; no production deployment is implied.

Checks:

```powershell
dotnet run --project tests/Pawfront.ProviderSecurity.Checks -- --sql
dotnet run --project tests/Pawfront.EmailBooking.Checks -- --sql
dotnet run --project tests/Pawfront.Notification.Checks
```

SQL checks use the disposable `PawfrontEmailBookingChecks` LocalDB database. The deployment repair is tested with `tests/provider-device-migration.sql`, which must only run against a disposable database from the repository root.
