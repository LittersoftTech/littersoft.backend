-- The provider's own bookings list (their job list), optionally narrowed to one
-- service or one calendar day.
--
-- THE CUSTOMER COLUMNS ARE LIVE-JOINED, and that is a fix rather than a feature.
-- [CustomerName] / [PetName] / [AnimalType] are columns on [Booking].[Bookings]
-- that ONLY a Custom walk-in ever populates -- [Booking].[CreateBooking] does not
-- write them at all, because an App booking carries [PetParentId] + [PetId] and
-- the identity is supposed to be read through those. Nothing here read through
-- them, so every App booking in this list came back with no customer and no pet
-- name whatsoever, and the provider app had to call the booking detail once per
-- row to render a card. Now the joined value wins and the booking's own free text
-- is the fallback, exactly as [Booking].[ListProviderEarningsBookings] has always
-- done it.
--
-- Strictly widening: these ordinals previously returned NULL for every App
-- booking, so nothing that reads them can break -- a client sees a name where it
-- used to see nothing.
--
-- [Breed] / [PetGender] / [CustomerPhotoUrl] are appended AFTER the standard
-- booking-row columns (ordinal 25 onward), so the shared C# ReadBookingRow reader
-- -- which every other booking sproc feeds -- stays untouched at 0-24. Same
-- convention [Booking].[ListBookingsByPetParent] uses for its snapshot extras.
--
-- All of the joined columns read from the LIVE parent and pet rows on purpose, so
-- a deleted account shows its anonymised placeholder ("Deleted User" /
-- "Deleted Pet") rather than leaving real personal data frozen in a list. They
-- are NULL on a Custom walk-in, which has no parent or pet record: its customer
-- is the free text above, and it has no photo or breed to show.
CREATE OR ALTER PROCEDURE [Booking].[ListBookingsByProvider]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceId UNIQUEIDENTIFIER = NULL,
    @BookingDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- @BookingDate narrows to a single calendar day when provided (provider-day
    -- view in the mobile UI). Omit it to return the full history.
    SELECT b.[BookingId],
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
           [CustomerName]     = COALESCE(pp.[FirstName] + N' ' + pp.[LastName], b.[CustomerName]),
           b.[CustomerMobileCountryCode],
           b.[CustomerMobile],
           [AnimalType]       = COALESCE(pet.[PetType], b.[AnimalType]),
           [PetName]          = COALESCE(pet.[PetName], b.[PetName]),
           b.[ServiceLocation],
           b.[CustomerLocation],
           b.[PricePerHour],
           b.[JobNotes],
           b.[PetId],
           -- Appended extras (ordinals 25+), see header.
           [Breed]            = pet.[Breed],
           [PetGender]        = pet.[Gender],
           [CustomerPhotoUrl] = pp.[ProfilePhotoUrl]
    FROM [Booking].[Bookings] b
    LEFT JOIN [Parent].[PetParents] pp
        ON pp.[PetParentId] = b.[PetParentId]
    LEFT JOIN [Parent].[Pets] pet
        ON pet.[PetId] = b.[PetId]
    WHERE b.[ProviderId] = @ProviderId
      AND (@ServiceId IS NULL OR b.[ServiceId] = @ServiceId)
      AND (@BookingDate IS NULL OR b.[BookingDate] = @BookingDate)
    ORDER BY b.[BookingDate] DESC, b.[StartTime] DESC;
END;
