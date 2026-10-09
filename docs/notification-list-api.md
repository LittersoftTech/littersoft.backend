# Notification list API

Both the Provider API and PetParent API expose:

```http
GET /api/v1/notifications?take=20
Authorization: Bearer <Firebase ID token>
```

The Provider API reads the Provider Firebase project; the PetParent API reads the
Parent Firebase project. Each reads only `users/{authenticatedFirebaseUid}/notifications`.
There is no caller-supplied recipient ID or audience. A raw Google sign-in token
is not accepted: exchange it through Firebase Authentication and use the Firebase
ID token. Existing issuer/audience/signature checks on each host still apply.

## Pagination and response

`take` defaults to 20 and must be 1–100. Results are ordered by `createdAt`
descending, then document ID descending to make equal timestamps deterministic.

```json
{
  "success": true,
  "data": {
    "items": [
      {
        "notificationId": "a3eb98c9-5f27-4776-bbcf-cd2801b3f642",
        "title": "New message",
        "body": "You have a new message.",
        "type": "MESSAGE_RECEIVED",
        "data": {
          "notificationId": "a3eb98c9-5f27-4776-bbcf-cd2801b3f642",
          "type": "MESSAGE_RECEIVED",
          "conversationId": "conversation-id"
        },
        "createdAt": "2026-09-27T12:00:00+00:00",
        "isRead": false
      }
    ],
    "nextCursor": "opaque-pagination-token"
  },
  "error": null
}
```

The sample `data` map is abbreviated; responses preserve all stored push data keys.
An empty inbox returns `items: []` and `nextCursor: null` with HTTP 200.

Pass the returned cursor unchanged for the next page:

```http
GET /api/v1/notifications?take=20&cursor=<nextCursor>
```

Stop when `nextCursor` is null. Refresh from the first page to see newly arrived
notifications. Cursors are scoped to the authenticated user and API audience;
they remain usable if the previous page's last document is deleted. They are
pagination positions, not authorization tokens. The response uses `Cache-Control:
no-store`. GET does not mark notifications read.

Errors: HTTP 400 for invalid page size/cursor; HTTP 401 for missing or rejected
Firebase authentication; HTTP 503 with `NotificationsUnavailable` when Firestore
is unavailable or misconfigured. Reads have a ten-second cancellation budget.

## Hosting configuration

Deploy **Pawfront.Api and Pawfront.PetParentApi** for these routes. In addition to
their existing Firebase token-validation configuration, the API hosts now need
server-side Firestore credentials:

| Host | Settings prefix | Default secret name |
| --- | --- | --- |
| Provider API | `Notifications:Provider` | `FirebaseProviderServiceAccount` |
| Parent API | `Notifications:PetParent` | `FirebaseParentServiceAccount` |

Each prefix supports `ProjectId`, `CredentialsSecretName` (existing Key Vault or
local secret provider), `CredentialsFilePath` (optional local alternative), and
`FirestoreDatabaseId` (defaults to `(default)`). The project must match that host's
`Firebase:ProjectId`. Azure environment settings use double underscores, e.g.
`Notifications__PetParent__CredentialsSecretName`. The host identity must be able
to read the configured secret; the service account needs Firestore read access.
Reuse the corresponding audience credentials already used by the push workers.

The Parent API's `littersoft-petparent - Web Deploy` publish profile runs
`Configure-PetParent.ps1 -Apply` before publishing. Install Azure CLI and run
`az login` on the publishing machine first. The script sets Production and enables
Key Vault, configures the Parent inbox, and clears any credential-file override.
It requires a system-assigned App Service identity and checks the Firebase, SQL,
Cosmos, and Blob secret names from appsettings using the publisher's Azure login.
Grant the app identity secret read access separately; the publisher's successful
check does not prove runtime access. The Firebase service account also needs
Firestore read access. Unrelated Azure settings are preserved. Applying settings
can restart the app before deployment and is not rolled back if publishing fails.
Run the script without `-Apply` for local validation without Azure changes.

No new SQL migration is required for the GET endpoint. It reads the documents
written by the earlier Firestore notification implementation; deploy that writer
and its SQL changes first if they are not live. No historical SQL backfill occurs.
Best-effort write gaps and push eligibility remain as described in
[Firestore notification setup](firestore-notifications.md).

Mobile clients can use this API instead of direct Firestore reads. This GET adds a Firestore read to its
own request latency without changing booking/chat request delivery behavior.

## Persisting read status

Both API hosts accept the same Firebase ID token for these bodyless requests:

- `POST /api/v1/notifications/{notificationId}/read`: returns 204, including when
  already read. Returns 404 if the document does not exist in the caller's inbox.
- `POST /api/v1/notifications/read-all`: returns 204 after marking notifications
  created at or before the operation's start as read. Newer arrivals remain unread.

Updates are scoped to the authenticated Firebase UID and the host's audience.
They persist `isRead: true` in the same Firestore documents returned by GET, so
other devices and reinstalls observe the stored status. No notification body or
recipient ID is accepted. Single-read never creates a missing notification.
The service account now needs Firestore update permission as well as read access.

Read-all processes up to 400 documents per batch with a 30-second request budget.
It is not atomic across batches: on a storage failure or timeout, some entries
may already be read. Both operations are safe to retry; failures return 503 with
`NotificationsUnavailable`. Missing or raw Google authentication returns 401.

## Verification

`dotnet run --project tests/Pawfront.Notification.Checks` checks both real endpoint
mappings through an in-memory HTTP server with fake authentication/storage, and
captures Firestore SDK query requests without network access. Checks cover
authentication, UID/audience isolation, pagination limits/cursors, response
envelopes, storage failures, query ordering and scope, lookahead, and final pages.
Live Firebase credentials, IAM permissions, and deployed responses must be verified
in the target environment.
