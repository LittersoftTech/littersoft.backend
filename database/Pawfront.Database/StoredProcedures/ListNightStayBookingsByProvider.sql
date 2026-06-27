CREATE OR ALTER PROCEDURE [Booking].[ListNightStayBookingsByProvider]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceId UNIQUEIDENTIFIER = NULL,
    @OnDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- @OnDate narrows to stays that include that night (CheckInDate <= date <
    -- CheckOutDate) — the provider-day view. Omit it to return full history.
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
           [PetId]
    FROM [Booking].[NightStayBookings]
    WHERE [ProviderId] = @ProviderId
      AND (@ServiceId IS NULL OR [ServiceId] = @ServiceId)
      AND (@OnDate IS NULL OR (@OnDate >= [CheckInDate] AND @OnDate < [CheckOutDate]))
    ORDER BY [CheckInDate] DESC, [CheckOutDate] DESC;
END;
