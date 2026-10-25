# Host event overlap rule 45

A parent or provider host can run only one event at a time. The server validates every event creation and every edit that changes `startDate`, `endDate`, `startTime`, or `endTime` against the same host account's other non-cancelled events. An edit excludes its own event ID. Events belonging to another host account do not conflict.

The check uses the complete start/end timestamps, including dates and the event timezone, and the strict half-open interval rule:

```text
newStart < otherEnd AND otherStart < newEnd
```

An event ending at 12:00 and another starting at 12:00 are allowed. Multi-day events reserve their entire start-to-end interval. An event's end must be after its start.

## API routes

| API host | Create | Update |
| --- | --- | --- |
| Provider | `POST /api/v1/providers/{providerId}/events` | `PUT` or `PATCH /api/v1/providers/{providerId}/events/{eventId}` |
| Parent | `POST /api/v1/pet-parents/{petParentId}/events` | `PUT` or `PATCH /api/v1/pet-parents/{petParentId}/events/{eventId}` |

Schedule edits are supported for unbooked parent events. The existing lock after the first booking and the prohibition on editing cancelled events still apply. Edits that leave the schedule unchanged do not perform the overlap check.

An overlap returns HTTP **409** with this direct JSON body:

```json
{
  "code": "HostEventOverlap",
  "conflictingEventId": "a24a1d26-cd52-4d16-81fd-a6e1ee1486b5",
  "conflictingEventTitle": "Morning walk"
}
```

This overlap response has no `success`, `data`, or `error` envelope. Both API hosts preserve titles containing quotes and Unicode. If several events conflict, the server returns the first by start date, start time, and event ID. Existing validation/ownership errors retain their previous responses.

## Atomic enforcement

The four SQL write procedures call `Event.AssertHostEventNoOverlap` inside their existing transaction, after acquiring the shared `EventLifecycle` exclusive application lock. The lock remains held through the check and insert/update, so concurrent requests cannot both pass against stale schedules. A rejected write rolls back before changing the event or its amenities. SQL rejects the overlap before the application writes the Cosmos physical extension.

The in-memory development store also performs the check and write under one lock. The app may give immediate feedback using loaded events, but the server independently checks its current records.

The overlap procedure and all four updated write procedures are included in `database/Pawfront.Database/Deployment/DeployAll.sql`. Apply that script before deploying the API projects. No application database or API deployment was performed for this change.

## Validation

```powershell
dotnet build tests/Pawfront.EventOverlap.Checks/Pawfront.EventOverlap.Checks.csproj -m:1
dotnet tests/Pawfront.EventOverlap.Checks/bin/Debug/net10.0/Pawfront.EventOverlap.Checks.dll
```

SQL integration checks use an isolated LocalDB database named `PawfrontHostEventOverlapChecks`, prepared with `DeployAll.sql`. Run the check executable with `--sql`. Test accounts and events are generated for that run and removed afterward. Coverage includes both account roles, the specified examples, containment, exact boundaries, multi-day intervals, daylight-saving transitions, self-exclusion, unchanged state after refusal, cancellation, conflict titles, the direct HTTP response, and simultaneous creates and edits.
