# Offering documents and booking requests

The six sitter, groomer and trainer offering endpoints accept omitted or null
`licenseType`, `licenseNumber` and `licenseImageUrl`. Saving without an image
stores a null licence, including when old licence text remains in the request.
All service-offering details still undergo their normal validation. Vet certificate
validation remains mandatory.

Saving the same document preserves its review status. Removing it deletes its
trust evidence and removes the `DocumentsAdded` badge. Replacing it removes the
old approval and submits the new evidence for review; offering saves never grant
approval themselves.

Booking list, Agenda jobs (`GET /providers/{id}/jobs`), and detail responses include
`requestExpiresAtUtc`. For a new request,
it is the earlier of creation plus 24 hours and the original start minus two hours.
For a modification request from either party, also include the proposed start minus
two hours. Night stays retain their existing rule: the earlier of 24 hours after
the request and the end of the original check-in day (midnight UTC). Drop-off time
and proposed times do not shorten that window. The field is null when there is no pending request. Clients
should count down to this value rather than calculating from the original slot.

Late acceptance is rejected immediately at this deadline. The existing scheduled
expiry job expires new requests or reverts modifications to `CONFIRMED` on its next
run. Its predicates use the same deadline; the job schedule controls how quickly
the stored status catches up. Notification deadlines follow the same rules.

Both provider routes accept `POST .../{bookingId}/wait-for-customer`:

- `/api/v1/providers/{providerId}/bookings`
- `/api/v1/providers/{providerId}/night-stay-bookings`

Use the existing start-job body with `location`. Waiting is allowed for confirmed
or `START_JOB` bookings from the scheduled start until closing, including after
the booked slot has ended. With no saved weekly hours, closing defaults to midnight
UTC. A confirmed booking enters `START_JOB`; an existing `START_JOB` keeps its code.
Repeated calls return HTTP 200 without duplicate waiting history or OTP issuance.
An unverified `START_JOB` settles as customer no-show through the existing job.

Deploy `database/Pawfront.Database/Deployment/DeployAll.sql` and both APIs together.
The SQL function, expiry procedures, read procedures, trust synchronisation, and
start procedures must be deployed before the updated APIs. Night-stay acceptance
continues through the original check-in day, subject to the 24-hour timeout.

Validation:

```text
dotnet run --project tests/Pawfront.ServicesModule.Checks
dotnet run --project tests/Pawfront.ServicesModule.Checks -- --sql
```

The SQL mode uses the disposable LocalDB database `PawfrontServicesModuleChecks`,
which must first have the current schema deployed. It creates synthetic accounts
and bookings. The checks cover offering validation, API request shapes, mandatory
vet certificates, SQL list/detail deadlines, both proposal directions, late response
rejection, automatic expiry, badge removal, and both HTTP wait routes with retries.
