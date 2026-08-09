# Chat — provider ↔ pet-parent messaging

Mobile-facing contract for `Pawfront.ChatApi`. For why it is built this way, see
the architecture notes at the end.

> **Not deployed yet.** The `[Chat]` schema ships in `DeployAll.sql` but has never
> been run, and the two Firebase service-account credentials are still
> outstanding. Build against this; do not expect a live endpoint.

---

## 1. Connecting

A third host, alongside the provider and pet-parent APIs. **Both apps use the same
one** — it validates tokens from both Firebase projects side by side, so each app
keeps sending the token it already has.

| | |
|---|---|
| REST | `{chatHost}/api/v1/…` — same `{ success, data, error }` envelope as the other hosts |
| Hub | `{chatHost}/hubs/chat` |
| Flutter client | [`signalr_netcore`](https://pub.dev/packages/signalr_netcore) |

### The token goes in `accessTokenFactory`, not a header

A WebSocket handshake cannot carry an `Authorization` header, so SignalR clients
put the token on the query string. The server reads `?access_token=` **only** on
`/hubs/*`; on REST paths it is ignored and the header is required. That scoping is
deliberate — a token in a URL ends up in every access log, and the REST surface
has no reason to accept one.

```dart
final connection = HubConnectionBuilder()
    .withUrl('$chatHost/hubs/chat', options: HttpConnectionOptions(
      accessTokenFactory: () async => await firebaseUser.getIdToken(),
    ))
    .withAutomaticReconnect()
    .build();
```

### Check who the server thinks you are

`GET /api/v1/me` → `{ participantType, participantId, firebaseUserId, canChat, userId }`

Worth calling once at startup while integrating. `canChat: false` means the
Firebase account is valid but onboarding never produced a ProviderId /
PetParentId — every chat route will answer **403 `ChatProfileNotCompleted`** and
the hub will drop the connection.

---

## 2. Hub

### Client → server

| Method | Notes |
|---|---|
| `JoinConversation(conversationId)` | Returns the conversation. Authorises first — a thread you are not part of throws. |
| `LeaveConversation(conversationId)` | **Call this on navigating away.** See below. |
| `SendMessage(conversationId, request)` | Returns the stored message. |
| `MarkRead(conversationId, upToSequence)` | Returns your new read state. |
| `SetTyping(conversationId, isTyping)` | Never stored. |
| `Heartbeat()` | Every ~60 s. See below. |

### Server → client

| Event | Payload |
|---|---|
| `MessageReceived` | the full message |
| `MessageRead` | `{ conversationId, participantType, participantId, lastReadSequence }` |
| `TypingChanged` | `{ conversationId, participantType, participantId, isTyping }` |
| `ConversationUpdated` | `{ conversationId, lastSequence, lastMessageAtUtc, unreadCount }` |
| `UnreadCountChanged` | `{ conversationId, unreadCount }` |

### Three things that matter more than they look

**1. Join/Leave drives push suppression.** A push is sent only when the recipient
has *no* connection with that thread open. Skip `LeaveConversation` and the user
stops being notified for that chat until they reconnect.

**2. `Heartbeat()` on a ~60 s timer.** A connection whose heartbeat stops for 3
minutes is presumed dead and purged. It re-registers on the next heartbeat, but in
between the user gets pushed for a thread they are reading.

**3. Always send `clientMessageId`.** It becomes the message id, so a retry after
a dropped response returns the original instead of posting twice. Omit it and the
server mints one — and every retry then duplicates.

---

## 3. REST

```
POST   /api/v1/conversations                              { counterpartyId }
GET    /api/v1/conversations                              ?skip= &take=  (max 20)
GET    /api/v1/conversations/unread-summary
GET    /api/v1/conversations/{id}
GET    /api/v1/conversations/{id}/messages                ?beforeSequence= &take=  (max 50)
POST   /api/v1/conversations/{id}/messages
POST   /api/v1/conversations/{id}/read                    { upToSequence }
DELETE /api/v1/conversations/{id}/messages/{messageId}
POST   /api/v1/conversations/{id}/attachments             multipart { file }
POST   /api/v1/blocks                                     { counterpartyId, reason? }
GET    /api/v1/blocks
DELETE /api/v1/blocks/{chatBlockId}
POST   /api/v1/blob-images                                { blobUrl }
```

`POST /conversations` takes only the counterparty's id — the server derives
whether that is a provider or a parent from your token, so there is nothing to get
wrong or contradict.

Sending exists as REST as well as a hub method so a client whose socket has
dropped can still send. Both go through identical server logic.

### Sending an image

Two calls, in this order:

1. `POST /conversations/{id}/attachments` (multipart, ≤ 5 MB, JPEG/PNG/WebP) →
   returns `{ blobUrl, contentType, sizeBytes, width, height }`
2. `POST /conversations/{id}/messages` with `kind: "Image"` and that object as
   `attachment`

The container is private, so render the image by POSTing its `blobUrl` to
`/blob-images` — same as every other image in the product.

---

## 4. Ordering, paging, reconnect

**Order by `sequence`, never by timestamp.** Two messages can share a millisecond;
they can never share a sequence.

History is newest-first. Page backwards by passing the response's
`nextBeforeSequence` as `?beforeSequence=`; `null` means you have reached the
start of the thread.

**SignalR is fire-and-forget.** On every reconnect, re-read
`GET /conversations/{id}/messages` from your last known sequence. That reconciliation
is the only delivery guarantee — the socket does not replay what you missed.

A retracted message keeps its place with `isDeleted: true` and no content. Render
"This message was deleted"; do not remove the row, or your sequence numbering will
disagree with the server's.

---

## 5. Errors

| Code | HTTP | Meaning |
|---|---|---|
| `ChatProfileNotCompleted` | 403 | Valid token, no provider/parent profile yet |
| `ConversationNotFound` | 404 | Unknown thread **or** not yours — deliberately the same answer |
| `Forbidden` | 403 | Not a party to this conversation |
| `ConversationBlocked` | 403 | One party blocked the other. Direction is never disclosed |
| `ProviderNotFound` / `PetParentNotFound` | 404 | Counterparty does not exist |
| `ProviderAccountDeleted` | 409 | That provider deleted their account |
| `UnsupportedMessageKind` | 400 | `kind` must be `Text` or `Image` |
| `ImageTooLarge` / `UnsupportedImageFormat` | 400 | Attachment rejected |
| `TooManyRequests` | 429 | Rate limited; honour `Retry-After` |

Rate limits: **30 messages/minute** and **10 new conversations/hour**, per account.

---

## 6. Push

`MESSAGE_RECEIVED`, category `MESSAGING`, route `/messages/thread`, with
`conversationId` and `senderName` in `data`.

Sent only when the recipient has no connection viewing that thread, so an open
chat never buzzes.

**Two open questions for the mobile team:**
- The body is `"Sent you a message."` with **no preview**. Say if you want one —
  it is a copy change plus one data key.
- `NotificationRoutes` are still placeholders pending your real route table.

---

## Architecture notes

**Why a third host.** Azure SignalR cannot route a message from one host's hub to
a client connected to another host's hub. Two hubs would mean two group namespaces
and a cross-host relay. One hub, one host — which then has to accept both Firebase
projects anyway.

**Why SQL *and* Cosmos.** SQL owns the thread index, per-side read state, blocks
and presence — everything that must be consistent and countable. Cosmos owns
message bodies, partitioned by `/conversationId`, where the volume is. The same
split as `Event.Events` + the Events document.

**Why names are never denormalised.** Counterparty names are joined live on every
read, so a deleted account reads "Deleted Provider" / "Deleted User" instead of
keeping its real name frozen in every thread. Same invariant the booking and
review reads hold.

**Why the message id comes from the client.** It is also the Cosmos document id.
Cosmos enforces uniqueness on nothing but `id`, so this is the only way to get
idempotency without a second index — and ids are partition-scoped, so two
conversations can never collide.

**How push is instant without giving up durability.** `Chat.AppendMessage` writes
the outbox row **already claimed** (status `Sending`, lease held), so the 1-minute
`NotificationDispatchFunction` skips it while the chat host sends it directly. If
the chat host dies mid-send, the lease lapses and the scheduled dispatcher takes
it over. The outbox stops being the delivery path and becomes the retry backstop —
there is no case where a message is stored and its notification is lost.

**One accepted trade-off.** SQL is written before Cosmos, so a crash between them
burns a sequence number and leaves a stale preview. The client's retry (same
`clientMessageId`) heals it. The alternative costs an extra round trip on every
message; sequence gaps are harmless, which is why there is deliberately no message
count anywhere to be wrong.
