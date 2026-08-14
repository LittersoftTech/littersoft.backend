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
| `MessageDeleted` | `{ conversationId, messageId, sequence, deletedAtUtc }` |
| `MessageRead` | `{ conversationId, participantType, participantId, lastReadSequence }` |
| `TypingChanged` | `{ conversationId, participantType, participantId, isTyping }` |
| `ConversationUpdated` | `{ conversationId, lastSequence, lastMessageAtUtc, unreadCount }` |
| `UnreadCountChanged` | `{ conversationId, unreadCount }` |

**`MessageDeleted` arrives the same two ways `MessageReceived` does** — to the
thread group (you have the conversation open) and to your personal group (you are
elsewhere in the app with the inbox on screen, and its preview line may have just
become "This message was deleted").

**Render it in place, do not remove the row.** The message keeps its sequence and
its timestamp; only its content is gone. Re-fetching history returns it with
`isDeleted: true` and a null `text`, so removing it on the event and restoring it
on the next fetch would make the thread flicker.

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

Uniqueness is enforced by a **primary key on `(conversationId, messageId)`**, so it
holds across the hub and REST alike — send the same id over both and the second is
a replay, not a second message. Retrying is therefore always safe, which is what
makes an offline queue workable: resend the whole queue after a reconnect without
tracking which ones got through.

---

## 3. REST

```
POST   /api/v1/conversations                              { counterpartyId }
GET    /api/v1/conversations                              ?search= &skip= &take=  (max 20)
GET    /api/v1/conversations/unread-summary
GET    /api/v1/conversations/{id}
DELETE /api/v1/conversations/{id}
GET    /api/v1/conversations/{id}/bookings                ?skip= &take=  (max 20)
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

### Searching the inbox

`GET /conversations?search=anna` returns inbox cards — the same shape, sorting and
paging as the unfiltered list, so the search results screen is the inbox screen
with a filter.

It matches the **counterparty's name** and the **last message's preview**. It is
deliberately not a full message-history search: bodies are partitioned per
conversation, so matching all of them would be a cross-partition scan per
keystroke. If searching whole threads is needed, say so — it wants a search index
rather than a bigger query.

Case-insensitive, trimmed, and `%` / `_` in the term are matched literally rather
than as wildcards. A blank term returns the whole inbox.

### Deleting a whole chat

`DELETE /conversations/{id}` clears the thread **for you only**. The other side
keeps every message, their unread count and their place in the conversation —
that is the point, and it is also the only thing the storage allows: a message is
one document read by both of you.

```json
{ "conversationId": "…", "clearedUpToSequence": 42, "deletedAtUtc": "…" }
```

What actually happens:

* the thread leaves **your** inbox and your history restarts after
  `clearedUpToSequence`;
* **the counterparty notices nothing** — no event, no change to their thread;
* **it comes back** the moment they send something, carrying on from there
  without the cleared history. A delete that stopped you receiving messages would
  not be a delete, it would be a broken conversation, so plan for a thread you
  deleted to reappear;
* re-opening it yourself (`POST /conversations` with the same counterparty) also
  puts it back on your inbox — the history stays cleared, since walking back into
  the room is not a request to undo the delete.

404 `ConversationNotFound` for a thread that does not exist and for one that is
not yours — one answer, as everywhere else here.

### The jobs behind a thread ("View Jobs")

`GET /conversations/{id}/bookings` returns every booking between the two of you,
newest first by service date — past and present, cancelled and completed included.
Works from either side, and takes no provider or parent id: the conversation id
*is* the pair.

```json
{
  "conversationId": "…", "providerId": "…", "petParentId": "…",
  "totalCount": 7, "skip": 0, "take": 20, "hasMore": false,
  "jobs": [ { "bookingId": "…", "bookingType": "SingleDay", "jobId": "PF-000123", … } ]
}
```

**`jobs[]` is the same job card the two delete-refusal payloads use** (the 409
`PendingJobsExist` body on `DELETE /pet-parents/{id}` and `DELETE /pets/{id}`), so
if you already render that, you already render this. `bookingType` discriminates
`"SingleDay"` from `"NightStay"` — the two kinds share no id space, so `bookingId`
alone cannot say which detail screen to open — and the other kind's fields are
null. `price` and `providerProfilePhotoUrl` are filled in best-effort and can be
null for an old booking whose service has since been deactivated.

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

**The inbox follows it.** When the retracted message was the thread's newest, its
`lastMessagePreview` on `GET /conversations` becomes `"This message was deleted"` —
already composed server-side, so render it as-is like any other preview. Deleting
an older message changes nothing on the card, and `lastMessageAtUtc` never moves in
either case: the message is still the thread's last activity, only its content is
gone. Until 2026-08-11 the card went on showing the deleted text.

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

**Why the message id comes from the client.** It is also the Cosmos document id,
and the second half of the primary key of `Chat.ConversationMessages`. One value
therefore keys idempotency in both stores, and ids are partition-scoped so two
conversations can never collide.

**How a send is made atomic.** Three steps around one client call:

```
Chat.ReserveMessageSequence   ->  Cosmos CreateItem  ->  Chat.CommitMessageAppend
   sequence only,                   the body             preview, unread, push
   nothing observable
```

Reserve authorises the sender, takes the sequence and writes nothing anybody can
see. Every observable effect belongs to commit, which runs only once the body is
durable. So a send either fully lands or leaves the thread exactly as it was —
there is no state in which the recipient gets a message the sender was told had
failed. The reservation is released if the body cannot be written, and neither
call holds a lock across the other.

The only residue of a failed send is a spent sequence number. Gaps are harmless,
which is why there is deliberately no message count anywhere to be wrong.

**Commit is idempotent**, and that is load-bearing rather than decorative: if a
send dies after the body is written, the retry finds the body already there and
comes straight back to commit, finishing the delivery that was missed. A second
commit moves no counter and queues no second push.

**How push is instant without giving up durability.** `Chat.CommitMessageAppend`
writes the outbox row **already claimed** (status `Sending`, lease held), so the
1-minute `NotificationDispatchFunction` skips it while the chat host sends it
directly. If the chat host dies mid-send, the lease lapses and the scheduled
dispatcher takes it over. The outbox stops being the delivery path and becomes the
retry backstop — there is no case where a message is stored and its notification
is lost.

> ⚠️ **That backstop is also the failure mode to watch for.** The chat host needs
> its own working Firebase credentials — it is the one API host that sends. With
> none usable, every instant send fails, each row falls to the scheduled
> dispatcher, and chat messages arrive a minute or more late while *appearing* to
> work. Nothing surfaces as an error to the client. This was a real reported bug
> (fixed 2026-08-11): the host had only a `CredentialsSecretName`, which resolves
> through Key Vault — and with `AzureKeyVault:Enabled = false` the local provider
> looks for `LocalSecrets:<name>` in config and throws when it is absent. A
> populated-looking `Notifications` section is therefore not evidence the host can
> send. Confirm with `Firebase initialisation failed` in the host log, or
> `LastError` on `Notification.NotificationOutbox`; a healthy chat row reaches
> `Sent` with `AttemptCount = 1` within a second or two of the message.
