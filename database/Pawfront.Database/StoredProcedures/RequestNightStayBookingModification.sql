-- Stages a check-in/check-out change proposal on a live multi-night booking and
-- moves it to MODIFICATION_REQUEST_BY_{PARENT|PROVIDER}. Mirror of
-- [Booking].[RequestBookingModification]. THROWs: 51260 not found, 51261
-- forbidden, 51262 not in a modifiable state, 51263 a proposal is already open,
-- 51271 the modification window has closed.
--
-- NEITHER PARTY may open a proposal once the stay's drop-off is less than 2 hours
-- away (THROW 51271; the stay's "service start" is CheckInDate + DropOffTime, the
-- same instant its no-show grace runs from). Mirror of 51151. (Widened
-- 2026-08-02 — the provider side was previously ungated and never auto-expired;
-- both parties are now symmetric.)
--
-- The @Acknowledged* params carry the provider's CURRENT terms as shown to and
-- confirmed by the requester when they had drifted since the stay was created;
-- they are staged here and applied on accept only. A stay adds the offering's
-- drop-off / pick-up times to the acknowledged set.
CREATE OR ALTER PROCEDURE [Booking].[RequestNightStayBookingModification]
    @NightStayBookingId UNIQUEIDENTIFIER,
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @ProposedCheckInDate DATE,
    @ProposedCheckOutDate DATE,
    @Note NVARCHAR(500) = NULL,
    @HasAcknowledgedTerms BIT = 0,
    @AcknowledgedPricePerNight DECIMAL(10, 2) = NULL,
    @AcknowledgedCancellationPolicyHours INT = NULL,
    @AcknowledgedDropOffTime TIME(0) = NULL,
    @AcknowledgedPickUpTime TIME(0) = NULL,
    @AcknowledgedAddressLine NVARCHAR(500) = NULL,
    @AcknowledgedCity NVARCHAR(200) = NULL,
    @AcknowledgedZipCode NVARCHAR(32) = NULL,
    @AcknowledgedLatitude DECIMAL(9, 6) = NULL,
    @AcknowledgedLongitude DECIMAL(9, 6) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @ProviderId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;
    DECLARE @CheckInDate DATE;
    DECLARE @DropOffTime TIME(0);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @ProviderId = [ProviderId], @PetParentId = [PetParentId],
           @CheckInDate = [CheckInDate], @DropOffTime = [DropOffTime]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51260, 'Night stay booking was not found.', 1;
    END

    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51261, 'You are not a party to this booking.', 1;
    END

    IF @CurrentStatus NOT IN (N'CONFIRMED', N'PROVIDER_ACCEPTED_MODIFICATION',
                              N'PARENT_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                              N'PARENT_DECLINED_MODIFICATION')
    BEGIN
        THROW 51262, 'A modification can only be requested on a confirmed booking.', 1;
    END

    -- The modification window closes 2 hours before drop-off on the check-in day
    -- (all times UTC), for either party's proposal.
    DECLARE @StartsAtUtc DATETIME2(7) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @DropOffTime),
                CAST(@CheckInDate AS DATETIME2(7)));

    IF @Now >= DATEADD(HOUR, -2, @StartsAtUtc)
    BEGIN
        THROW 51271, 'A booking can no longer be modified within 2 hours of the service start time.', 1;
    END

    IF EXISTS (SELECT 1 FROM [Booking].[NightStayBookingModifications] WHERE [NightStayBookingId] = @NightStayBookingId)
    BEGIN
        THROW 51263, 'A modification request is already awaiting a response.', 1;
    END

    INSERT INTO [Booking].[NightStayBookingModifications]
        ([NightStayBookingId], [RequestedByActor], [RequestedByActorId],
         [ProposedCheckInDate], [ProposedCheckOutDate], [RequestNote],
         [HasAcknowledgedTerms], [AcknowledgedPricePerNight], [AcknowledgedCancellationPolicyHours],
         [AcknowledgedDropOffTime], [AcknowledgedPickUpTime],
         [AcknowledgedAddressLine], [AcknowledgedCity], [AcknowledgedZipCode],
         [AcknowledgedLatitude], [AcknowledgedLongitude])
    VALUES
        (@NightStayBookingId, @Actor, @ActorId, @ProposedCheckInDate, @ProposedCheckOutDate, @Note,
         ISNULL(@HasAcknowledgedTerms, 0), @AcknowledgedPricePerNight, @AcknowledgedCancellationPolicyHours,
         @AcknowledgedDropOffTime, @AcknowledgedPickUpTime,
         @AcknowledgedAddressLine, @AcknowledgedCity, @AcknowledgedZipCode,
         @AcknowledgedLatitude, @AcknowledgedLongitude);

    DECLARE @NewStatus NVARCHAR(48) =
        CASE WHEN @Actor = N'Provider' THEN N'MODIFICATION_REQUEST_BY_PROVIDER'
             ELSE N'MODIFICATION_REQUEST_BY_PARENT' END;

    UPDATE [Booking].[NightStayBookings]
    SET [Status] = @NewStatus, [UpdatedAtUtc] = @Now
    WHERE [NightStayBookingId] = @NightStayBookingId;

    INSERT INTO [Booking].[NightStayBookingStatusHistory]
        ([NightStayBookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@NightStayBookingId, @CurrentStatus, @NewStatus, @Actor, @ActorId, @Note);

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
    WHERE [NightStayBookingId] = @NightStayBookingId;

    COMMIT TRANSACTION;
END;
