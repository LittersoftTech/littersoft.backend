# Account contact uniqueness

Parent and provider accounts belong to independent Firebase projects. A person may use the same email, country code and mobile number in both apps. Firebase UID strings are also scoped to each app.

Mobile uniqueness is enforced by the separate `UX_Providers_MobileNumber` and `UX_PetParents_MobileNumber` indexes. Profile creation and mobile updates must only check their own schema.

Email uniqueness is checked when saving Firebase authentication, within the matching auth identity table only. A different Firebase user in the same app receives HTTP 409 `EmailAlreadyExists`; repeated sign-in for the same user remains allowed. Range locks protect concurrent email registrations. Existing duplicate emails are not merged or deleted; affected users require account reconciliation before their next authentication save.

Deploy the updated `SaveProviderAuthIdentity` and `SaveParentAuthIdentity` procedures (also included in `Deployment/DeployAll.sql`) together with both APIs. Run `tests/account-contact-uniqueness.sql` against a disposable deployed database. It creates synthetic accounts and covers cross-app reuse, repeated sign-in, same-app email inserts/updates and same-app mobile duplicates.

The source already scopes mobile checks to each account type. A continuing cross-app mobile conflict in an environment requires comparing that database's deployed procedures, indexes and triggers with this source.

Deleted providers release both contacts. Email duplicate checks exclude identities whose provider is permanently deleted, and `UX_Providers_MobileNumber` is filtered to `IsDeleted = 0`. Ordinary inactive providers and identities still completing onboarding continue to reserve their contacts.

`DELETE /providers/{id}` anonymises email, mobile and Firebase UID. Signing up again creates a new auth identity and a new provider ID; no lookup or merge uses email or mobile to recover the old account. Bookings, events and payment history retain the old provider ID, and payout methods are removed during deletion. The app remains responsible for deleting the Firebase user.

`Deployment/DeployAll.sql` includes `Deployment/ReleaseDeletedProviderContacts.sql` after the deleted-account columns exist. This idempotent upgrade repairs contact details and Firebase links retained by older deleted records and replaces the mobile index atomically. Apply the database upgrade as well as the API changes. For an existing schema, the focused deployment consists of the repair script followed by `SaveProviderAuthIdentity.sql` and `UpdateProviderMobile.sql`.

Run `dotnet run --project tests/Pawfront.ProviderReregistration.Checks` for in-memory checks and `tests/provider-reregistration.sql` against a disposable deployed database for SQL history-isolation, legacy-record, and duplicate checks.
