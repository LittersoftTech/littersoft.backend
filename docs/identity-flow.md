# Identity badges and review

## Parent ID

The existing owner-only `POST /api/v1/pet-parents/{petParentId}/identity` multipart upload accepts these exact values:

| Label | Value |
| --- | --- |
| Passport | `Passport` |
| National Identity Card (CH/EU/EFTA) | `NationalId` |
| Swiss Residence Permit (B/C/L) | `ResidencePermit` |
| Driving Licence (CH/EU/EFTA) | `DriverLicense` |
| Other | `Other` |

There is one identity per parent. Uploading again replaces it. Upload and owner GET responses now include `status` (`InReview`, `Added`, `NotApproved`) and nullable `rejectionReason`. Every upload resets status to `InReview` and clears the reason, including replacement of an accepted ID and re-upload after rejection. Only `Added` grants `hasIdBadge`.

DELETE works in all three states. Removal immediately removes the badge. Identity is optional and no longer gates `isFullyOnboarded`; the other onboarding requirements are unchanged.

The team checks document kind and account name manually. There is no authority verification. Display the label **ID Added**, never a claim that an authority verified the identity.

## Team review API

These endpoints are on the provider API host. They accept Firebase ID tokens for that host with the server-issued custom claim `admin: true`. Regular parent/provider tokens and raw Google tokens cannot access documents or review actions.

- `GET /api/v1/admin/parent-identities?status=InReview` returns the oldest 100 matching records, each with `identity`, `firstName`, and `lastName`. Status defaults to `InReview`; `Added` and `NotApproved` are also supported. Review and refresh the queue to advance it.
- `POST /api/v1/admin/parent-identities/{parentIdentityId}/review` accepts:

```json
{
  "decision": "Reject",
  "expectedUpdatedAtUtc": "2026-10-23T12:00:00.1234567+00:00",
  "rejectionReason": "The photo is blurry and the bottom of the ID is cut off."
}
```

Use `decision: "Accept"` to accept, with no reason. Rejection requires a nonblank reason, at most 2000 characters. Pass the exact `identity.updatedAtUtc` from the queue without rounding it. Success is 204. Invalid input is 400 `InvalidRequest`. Missing, already reviewed, deleted, or replaced identities return 409 `IdentityReviewConflict`; refresh before reviewing again. A replacement cannot be accepted using an earlier review snapshot.

Each successful review enqueues exactly one `IDENTITY_ADDED` or `IDENTITY_NOT_APPROVED` notification in the review transaction. The existing dispatcher writes Firestore and sends push; `entityType` is `Identity`, `entityId` is the identity id, and route is `/profile/identity`. No document type, document URL, chip number, or rejection text is placed in notification payloads. The owner sees the reason by fetching their identity.

## Display flags and privacy

Badge values are resolved live in one batched database read per HTTP response. The same resolver enriches the SignalR `JoinConversation` header. No badge is cached against a booking, review, or conversation.

`hasIdBadge` is included on parent profile/details, booking and night-stay list cards and parent detail sections, job customer cards, review authors, the chat counterparty, PawPrints viewers, parent event organizers, event bookings and attendee cards. Event attendee cards reflect the authenticated booking parent's badge, not a guest attendee's identity. Event bookings resolve the stored account id and type; free-text booker email cannot grant a badge. Custom bookings without a parent have `false`.

`hasMicrochipBadge` is included on pet cards, provider pet profiles, booking and night-stay cards and pet detail sections, job pet cards, PawPrints pet viewers, and the parent's own pet responses. It is true only for a currently valid saved number. The provider-facing `GET /pets/{petId}` no longer exposes `microchipId`. The parent's own pet management responses retain their number. Document type and URL remain restricted to the parent owner and team review responses.

Customer lists composed from booking/job cards can use the flags on those cards. Refresh relevant profiles/cards after uploading, replacing, or deleting an ID; those actions do not emit an acceptance notification.

## Microchip validation

Pet create/update permits an absent chip number. A supplied number must contain exactly 15 ASCII digits. Its first three digits must be one of the 249 ISO 3166-1 numeric country/territory codes or a manufacturer code from 900 through 998. Leading zeroes are retained. Invalid values return 400 `MicrochipIdInvalid`. A number on another pet still returns 409 `MicrochipIdAlreadyExists`; saving the same number on the same pet succeeds. Removing the number removes the badge. There is no chip review step.

## Deployment and checks

Deploy `database/Pawfront.Database/Deployment/DeployAll.sql` before these API builds. Its idempotent upgrade adds the review columns and constraints, accepts `Other`, and installs the review procedure. Existing uploads start in `InReview` and need explicit team acceptance. Deploy the Functions build too, so its notification catalog knows the two new types. The admin console must use a token refreshed after its server-issued admin claim was provisioned.

Run `dotnet run --project tests/Pawfront.Identity.Checks` for contract, validation, notification-payload and admin authorization checks. For SQL integration, deploy to the dedicated `PawfrontIdentityChecks` LocalDB database using the deployment script's required `sqlcmd -I -b` options, run `tests/identity-flow.sql`, and run the checks with `-- --sql`. Run `tests/Pawfront.ParentCompletion.Checks` and `tests/Pawfront.Notification.Checks` for onboarding and notification regressions.

## Provider badges

The separate provider trust badge story is implemented and documented in [provider-trust-badge.md](provider-trust-badge.md).
