CREATE OR ALTER PROCEDURE [Booking].[ListBookingsByProvider]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceId UNIQUEIDENTIFIER = NULL,
    @BookingDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- @BookingDate narrows to a single calendar day when provided (provider-day
    -- view in the mobile UI). Omit it to return the full history.
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
           [PetId]
    FROM [Booking].[Bookings]
    WHERE [ProviderId] = @ProviderId
      AND (@ServiceId IS NULL OR [ServiceId] = @ServiceId)
      AND (@BookingDate IS NULL OR [BookingDate] = @BookingDate)
    ORDER BY [BookingDate] DESC, [StartTime] DESC;
END;
