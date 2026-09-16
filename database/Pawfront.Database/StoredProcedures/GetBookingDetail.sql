CREATE OR ALTER PROCEDURE [Booking].[GetBookingDetail]
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Enriched single-booking read backing the booking-detail endpoints. Returns
    -- the base booking columns PLUS the sequential JobNumber, payout fields, and
    -- the joined pet-parent / pet / provider records so App bookings (which store the
    -- customer/pet fields as NULL — those columns are Custom-walk-in only) can
    -- still surface customer + pet details. The flat [Booking].[GetBooking] proc
    -- is intentionally left untouched; it backs the many internal callers that
    -- only need the raw row.
    SELECT b.[BookingId],
           b.[JobNumber],
           b.[ProviderId],
           b.[PetParentId],
           b.[ServiceId],
           b.[ServiceCategory],
           b.[SubCategory],
           b.[BookingDate],
           b.[StartTime],
           b.[EndTime],
           b.[Status],
           b.[CreatedAtUtc],
           b.[UpdatedAtUtc],
           b.[CancelledAtUtc],
           b.[ServiceItemCode],
           b.[Source],
           b.[CustomerName],
           b.[CustomerMobileCountryCode],
           b.[CustomerMobile],
           b.[AnimalType],
           b.[PetName],
           b.[ServiceLocation],
           b.[CustomerLocation],
           b.[PricePerHour],
           b.[JobNotes],
           b.[PetId],
           b.[PayoutStatus],
           b.[PayoutId],
           -- Pet-parent join (App bookings) ----------------------------------
           pp.[FirstName]         AS [ParentFirstName],
           pp.[LastName]          AS [ParentLastName],
           pp.[Gender]            AS [ParentGender],
           pp.[MobileCountryCode] AS [ParentMobileCountryCode],
           pp.[MobileNumber]      AS [ParentMobileNumber],
           pp.[ProfilePhotoUrl]   AS [ParentPhotoUrl],
           -- Pet join (App bookings) -----------------------------------------
           pet.[PetName]          AS [PetProfileName],
           pet.[PetType]          AS [PetType],
           pet.[Gender]           AS [PetGender],
           pet.[ProfilePhotoUrl]  AS [PetPhotoUrl],
           -- Provider join (both booking sources) ------------------------------
           prov.[FirstName]         AS [ProviderFirstName],
           prov.[LastName]          AS [ProviderLastName],
           prov.[Gender]            AS [ProviderGender],
           prov.[MobileCountryCode] AS [ProviderMobileCountryCode],
           prov.[MobileNumber]      AS [ProviderMobileNumber],
           -- Pet medical extras (App bookings) ---------------------------------
           pet.[Breed]              AS [PetBreed],
           pet.[VaccinationStatus]  AS [PetVaccinationStatus],
           pet.[VaccinationType]    AS [PetVaccinationType],
           pet.[VaccinationDose]    AS [PetVaccinationDose],
           pet.[Prescription]       AS [PetPrescription],
           pet.[SterilizationStatus] AS [PetSterilizationStatus],
           pet.[MedicalHistory]      AS [PetMedicalHistory],
           pet.[Temperament]         AS [PetTemperament],
           -- Location choice + the parent's address (App bookings) -------------
           b.[LocationType],
           pp.[AddressLine]         AS [ParentAddressLine],
           pp.[City]                AS [ParentCity],
           pp.[ZipCode]             AS [ParentZipCode],
           pp.[Latitude]            AS [ParentLatitude],
           pp.[Longitude]           AS [ParentLongitude],
           -- Vet prescription (per-visit snapshot) — present only once a vet has
           -- recorded one for this booking; NULLs otherwise. NextConsultationDate
           -- is the pet's rolling Vet follow-up (Parent.PetNextConsultations), not
           -- stored on the prescription row. Appended LAST so existing column
           -- ordinals in the reader stay stable.
           CASE WHEN rx.[BookingId] IS NULL THEN 0 ELSE 1 END AS [HasPrescription],
           rx.[PrescriptionText],
           rx.[IsPetVaccinated],
           rx.[Vaccinations]        AS [PrescriptionVaccinations],
           nc.[NextConsultationDate] AS [NextConsultationDate],
           -- Snapshots captured at booking time (price-lock siblings). The detail
           -- read PREFERS these over the live provider policy / resolved address,
           -- falling back to live only for legacy rows where they're NULL. Appended
           -- LAST so existing reader ordinals stay stable.
           b.[CancellationPolicyHours],
           b.[SnapshotAddressLine],
           b.[SnapshotCity],
           b.[SnapshotZipCode],
           b.[SnapshotLatitude],
           b.[SnapshotLongitude],
           -- Payment ledger join. HOW the money changed hands ('Cash'/'Digital')
           -- is recorded only on the ledger row, never on the booking, so the
           -- payment block could not report it without this. Both columns stay
           -- NULL until the provider marks the booking PAID. Appended LAST so
           -- existing reader ordinals stay stable.
           pay.[PaymentMethod] AS [PayoutMethod],
           pay.[PaidAtUtc],
           -- Has this booking ever actually BEEN modified? True once either party
           -- accepted a schedule change, which is the only thing that rewrites the
           -- booking's own date/time. Read from the audit trail rather than from
           -- [Booking].[BookingModifications], because that table is a STAGING area
           -- holding only the open proposal -- the row is deleted on accept AND on
           -- decline, so after the fact it can say nothing about whether a
           -- modification happened.
           --
           -- A REQUESTED-then-declined modification is deliberately NOT counted:
           -- the booking's terms are exactly what they were, so a screen labelling
           -- it "modified" would be wrong. Use the status-history endpoint for the
           -- full trail, including proposals that went nowhere.
           --
           -- Appended LAST so existing reader ordinals stay stable.
           CAST(CASE WHEN EXISTS (
                         SELECT 1
                         FROM [Booking].[BookingStatusHistory] AS mh
                         WHERE mh.[BookingId] = b.[BookingId]
                           AND mh.[ToStatus] IN (N'PROVIDER_ACCEPTED_MODIFICATION', N'PARENT_ACCEPTED_MODIFICATION'))
                     THEN 1 ELSE 0 END AS BIT) AS [IsModificationDone]
    FROM [Booking].[Bookings] AS b
    LEFT JOIN [Parent].[PetParents] AS pp
        ON pp.[PetParentId] = b.[PetParentId]
    LEFT JOIN [Parent].[Pets] AS pet
        ON pet.[PetId] = b.[PetId]
    LEFT JOIN [Provider].[Providers] AS prov
        ON prov.[ProviderId] = b.[ProviderId]
    LEFT JOIN [Booking].[BookingPrescriptions] AS rx
        ON rx.[BookingId] = b.[BookingId]
    LEFT JOIN [Parent].[PetNextConsultations] AS nc
        ON nc.[PetId] = b.[PetId] AND nc.[ConsultationType] = N'Vet'
    -- BookingType discriminates which booking table BookingId points at — the
    -- ledger is shared by single-day and night-stay bookings and has no FK.
    LEFT JOIN [Booking].[BookingPayments] AS pay
        ON pay.[BookingId] = b.[BookingId] AND pay.[BookingType] = N'SingleDay'
    WHERE b.[BookingId] = @BookingId;
END;
