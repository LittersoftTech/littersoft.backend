CREATE OR ALTER PROCEDURE [Booking].[GetBooking]
    @BookingId UNIQUEIDENTIFIER
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
           [CashNotReceivedAtUtc], [CancellationReason],
           CAST(CASE WHEN EXISTS (SELECT 1 FROM [Booking].[BookingStatusHistory] mh WHERE mh.[BookingId] = [Booking].[Bookings].[BookingId] AND mh.[ToStatus] = N'PARENT_ACCEPTED_MODIFICATION') THEN 1 ELSE 0 END AS BIT) AS [ProviderModificationUsed],
           CASE WHEN [Booking].[Bookings].[Status] = N'CREATED' THEN
               [Booking].[RequestExpiresAtUtc]([Booking].[Bookings].[CreatedAtUtc], [Booking].[Bookings].[BookingDate], [Booking].[Bookings].[StartTime], NULL, NULL)
           WHEN [Booking].[Bookings].[Status] IN (N'MODIFICATION_REQUEST_BY_PARENT',N'MODIFICATION_REQUEST_BY_PROVIDER') THEN
               (SELECT [Booking].[RequestExpiresAtUtc](m.[CreatedAtUtc], [Booking].[Bookings].[BookingDate], [Booking].[Bookings].[StartTime], m.[ProposedBookingDate], m.[ProposedStartTime])
                FROM [Booking].[BookingModifications] m WHERE m.[BookingId] = [Booking].[Bookings].[BookingId])
           END AS [RequestExpiresAtUtc],
           (SELECT pay.[PaidAtUtc] FROM [Booking].[BookingPayments] pay
            WHERE pay.[BookingType]=N'SingleDay' AND pay.[BookingId]=[Booking].[Bookings].[BookingId]) AS [PaidAtUtc]
    FROM [Booking].[Bookings]
    WHERE [BookingId] = @BookingId;
END;
