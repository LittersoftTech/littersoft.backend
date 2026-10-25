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
           [SnapshotLongitude],
           [CashNotReceivedAtUtc], [CancellationReason],
           CAST(CASE WHEN EXISTS (SELECT 1 FROM [Booking].[NightStayBookingStatusHistory] mh WHERE mh.[NightStayBookingId] = [Booking].[NightStayBookings].[NightStayBookingId] AND mh.[ToStatus] = N'PARENT_ACCEPTED_MODIFICATION') THEN 1 ELSE 0 END AS BIT) AS [ProviderModificationUsed],
           CASE WHEN [Booking].[NightStayBookings].[Status] = N'CREATED' THEN
               [Booking].[NightStayRequestExpiresAtUtc]([Booking].[NightStayBookings].[CreatedAtUtc], [Booking].[NightStayBookings].[CheckInDate])
           WHEN [Booking].[NightStayBookings].[Status] IN (N'MODIFICATION_REQUEST_BY_PARENT',N'MODIFICATION_REQUEST_BY_PROVIDER') THEN
               (SELECT [Booking].[NightStayRequestExpiresAtUtc](m.[CreatedAtUtc], [Booking].[NightStayBookings].[CheckInDate])
                FROM [Booking].[NightStayBookingModifications] m WHERE m.[NightStayBookingId] = [Booking].[NightStayBookings].[NightStayBookingId])
           END AS [RequestExpiresAtUtc],
           (SELECT pay.[PaidAtUtc] FROM [Booking].[BookingPayments] pay
            WHERE pay.[BookingType]=N'NightStay' AND pay.[BookingId]=[Booking].[NightStayBookings].[NightStayBookingId]) AS [PaidAtUtc]
    FROM [Booking].[NightStayBookings]
    WHERE [PetParentId] = @PetParentId
    ORDER BY [CheckInDate] DESC, [CheckOutDate] DESC;
END;
