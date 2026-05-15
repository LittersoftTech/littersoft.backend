CREATE OR ALTER PROCEDURE [Booking].[ListBookingsByProvider]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    SELECT [BookingId],
           [ProviderId],
           [PetParentId],
           [ServiceCategory],
           [SubCategory],
           [BookingDate],
           [StartTime],
           [EndTime],
           [Status],
           [CreatedAtUtc],
           [UpdatedAtUtc],
           [CancelledAtUtc]
    FROM [Booking].[Bookings]
    WHERE [ProviderId] = @ProviderId
    ORDER BY [BookingDate] DESC, [StartTime] DESC;
END;
