CREATE OR ALTER PROCEDURE [Booking].[ListBookingsByPetParent]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc],
           [ServiceItemCode],
           [Source],
           [CustomerName],
           [CustomerMobileCountryCode],
           [CustomerMobile],
           [AnimalType],
           [PetName],
           [ServiceLocation],
           [CustomerLocation],
           [PricePerHour],
           [JobNotes],
           [PetId],
           -- Frozen-at-creation extras for the parent "my bookings" cards (the
           -- cancellation-policy + selected-location snapshots; PricePerHour above
           -- is already the frozen rate). Appended LAST so the shared booking-row
           -- reader's ordinals stay stable.
           [LocationType],
           [CancellationPolicyHours],
           [SnapshotAddressLine],
           [SnapshotCity],
           [SnapshotZipCode],
           [SnapshotLatitude],
           [SnapshotLongitude]
    FROM [Booking].[Bookings]
    WHERE [PetParentId] = @PetParentId
    ORDER BY [BookingDate] DESC, [StartTime] DESC;
END;
