# Service metadata

Both provider and pet-parent hosts expose `GET /api/v1/metadata/services` using their existing Firebase authentication. Send `Authorization: Bearer <token>` for the relevant host.

The response contains the 22 service names, owners, and descriptions in catalogue order. Reference data is shared between both hosts and requires no database migration.

| Query parameter | Meaning |
| --- | --- |
| `SR` | Optional full service name, such as `Day Care` or `Bath & Blow Dry`. Matches exactly, ignoring case and surrounding whitespace. This is the service name, not an internal service ID or offering code. |

Omitting `SR`, or sending an empty/whitespace value, returns all services. An unknown name returns HTTP 200 with an empty `data` array. Filtered responses also use an array.

Example: `GET /api/v1/metadata/services?SR=Day%20Care`

```json
{
  "success": true,
  "data": [
    {
      "serviceName": "Day Care",
      "serviceOwner": "Pet Sitter",
      "serviceDescription": "Pet day care facility for Short stays up to 12 hrs."
    }
  ],
  "error": null
}
```

URL-encode service names, especially the `&` character: `GET /api/v1/metadata/services?SR=Bath%20%26%20Blow%20Dry`.

The existing `GET /api/v1/metadata` response is unchanged.

Validation: `dotnet run --project tests/Pawfront.Metadata.Checks` exercises both hosts' metadata routes in an isolated HTTP server without external services.
