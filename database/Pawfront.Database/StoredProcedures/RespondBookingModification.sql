-- The counterparty accepts or declines the staged modification on a single-day
-- booking. Provider responds to a MODIFICATION_REQUEST_BY_PARENT, Parent to a
-- MODIFICATION_REQUEST_BY_PROVIDER. On accept the staged date/time is applied to
-- the booking row (after a race-safe capacity re-check on the proposed window,
-- excluding this booking) and the status becomes {PROVIDER|PARENT}_ACCEPTED_MODIFICATION;
-- on decline the booking row is left unchanged and the status becomes
-- {PROVIDER|PARENT}_DECLINED_MODIFICATION. EITHER WAY the staging row is DELETED
-- ("staging -> main" on accept, discarded on decline). THROWs: 51145 not found,
-- 51146 forbidden, 51147 no proposal awaiting your response, 51148 no capacity.
CREATE OR ALTER PROCEDURE [Booking].[RespondBookingModification]
    @BookingId UNIQUEIDENTIFIER,
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @Accept BIT,
    @Capacity INT,
    @Note NVARCHAR(500) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @ServiceId UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @ProviderId = [ProviderId],
           @PetParentId = [PetParentId], @ServiceId = [ServiceId]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51145, 'Booking was not found.', 1;
    END

    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51146, 'You are not a party to this booking.', 1;
    END

    -- The responder is the counterparty: provider answers the parent's request
    -- and vice versa.
    DECLARE @ExpectedStatus NVARCHAR(48) =
        CASE WHEN @Actor = N'Provider' THEN N'MODIFICATION_REQUEST_BY_PARENT'
             ELSE N'MODIFICATION_REQUEST_BY_PROVIDER' END;

    IF @CurrentStatus <> @ExpectedStatus
    BEGIN
        THROW 51147, 'There is no modification request awaiting your response.', 1;
    END

    DECLARE @ModId UNIQUEIDENTIFIER, @PDate DATE, @PStart TIME(0), @PEnd TIME(0);
    SELECT @ModId = [BookingModificationId], @PDate = [ProposedBookingDate],
           @PStart = [ProposedStartTime], @PEnd = [ProposedEndTime]
    FROM [Booking].[BookingModifications] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @ModId IS NULL
    BEGIN
        THROW 51147, 'There is no modification request awaiting your response.', 1;
    END

    DECLARE @NewStatus NVARCHAR(48);

    IF @Accept = 1
    BEGIN
        -- Race-safe capacity re-check on the proposed window, excluding this booking.
        DECLARE @Concurrent INT;
        SELECT @Concurrent = COUNT(*)
        FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [BookingDate] = @PDate
          AND [BookingId] <> @BookingId
          AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED')
          AND [StartTime] < @PEnd
          AND [EndTime] > @PStart;

        IF @Concurrent >= @Capacity
        BEGIN
            THROW 51148, 'No remaining capacity for the proposed time.', 1;
        END

        SET @NewStatus = CASE WHEN @Actor = N'Provider' THEN N'PROVIDER_ACCEPTED_MODIFICATION'
                              ELSE N'PARENT_ACCEPTED_MODIFICATION' END;

        -- Staging -> main: copy the proposed date/time onto the booking.
        UPDATE [Booking].[Bookings]
        SET [BookingDate] = @PDate,
            [StartTime] = @PStart,
            [EndTime] = @PEnd,
            [Status] = @NewStatus,
            [UpdatedAtUtc] = @Now
        WHERE [BookingId] = @BookingId;
    END
    ELSE
    BEGIN
        SET @NewStatus = CASE WHEN @Actor = N'Provider' THEN N'PROVIDER_DECLINED_MODIFICATION'
                              ELSE N'PARENT_DECLINED_MODIFICATION' END;

        UPDATE [Booking].[Bookings]
        SET [Status] = @NewStatus, [UpdatedAtUtc] = @Now
        WHERE [BookingId] = @BookingId;
    END

    -- Remove the proposal from staging (consumed on accept, discarded on decline).
    DELETE FROM [Booking].[BookingModifications] WHERE [BookingModificationId] = @ModId;

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
