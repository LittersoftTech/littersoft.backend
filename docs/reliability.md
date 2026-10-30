# Profile reliability

Profile GET responses include `data.reliability`. The API returns the numeric parent service score and its supporting totals; mobile clients decide what to display. Community flags and service flags are independent, and parent/provider accounts are evaluated separately.

| Host | Endpoint | Profile |
| --- | --- | --- |
| Parent API | `GET /api/v1/pet-parents/{petParentId}/profile` | Parent's own profile |
| Provider API | `GET /api/v1/pet-parents/{petParentId}/details` | Parent details viewed by a provider |
| Provider API | `GET /api/v1/providers/{providerId}/profile` | Provider account profile |
| Parent API | `GET /api/v1/providers/{providerId}` | Provider public profile |

These GET routes declare their response envelopes in OpenAPI. Profile write responses are unchanged and may have a null `reliability`; fetch the GET response for the current reliability result.

## Response fields

`reliability` has `services`, `community`, `hosting`, and `calculatedAtUtc`. Dates are UTC timestamps. The backend calculates the section on every read, so ageing and expiry apply even when no new booking has occurred. Numeric scores are rounded to two decimal places; flag decisions use the unrounded score.

| Section | Fields | Meaning |
| --- | --- | --- |
| Services | `isApplicable`, `score`, `meetsVolumeGate`, `countedBookings` | Whether scoring applies, score out of 100 (null below five counted bookings), and volume |
| Services | `completedAndPaidBookings`, `lateCancellations`, `noShows`, `unpaidServices` | Counts in the rolling 180-day window |
| Services | `weightedPointsInFavour`, `weightedPointsAgainst` | Totals used by the formula |
| Services | `isRedFlagged`, `lastNegativeAtUtc`, `redFlagExpiresAtUtc` | Current service flag, latest counted negative outcome, and active flag expiry |
| Services | `additionalPaidBookingsToReachThreshold` | Number of new positive outcomes needed to reach 50, reweighting the history after every simulated outcome; null when unflagged |
| Services | `windowDays`, `minimumCountedBookings`, `redFlagThreshold`, `redFlagDurationDays` | Rule parameters: 180, 5, 50, 30 |
| Community | `isRedFlagged`, `redFlagExpiresAtUtc` | Attendee flag and active expiry |
| Community | `consecutiveNegativeMarks`, `consecutivePaidEventsAttended`, `paidEventsToRestoreAccess` | Trigger/recovery progress; the existing trigger counter resets after a pair raises a flag |
| Community | `canCreateEvents`, `maximumUpcomingBookings`, `activeBookingCount`, `availableBookingSlots` | Effective event restrictions and remaining capacity; existing bookings are retained even when they exceed a new limit |
| Community | `triggerWindowDays`, `redFlagDurationDays` | Both 30 |
| Hosting | `hasPublishedEvents`, `eventsRun`, `eventsCancelled`, `windowStartsAtUtc`, `windowMonths` | Published factual history in the last 12 calendar months |

Providers have `services.isApplicable = false`, `score = null`, zero service counts, and no service flag. Their existing ratings/reviews remain separate. Both account types have community attendance state and host history.

## Parent service calculation

Each counted booking has one current mark: payment received = +1, late parent cancellation = -2, parent no-show = -4, declared payment unpaid = -6. A subsequent payment replaces an unpaid mark with a positive mark. Completed but not yet declared paid/unpaid bookings do not count.

Counted outcomes from the last 180 days are ordered by outcome timestamp, newest first. The first five have full weight, the next five half weight, and the remaining outcomes quarter weight. Ties use booking type and ID for deterministic ordering. The mark also stores the scheduled service date for audit purposes.

`score = weightedPointsInFavour / (weightedPointsInFavour + weightedPointsAgainst) * 100`

At least five counted bookings are required. Four paid bookings plus one no-show therefore count and score 50. The weighted formula takes precedence over the original document's unweighted examples.

A flag is active only when the current rolling score is below 50 and the latest counted negative outcome is less than 30 days old. It lifts at 50, after 30 days without a negative outcome, or when the rolling history falls below the five-booking gate. This implements the user's rolling-score clarification, overriding the document's instruction to preserve a flag despite ageing.

Late cancellation uses the booking's creation-time cancellation-policy snapshot and the existing service-booking UTC scheduling convention. Cancellation strictly after the notice deadline counts; cancellation at the deadline or without a notice period does not. Unaccepted requests do not count. Private/custom jobs, zero-priced introductory bookings, provider cancellation/no-show, expired/declined requests, modification requests, and OTP-attempt closures do not generate marks.

The history spans single-day sitting, grooming, training, veterinary appointments, and multi-night sitting. New terminal outcomes are captured atomically by triggers on the existing booking status-history tables. This also covers scheduled no-show settlement. There is no backfill: installing the triggers starts collection for subsequent outcomes.

## Community and hosting

The existing community rule raises a 30-day flag after two consecutive negative marks within 30 days. Paid attendance breaks the negative sequence; two consecutive paid attendances recover an active flag. A flagged account cannot create an event and can hold one event booking; other accounts can hold five. Enforcement remains in the existing event operations.

Each new negative mark while the community flag is active restarts its expiry at 30 days from that mark and resets `consecutivePaidEventsAttended` to zero. `GET /api/v1/me/reliability` reflects the new `redFlagExpiresAt` immediately after cancellation. Reprocessing the same booking outcome does not restart the timer again.

Free events generate no marks and do not reset either sequence. They close at the end of the final event day without relying on payment collection. The existing five-minute event sweep performs settlement.

Hosting counts active events whose end timestamp is in the last 12 months, including free events and events nobody booked. Cancellations count only when the event had bookings and the cancellation timestamp is in the window. Host removal does not erase cancellation history or move its date. A dedicated `Events.CancelledAtUtc` preserves that date separately from later edits. Future events and cancellations without bookings do not increment these counts.

## Deferred work and deployment

Challenge windows, notification-based eligibility, report pauses, upheld-report reversals, and one-time review requests are deferred as requested. Negative service outcomes count immediately. There is no notification-delivery dependency in this version.

Deploy `database/Pawfront.Database/Deployment/DeployAll.sql` before deploying the API projects. It includes the new mark table, triggers, cancellation timestamp, reliability reader procedure, and free-event corrections. The script was also synchronized with the existing `BlockParticipant.sql` source to remove a stale reference to an undeclared variable that prevented fresh deployments. No application environment was deployed during implementation.

Validation:

```powershell
dotnet build src/Pawfront.Api/Pawfront.Api.csproj --no-restore -m:1
dotnet build src/Pawfront.PetParentApi/Pawfront.PetParentApi.csproj --no-restore -m:1
dotnet build tests/Pawfront.Reliability.Checks/Pawfront.Reliability.Checks.csproj -m:1
dotnet tests/Pawfront.Reliability.Checks/bin/Debug/net10.0/Pawfront.Reliability.Checks.dll
```

For SQL integration checks, prepare an isolated LocalDB database named `PawfrontReliabilityChecks` with `DeployAll.sql`, then run the check executable with `--sql`. All fixtures are synthetic and rolled back.
