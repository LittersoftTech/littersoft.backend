# Distance in search responses

Supported GET routes:
- `/api/v1/providers/search/day-care`
- `/api/v1/providers/search/night-stay`
- `/api/v1/providers/search/vets`
- `/api/v1/providers/search/groomers`
- `/api/v1/providers/search/trainers`
- `/api/v1/events`

Add optional query parameters `latitude` and `longitude` for the parent's chosen
search location. Both must be supplied together, in decimal degrees (WGS84):
latitude from -90 to 90; longitude from -180 to 180. Non-finite numbers are invalid.
`distanceUnit` accepts `km` or `mi` (case insensitive), defaulting to `km` when omitted.
Invalid units, invalid coordinates, or a partial coordinate pair return HTTP 400.

Example: `/api/v1/providers/search/day-care?latitude=47.3769&longitude=8.5417&distanceUnit=mi`

Each result includes `"distance": 2.6, "distanceUnit": "mi"` (example values).
Distance is straight-line great-circle distance, rounded to one decimal place,
not driving distance. Miles are international miles. Providers use their stored
service-registration coordinates, shared by their offerings, even when the service
can be delivered at the parent's home. Events use physical venue coordinates.

If the origin is omitted, or destination coordinates are unavailable or invalid,
`distance` is null. Online events have null distance. `distanceUnit` still reports
the requested/default unit. Zero coordinates are valid and are not treated as missing.
City remains an independent filter; neither city nor ZIP is geocoded to calculate
distance. Clients selecting an address, city, or ZIP must supply its coordinates.

No distance filtering or sorting is added. Existing ordering, pagination, and
availability checks remain unchanged. Event detail and trending routes do not accept
these new parameters; their shared response fields default to null distance and km.
No database migration or external maps API is required. Provider location reads are
limited to distinct providers on the returned page and skipped without an origin.
