CREATE OR ALTER PROCEDURE [Booking].[CancelBooking]
    @BookingId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(32);
    DECLARE @CurrentParent UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status],
           @CurrentParent = [PetParentId]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51063, 'Booking was not found.', 1;
    END

    IF @CurrentParent <> @PetParentId
    BEGIN
        THROW 51064, 'Only the original booker can cancel this booking.', 1;
    END

    IF @CurrentStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'JOB_INCOMPLETE', N'OTP_MAX_ATTEMPTS_EXCEEDED')
    BEGIN
        THROW 51065, 'Booking is already cancelled.', 1;
    END

    UPDATE [Booking].[Bookings]
    SET [Status] = N'PARENT_CANCELLED',
        [CancelledAtUtc] = @Now,
        [UpdatedAtUtc] = @Now
    WHERE [BookingId] = @BookingId;

    -- Audit the cancellation (the booker is, by definition, the parent here).
    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, N'PARENT_CANCELLED', N'Parent', @PetParentId, NULL);

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

    COMMIT TRANSACTION;
END;
