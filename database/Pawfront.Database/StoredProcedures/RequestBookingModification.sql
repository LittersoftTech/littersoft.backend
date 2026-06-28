-- Stages a date/time change proposal on a live single-day booking and moves the
-- booking to MODIFICATION_REQUEST_BY_{PARENT|PROVIDER}. The proposed window's
-- validity (working hours, closures, duration) is checked by the Application
-- layer first; here we enforce party + state + the one-open-proposal rule, then
-- insert the staging row. THROWs: 51140 not found, 51141 forbidden, 51142 not in
-- a modifiable state, 51143 a proposal is already open.
CREATE OR ALTER PROCEDURE [Booking].[RequestBookingModification]
    @BookingId UNIQUEIDENTIFIER,
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @ProposedBookingDate DATE,
    @ProposedStartTime TIME(0),
    @ProposedEndTime TIME(0),
    @Note NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @ProviderId = [ProviderId], @PetParentId = [PetParentId]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51140, 'Booking was not found.', 1;
    END

    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51141, 'You are not a party to this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION',
                              N'PARENT_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                              N'PARENT_DECLINED_MODIFICATION')
    BEGIN
        THROW 51142, 'A modification can only be requested on a confirmed booking.', 1;
    END

    IF EXISTS (SELECT 1 FROM [Booking].[BookingModifications] WHERE [BookingId] = @BookingId)
    BEGIN
        THROW 51143, 'A modification request is already awaiting a response.', 1;
    END

    INSERT INTO [Booking].[BookingModifications]
        ([BookingId], [RequestedByActor], [RequestedByActorId],
         [ProposedBookingDate], [ProposedStartTime], [ProposedEndTime], [RequestNote])
    VALUES
        (@BookingId, @Actor, @ActorId, @ProposedBookingDate, @ProposedStartTime, @ProposedEndTime, @Note);

    DECLARE @NewStatus NVARCHAR(48) =
        CASE WHEN @Actor = N'Provider' THEN N'MODIFICATION_REQUEST_BY_PROVIDER'
             ELSE N'MODIFICATION_REQUEST_BY_PARENT' END;

    UPDATE [Booking].[Bookings]
    SET [Status] = @NewStatus, [UpdatedAtUtc] = @Now
    WHERE [BookingId] = @BookingId;

    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

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
    WHERE [BookingId] = @BookingId;

    COMMIT TRANSACTION;
END;
