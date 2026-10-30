# Firestore notification inboxes

The apps may now also read through the authenticated
[notification list API](notification-list-api.md). Direct Firestore access remains
available; both approaches use the same documents.

Both audiences use `users/{firebaseUid}/notifications/{notificationId}` in their
own Firebase project. The Functions dispatcher and instant chat background worker
share `InboxPushSender`, which starts a Firestore write alongside FCM and observes
its completion. No Firestore network call runs on the API request path.

The document contains `title`, `body`, `type`, a nested `data` map copied exactly
from the push payload (including routing keys and string values), `createdAt`
(Firestore server timestamp), and `isRead: false`. Its ID is the push payload's
`notificationId`. The Firebase UID comes from the recipient's auth identity, not
the sender or the internal parent/provider GUID.

Writes use create-only semantics. An existing document counts as success without
changing any fields, so overlapping workers and retries preserve read state and
the original creation timestamp. Multiple device tokens produce one document.

## Failure and eligibility behavior

The inbox write is independent of FCM success: attempted pushes still get an
inbox entry if FCM rejects them. A missing UID, permissions problem, timeout, or
other Firestore failure is logged and does not alter the FCM delivery result.
The write has a five-second cancellation budget. It is best effort: there is no
independent durable Firestore retry or historical backfill. A subsequent push
retry attempts the write again; a successful push with a failed inbox write can
leave a missing entry.

Existing push eligibility is preserved. Notifications skipped before the push
sender (including recipients with no active device tokens, and chat suppressed
because the conversation is muted or being viewed) do not create Firestore
entries. This is a mirror of attempted pushes, not a replacement for all SQL inbox
history. Firestore read state is independent of the existing SQL read state.

## Deployment

1. Apply `StoredProcedures/ClaimPendingNotifications.sql` and
   `StoredProcedures/CommitMessageAppend.sql` from `database/Pawfront.Database`
   **before deploying the application binaries**. They append recipient UID
   columns to existing result sets, preserving their earlier column order.
   `Deployment/DeployAll.sql` includes the same changes for full deployments.
2. In both Firebase projects, grant the configured audience service account
   Firestore data access (`roles/datastore.user` or equivalent). Creating a database
   does not by itself ensure the service account has permission to write.
3. Merge `firebase/notifications.firestore.rules` into each project's existing
   rules and publish. Preserve any unrelated rules; ensure no broader rule already
   grants access to these notification paths. Rules grant the authenticated owner
   read access and Boolean `isRead` updates only. Server writes use IAM.
4. Deploy **Pawfront.Functions and Pawfront.ChatApi**. Each reuses its existing
   `Notifications:Provider` and `Notifications:PetParent` credentials/project IDs.
   `Notifications:{audience}:FirestoreDatabaseId` defaults to `(default)`; set it
   only if a named database was created. No new secret or credential is required.
5. In each mobile app, read its own project's collection, order by `createdAt`
   descending, paginate, and update `isRead` directly. Unread filtering combined
   with sorting may require an additional Firestore index.

## Verification

Run `dotnet run --project tests/Pawfront.Notification.Checks` for isolated checks
of actual Firestore write requests and push/write coordination. These checks make
no live Firebase calls. Build both delivery hosts before deployment.

After deployment, trigger one booking and one chat notification for each audience
and confirm the project, recipient UID, payload ID, copy, and data keys. Mark an
entry read and replay the same notification ID to verify that read state and
creation time remain unchanged. Verify with mobile authentication that a user
cannot read another user's notifications or change fields other than `isRead`.
