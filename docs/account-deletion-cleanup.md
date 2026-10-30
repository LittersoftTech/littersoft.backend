# Account deletion cleanup

The existing provider and pet-parent delete APIs now also clean notification history. Routes, request bodies and response envelopes are unchanged.

On successful deletion, the SQL transaction removes the account's notification outbox rows (pending and delivered), device tokens, live chat connections and analytics view records. The deleting account's conversation participation is marked deleted, muted and read. Parent event-waitlist entries are removed. Existing profile/pet anonymisation and cleanup of photos, identity documents, provider settings and service listings remain in place.

The API immediately tries to remove Firestore notifications for up to ten seconds. A remote failure does not undo the committed account deletion: `Notification.AccountNotificationCleanup` retains the original Firebase UID and the Functions app retries every five minutes. After successful inbox cleanup, the queue clears that UID. This does not delete the Firebase authentication user; the existing deletion flow severs its SQL account link.

Firestore cleanup uses bounded batches of 400 and an account-specific deletion marker. Notifications now carry a server-generated `data.recipientId`. Notifications for a replacement account using the same Firebase UID are preserved, while late notification workers cannot recreate entries for the deleted account. Legacy notifications without an account ID are removed when they predate the account deletion. The marker retains only an account ID and deletion timestamp to prevent recreation.

Deletion guards still apply. If unfinished bookings or open support tickets refuse deletion, notification cleanup is not queued and the account's data remains unchanged. Repeated successful deletion is safe. New ordinary and instant notifications targeting deleted accounts are suppressed.

Shared records remain: bookings, payment and invoice history, events and attendee history, support cases, and conversations/messages needed by other participants. The account and pet profiles are anonymised rather than physically deleting rows referenced by those records. Profile/gallery/blob and service-listing deletion retain their existing best-effort behavior.

Deploy the updated `database/Pawfront.Database/Deployment/DeployAll.sql` first, then both APIs, Chat API and the Functions app. This adds the cleanup queue and its five-minute retry timer. Firestore credentials need permission to query and delete inbox documents and read/write account deletion markers. Existing notification-writing transactions also read those markers.

Verified locally with:

```powershell
dotnet run --project tests/Pawfront.Notification.Checks
dotnet run --project tests/Pawfront.ProviderSecurity.Checks -- --sql
```

SQL checks use the disposable `PawfrontEmailBookingChecks` LocalDB database. No production deployment is implied.
