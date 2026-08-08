CREATE OR ALTER PROCEDURE [Booking].[GetNightStayBookingDetail]
    @NightStayBookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Enriched single-booking read backing the night-stay booking-detail endpoint.
    -- Mirrors [Booking].[GetBookingDetail] for the multi-night model: the base
    -- columns PLUS the sequential JobNumber, payout fields, and the joined
    -- pet-parent / pet / provider records. Night-stay bookings are App-only (PetParentId is
    -- always set; PetId is set for parent-app bookings), so there is no Custom
    -- shape — the customer + pet details always come from the joined records.
    SELECT b.[NightStayBookingId],
           b.[JobNumber],
           b.[ProviderId],
           b.[PetParentId],
           b.[ServiceId],
           b.[ServiceCategory],
           b.[SubCategory],
           b.[CheckInDate],
           b.[CheckOutDate],
           b.[DropOffTime],
           b.[PickUpTime],
           b.[Status],
           b.[CreatedAtUtc],
           b.[UpdatedAtUtc],
           b.[CancelledAtUtc],
           b.[PetId],
           b.[PayoutStatus],
           b.[PayoutId],
           b.[PricePerNight],
           -- Pet-parent join -------------------------------------------------
           pp.[FirstName]         AS [ParentFirstName],
           pp.[LastName]          AS [ParentLastName],
           pp.[Gender]            AS [ParentGender],
           pp.[MobileCountryCode] AS [ParentMobileCountryCode],
           pp.[MobileNumber]      AS [ParentMobileNumber],
           pp.[ProfilePhotoUrl]   AS [ParentPhotoUrl],
           -- Pet join --------------------------------------------------------
           pet.[PetName]          AS [PetProfileName],
           pet.[PetType]          AS [PetType],
           pet.[Gender]           AS [PetGender],
           pet.[ProfilePhotoUrl]  AS [PetPhotoUrl],
           -- Provider join -----------------------------------------------------
           prov.[FirstName]         AS [ProviderFirstName],
           prov.[LastName]          AS [ProviderLastName],
           prov.[Gender]            AS [ProviderGender],
           prov.[MobileCountryCode] AS [ProviderMobileCountryCode],
           prov.[MobileNumber]      AS [ProviderMobileNumber],
           -- Pet medical extras ------------------------------------------------
           pet.[Breed]              AS [PetBreed],
           pet.[VaccinationStatus]  AS [PetVaccinationStatus],
           pet.[VaccinationType]    AS [PetVaccinationType],
           pet.[VaccinationDose]    AS [PetVaccinationDose],
           pet.[Prescription]       AS [PetPrescription],
           pet.[SterilizationStatus] AS [PetSterilizationStatus],
           pet.[MedicalHistory]      AS [PetMedicalHistory],
           pet.[Temperament]         AS [PetTemperament],
           -- Stay notes + location choice + the parent's address ----------------
           b.[JobNotes],
           b.[LocationType],
           pp.[AddressLine]         AS [ParentAddressLine],
           pp.[City]                AS [ParentCity],
           pp.[ZipCode]             AS [ParentZipCode],
           pp.[Latitude]            AS [ParentLatitude],
           pp.[Longitude]           AS [ParentLongitude],
           -- Snapshots captured at booking time. The detail read PREFERS these over
           -- the live provider policy / resolved address, falling back to live only
           -- for legacy rows where they're NULL. Appended LAST for stable ordinals.
           b.[CancellationPolicyHours],
           b.[SnapshotAddressLine],
           b.[SnapshotCity],
           b.[SnapshotZipCode],
           b.[SnapshotLatitude],
           b.[SnapshotLongitude],
           -- Payment ledger join. HOW the money changed hands ('Cash'/'Digital')
           -- is recorded only on the ledger row, never on the booking. Both
           -- columns stay NULL until the provider marks the stay PAID. Appended
           -- LAST so existing reader ordinals stay stable.
           pay.[PaymentMethod] AS [PayoutMethod],
           pay.[PaidAtUtc]
    FROM [Booking].[NightStayBookings] AS b
    LEFT JOIN [Parent].[PetParents] AS pp
        ON pp.[PetParentId] = b.[PetParentId]
    LEFT JOIN [Parent].[Pets] AS pet
        ON pet.[PetId] = b.[PetId]
    LEFT JOIN [Provider].[Providers] AS prov
        ON prov.[ProviderId] = b.[ProviderId]
    -- BookingType discriminates which booking table BookingId points at — the
    -- ledger is shared by single-day and night-stay bookings and has no FK.
    LEFT JOIN [Booking].[BookingPayments] AS pay
        ON pay.[BookingId] = b.[NightStayBookingId] AND pay.[BookingType] = N'NightStay'
    WHERE b.[NightStayBookingId] = @NightStayBookingId;
END;
