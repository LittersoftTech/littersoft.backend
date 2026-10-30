# Event organizer photos

`GET /events` and `GET /events/{eventId}` now populate `organizer.imageUrl` for provider hosts from the current service-profile image. The same enrichment applies to trending events, organizer-specific lists, and create/update responses on both API hosts.

The provider service category selects the same image used by the provider profile: the business image for a hotel/shop/school/shelter/clinic or the freelance profile image. Photos are read live on every request, so changing or removing a photo updates existing events without editing them. Repeated hosts share one lookup within each list response.

Pet-parent host photos retain their existing behavior. A missing profile/photo returns null. A failed profile lookup is logged and the event remains available with a null photo.

No request or response schema changes and no database migration are required. Deploy the updated API applications. Run focused checks with `dotnet run --project tests/Pawfront.EventOrganizer.Checks`.
