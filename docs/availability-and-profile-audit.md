# Availability and profile audit

## Implemented

Dated searches reject weekly days off (including absent weekday rows) and any service-specific time off. Night Stay checks every date from check-in through pickup, inclusive. Capacity still counts stayed nights only; pickup consumes no additional night.

Booking creation and modification request/acceptance enforce availability. Acceptance rereads the pending proposal and current schedule/time off. App booking creation and modifications retain the existing two-hour lead time in UTC, which also rejects past dates. Custom booking availability validation rejects past dates.

HTTP 400 codes: `ServiceClosed` for time off and closed Night Stay dates; `InvalidBookingTime` for hourly working-hours violations; `BookingLeadTimeTooShort` for past or insufficient-lead-time app bookings. Invalid Night Stay ranges retain `InvalidNightStayDates`.

The basic-info SQL completion predicate now treats the pair 0,0 as missing coordinates. Either axis may legitimately be zero on its own. Updated both the standalone procedure and deployment bundle. No live database migration or geocoding was run.

## Findings and decisions outstanding

- Images were not changed. Suggested policy: 3,145,728 bytes per image; JPEG, PNG, HEIC and WebP; verify actual format as well as declared content type; consistent `ImageTooLarge` and `UnsupportedImageFormat` errors. Settle byte units and HEIC preview handling before implementation. Existing provider and service banners have a 5 MB limit, so include those when agreeing the scope of “every image endpoint.”
- Cities currently use trimmed, case-insensitive equality. Zürich/Zurich and Genève/Geneva may appear separately; city=Zurich does not match Zürich. Accent normalization can handle the first pair, but language aliases require a shared canonical city identifier or explicit alias mapping. No city behavior was changed.
- Recommend prompting parents with missing or 0,0 coordinates to confirm their saved address. No silent address backfill was added.
- Current schema and available repository history initialize medical statuses to NULL, without defaults. The pet creation procedure omits these columns; the dedicated medical update writes supplied statuses. No automatic medical-status backfill was found. This does not establish what historical clients submitted or what scripts ran directly against production. Current nonblank statuses alone cannot prove parent confirmation.

## Verification

Both API projects build. `dotnet run --project tests/Pawfront.Availability.Checks/Pawfront.Availability.Checks.csproj` runs 20 checks for missing/closed weekdays, pickup and intermediate dates, full/partial time off, service isolation, search exclusion, and hourly/Night Stay modification rejection. SQL completion predicate was reviewed; no live database checks or deployment were performed.
