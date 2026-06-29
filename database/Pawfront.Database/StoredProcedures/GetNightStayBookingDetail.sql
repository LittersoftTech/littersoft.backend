CREATE OR ALTER PROCEDURE [Booking].[GetNightStayBookingDetail]
    @NightStayBookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Enriched single-booking read backing the night-stay booking-detail endpoint.
    -- Mirrors [Booking].[GetBookingDetail] for the multi-night model: the base
    -- columns PLUS the sequential JobNumber, payout fields, and the joined
    -- pet-parent / pet records. Night-stay bookings are App-only (PetParentId is
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
           pet.[ProfilePhotoUrl]  AS [PetPhotoUrl]
    FROM [Booking].[NightStayBookings] AS b
    LEFT JOIN [Parent].[PetParents] AS pp
        ON pp.[PetParentId] = b.[PetParentId]
    LEFT JOIN [Parent].[Pets] AS pet
        ON pet.[PetId] = b.[PetId]
    WHERE b.[NightStayBookingId] = @NightStayBookingId;
END;
