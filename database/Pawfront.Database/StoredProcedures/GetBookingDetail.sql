CREATE OR ALTER PROCEDURE [Booking].[GetBookingDetail]
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Enriched single-booking read backing the booking-detail endpoints. Returns
    -- the base booking columns PLUS the sequential JobNumber, payout fields, and
    -- the joined pet-parent / pet records so App bookings (which store the
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
           pet.[ProfilePhotoUrl]  AS [PetPhotoUrl]
    FROM [Booking].[Bookings] AS b
    LEFT JOIN [Parent].[PetParents] AS pp
        ON pp.[PetParentId] = b.[PetParentId]
    LEFT JOIN [Parent].[Pets] AS pet
        ON pet.[PetId] = b.[PetId]
    WHERE b.[BookingId] = @BookingId;
END;
