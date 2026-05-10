# Pawfront Database

This project owns SQL Server schema scripts for structured Pawfront data.

All provider-related SQL objects live in the `Provider` schema.

The first provider-app table is `Provider.ProviderAuthIdentities`, which stores the Firebase-authenticated sign-up step before the provider has completed personal details.

The second table is `Provider.Providers`, which creates the main `ProviderId` used throughout Pawfront.

The mobile verification table is `Provider.ProviderMobileOtps`, which tracks OTP sends and validations.

Provider device messaging tokens are stored in `Provider.ProviderDeviceTokens`.

## Provider Sign-In Mapping

Firebase remains the identity provider for Google, Apple, and email/password sign-in. The API should verify the Firebase ID token, then upsert or read the matching row by `FirebaseUserId`.

`ProviderAuthIdentities` captures:

- `ProviderAuthIdentityId`: primary key for step one.
- `ProviderId`: nullable until step two completes.
- `FirebaseUserId`: Firebase UID, globally unique in Pawfront.
- `AuthProvider`: `Google`, `Apple`, or `EmailPassword`.
- `FirebaseProviderId`, `FirebaseTenantId`: optional raw Firebase metadata.
- `Email`, `IsEmailVerified`, `DisplayName`, `FirebasePhoneNumber`, `PhotoUrl`: profile details from Firebase.
- `SignUpStatus`: `FirebaseAuthenticated` or `ProviderProfileCompleted`.
- `CreatedAtUtc`, `UpdatedAtUtc`: audit timestamps.

`ProviderDeviceTokens` captures:

- `ProviderDeviceTokenId`: primary key for the device-token row.
- `ProviderAuthIdentityId`: available immediately after Firebase login.
- `ProviderId`: nullable until provider profile creation, then populated.
- `FcmToken`: token used later for Firebase Cloud Messaging.
- `DeviceId`, `DevicePlatform`: optional mobile device metadata.
- `IsActive`, `LastSeenAtUtc`, `CreatedAtUtc`, `UpdatedAtUtc`.

`Providers` captures:

- `ProviderId`: primary key used throughout the application.
- `ProviderAuthIdentityId`: link back to step one.
- `FirstName`, `LastName`, `Gender`, `MobileCountryCode`, `MobileNumber`, `DateOfBirth`.
- `MobileVerifiedAtUtc`: populated once the OTP is validated.
- A unique index on `MobileCountryCode` and `MobileNumber`.

`ProviderMobileOtps` captures:

- `ProviderMobileOtpId`: unique identifier returned to the mobile app.
- `ProviderId`: provider being verified.
- `MobileCountryCode`, `MobileNumber`: snapshot of the number the OTP was sent to.
- `OtpCodeHash`: salted hash of the OTP.
- `DateSentUtc`, `DateValidatedUtc`, `ExpiresAtUtc`.
- `ValidationStatus`: `Pending`, `Validated`, or `Expired`.

Stored procedures:

- `Provider.SaveProviderAuthIdentity`
- `Provider.CompleteProviderProfile`
- `Provider.CreateMobileVerificationOtp`
- `Provider.VerifyMobileVerificationOtp`
