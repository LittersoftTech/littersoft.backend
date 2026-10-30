# Parent search and profile completion

`GET /api/v1/providers/search/cities` returns the usual success envelope with
`data: ["Basel", "Zurich"]`. Populate the booking city dropdown from this list.
Cities come from registered provider address fields across all categories,
with the same active-account and blocking filters as provider discovery.
Whitespace is trimmed, empty cities excluded, duplicates removed ignoring case,
and cities sorted alphabetically. No providers means `data: []`. All results are
considered, not just the first search page. Existing searches accept the selected
value through `city`.

Every pet in list/detail GET responses and POST/PATCH pet responses now includes
the read-only boolean `isPetProfileComplete`. It is computed from saved fields:

- Details: nonblank `petName`, `petType` (species), `breed`, `gender`, and a set `dateOfBirth`.
- Medical: nonblank `vaccinationStatus` and `sterilizationStatus`. These are the
  two required fields saved together by the medical submission endpoint; their
  presence is the existing-data-compatible evidence of submission. Negative
  statuses count as answered. `temperament`, `medicalHistory`, `vaccinationType`,
  `vaccinationDose`, and `prescription` are optional.
- Photo: nonblank `profilePhotoUrl`. Gallery images do not count as a primary photo.

`POST /api/v1/pets/{petId}/profile-image` also returns `isPetProfileComplete`.
New `DELETE /api/v1/pets/{petId}/profile-image` clears the primary-photo reference
and returns `{ petId, profilePhotoUrl: null, updatedAtUtc, isPetProfileComplete: false }`
inside the standard envelope. It uses the existing pet ownership filter and is
idempotent. Stored blobs are retained, as on profile-image replacement; this
endpoint removes the reference. Subsequent reads recalculate completion without
any stored completion flag or client-supplied completion value.

Onboarding status is read afresh from saved parent and pet data on each request,
regardless of which screen saved it:

- `basicInfo`: required saved profile fields (name, gender, mobile, DOB, address,
  city, ZIP and coordinates); description is optional.
- `profilePhoto`: the parent's saved primary photo URL.
- `pets`: at least one non-deleted pet.
- `petMedicalInfo`: at least one non-deleted pet, with both required medical
  statuses set on every pet.
- Per-pet `missingSections`: `Details`, `MedicalInfo`, and/or `ProfilePhoto`, as
  applicable. Uploading a primary photo clears `ProfilePhoto`; deleting it restores it.

The existing parent `isFullyOnboarded` and `pets.incompletePetId` medical gate
remain separate from the stricter new pet-profile completion flag. A pet can
finish medical onboarding while still needing a profile photo.

The previous photo checks already read the saved primary URLs; no registration-only
photo flag was found. If deployed behavior differs, verify that the API and SQL
procedures are deployed together and that the app reloads status after saving.

Deploy the updated `Parent.GetPetParentOnboardingStatus` and
`Parent.UpdatePetProfilePhoto` procedures with the API. Both standalone procedure
files and `Deployment/DeployAll.sql` are updated. No new table column is needed.

Run regression checks with:
`dotnet run --project tests/Pawfront.ParentCompletion.Checks/Pawfront.ParentCompletion.Checks.csproj`.
These exercise application completion and city logic; live SQL/upload integration
requires a configured database and blob storage.
