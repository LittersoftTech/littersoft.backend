CREATE OR ALTER PROCEDURE [Booking].[ListNightStayBookingsByPetParent]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [NightStayBookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [CheckInDate],
           [CheckOutDate],
           [DropOffTime],
           [PickUpTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [PetId],
           -- Frozen-at-creation extras for the parent "my bookings" cards (price-lock
           -- rate + cancellation-policy + selected-location snapshots). Appended LAST
           -- so the shared night-stay row reader's ordinals stay stable.
           [PricePerNight],
           [LocationType],
           [CancellationPolicyHours],
           [SnapshotAddressLine],
           [SnapshotCity],
           [SnapshotZipCode],
           [SnapshotLatitude],
           [SnapshotLongitude]
    FROM [Booking].[NightStayBookings]
    WHERE [PetParentId] = @PetParentId
    ORDER BY [CheckInDate] DESC, [CheckOutDate] DESC;
END;
