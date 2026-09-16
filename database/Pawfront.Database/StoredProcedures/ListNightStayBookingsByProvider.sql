-- The provider's own boarding-stay list, optionally narrowed to one service or
-- one night.
--
-- THE CUSTOMER COLUMNS ARE NEW HERE, and unlike the single-day list there is
-- nothing to fall back to: [Booking].[NightStayBookings] has no walk-in shape at
-- all (boarding is App-only), so it carries no free-text customer or pet columns
-- and this list previously returned no customer information of any kind -- not
-- even a name. A provider's boarding list could not render a card without a
-- per-row call to the booking detail.
--
-- Appended AFTER the standard night-stay row columns (ordinal 15 onward), so the
-- shared C# ReadRow reader -- which every other night-stay sproc feeds -- stays
-- untouched at 0-14. Same convention
-- [Booking].[ListNightStayBookingsByPetParent] uses for its snapshot extras.
--
-- Joined LIVE, so a deleted account reads its anonymised placeholder
-- ("Deleted User" / "Deleted Pet") rather than leaving real personal data frozen
-- in a list. [PetId] is nullable on legacy rows, so all six can be NULL.
CREATE OR ALTER PROCEDURE [Booking].[ListNightStayBookingsByProvider]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceId UNIQUEIDENTIFIER = NULL,
    @OnDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- @OnDate narrows to stays that include that night (CheckInDate <= date <
    -- CheckOutDate) -- the provider-day view. Omit it to return full history.
    SELECT n.[NightStayBookingId],
           n.[ProviderId],
           n.[PetParentId],
           n.[ServiceId],
           n.[ServiceCategory],
           n.[SubCategory],
           n.[CheckInDate],
           n.[CheckOutDate],
           n.[DropOffTime],
           n.[PickUpTime],
           n.[Status],
           n.[CreatedAtUtc],
           n.[UpdatedAtUtc],
           n.[CancelledAtUtc],
           n.[PetId],
           -- Appended extras (ordinals 15+), see header.
           [CustomerName]     = pp.[FirstName] + N' ' + pp.[LastName],
           [CustomerPhotoUrl] = pp.[ProfilePhotoUrl],
           [PetName]          = pet.[PetName],
           [AnimalType]       = pet.[PetType],
           [Breed]            = pet.[Breed],
           [PetGender]        = pet.[Gender]
    FROM [Booking].[NightStayBookings] n
    LEFT JOIN [Parent].[PetParents] pp
        ON pp.[PetParentId] = n.[PetParentId]
    LEFT JOIN [Parent].[Pets] pet
        ON pet.[PetId] = n.[PetId]
    WHERE n.[ProviderId] = @ProviderId
      AND (@ServiceId IS NULL OR n.[ServiceId] = @ServiceId)
      AND (@OnDate IS NULL OR (@OnDate >= n.[CheckInDate] AND @OnDate < n.[CheckOutDate]))
    ORDER BY n.[CheckInDate] DESC, n.[CheckOutDate] DESC;
END;
