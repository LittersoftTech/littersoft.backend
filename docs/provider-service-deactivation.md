# Deactivate an individual service

`POST /api/v1/providers/{providerId}/services/{serviceId}/deactivate`

Uses the existing authenticated provider API. No request body is required. Obtain
`serviceId` from `GET /api/v1/providers/{providerId}/services`.

- Returns `204 No Content` on success, including when already inactive.
- Returns `404` with error code `ProviderServiceNotFound` when the service does not
  exist or belongs to a different provider.
- Allows deactivating the last active service. Repeat for each service to deactivate all.
- Preserves `profile.isActive`, offering configuration, and existing bookings/history.
- Inactive services are excluded from the default service list; use
  `?includeInactive=true` to include them. New booking creation rejects inactive services.
- Saving the corresponding offering again uses the existing activation behavior;
  this can reactivate services present in that offering.

The business online/offline switch remains
`POST /api/v1/providers/{providerId}/active-status` with `{ "isActive": false }`
(or `true` to go online). Service deactivation never calls that API.
