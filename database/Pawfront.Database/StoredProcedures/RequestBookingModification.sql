-- Stages a date/time change proposal on a live single-day booking and moves the
-- booking to MODIFICATION_REQUEST_BY_{PARENT|PROVIDER}. The proposed window's
-- validity (working hours, closures, duration) is checked by the Application
-- layer first; here we enforce party + state + the one-open-proposal rule, then
-- insert the staging row. THROWs: 51140 not found, 51141 forbidden, 51142 not in
-- a modifiable state, 51143 a proposal is already open, 51151 the modification
-- window has closed.
--
-- NEITHER PARTY may open a proposal once the service starts in less than 2 hours
-- (THROW 51151) — by then the job is imminent and whoever is left waiting needs a
-- settled schedule. Same cutoff that expires an unanswered proposal, which the
-- scheduled external job settles (reverting the booking to CONFIRMED); together
-- the two rules mean that from T-2h a booking is never left sitting in either
-- MODIFICATION_REQUEST_BY_* status, so it stays startable. (Widened 2026-08-02 —
-- the provider side was previously ungated and never auto-expired; both parties
-- are now symmetric.)
--
-- When the provider's terms have drifted since the booking was created and the
-- requester acknowledged the drift, @HasAcknowledgedTerms = 1 and the
-- @Acknowledged* params carry the CURRENT terms exactly as the requester was
-- shown them. They are staged alongside the schedule and applied by
-- [Booking].[RespondBookingModification] on accept — never here, so a declined
-- proposal leaves the booking's frozen terms untouched.
CREATE OR ALTER PROCEDURE [Booking].[RequestBookingModification]
    @BookingId UNIQUEIDENTIFIER,
    @Actor NVARCHAR(16),
    @ActorId UNIQUEIDENTIFIER,
    @ProposedBookingDate DATE,
    @ProposedStartTime TIME(0),
    @ProposedEndTime TIME(0),
    @Note NVARCHAR(500) = NULL,
    @HasAcknowledgedTerms BIT = 0,
    @AcknowledgedPricePerHour DECIMAL(10, 2) = NULL,
    @AcknowledgedCancellationPolicyHours INT = NULL,
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
    DECLARE @BookingDate DATE;
    DECLARE @StartTime TIME(0);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @ProviderId = [ProviderId], @PetParentId = [PetParentId],
           @BookingDate = [BookingDate], @StartTime = [StartTime]
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

    -- The modification window closes 2 hours before the service starts (all
    -- times UTC), for either party's proposal.
    DECLARE @StartsAtUtc DATETIME2(7) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @StartTime),
                CAST(@BookingDate AS DATETIME2(7)));

    IF @Now >= DATEADD(HOUR, -2, @StartsAtUtc)
    BEGIN
        THROW 51151, 'A booking can no longer be modified within 2 hours of the service start time.', 1;
    END

    IF EXISTS (SELECT 1 FROM [Booking].[BookingModifications] WHERE [BookingId] = @BookingId)
    BEGIN
        THROW 51143, 'A modification request is already awaiting a response.', 1;
    END

    -- Captured so the notification's dedupe key can be scoped to THIS proposal
    -- rather than to the booking (see the enqueue below).
    DECLARE @InsertedModification TABLE ([BookingModificationId] UNIQUEIDENTIFIER);

    INSERT INTO [Booking].[BookingModifications]
        ([BookingId], [RequestedByActor], [RequestedByActorId],
         [ProposedBookingDate], [ProposedStartTime], [ProposedEndTime], [RequestNote],
         [HasAcknowledgedTerms], [AcknowledgedPricePerHour], [AcknowledgedCancellationPolicyHours],
         [AcknowledgedAddressLine], [AcknowledgedCity], [AcknowledgedZipCode],
         [AcknowledgedLatitude], [AcknowledgedLongitude])
    OUTPUT inserted.[BookingModificationId] INTO @InsertedModification
    VALUES
        (@BookingId, @Actor, @ActorId, @ProposedBookingDate, @ProposedStartTime, @ProposedEndTime, @Note,
         ISNULL(@HasAcknowledgedTerms, 0), @AcknowledgedPricePerHour, @AcknowledgedCancellationPolicyHours,
         @AcknowledgedAddressLine, @AcknowledgedCity, @AcknowledgedZipCode,
         @AcknowledgedLatitude, @AcknowledgedLongitude);

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

    -- The counterparty has to review it, so they are the one notified.
    DECLARE @ReqAudience NVARCHAR(16) =
        CASE WHEN @Actor = N'Provider' THEN N'PetParent' ELSE N'Provider' END;
    DECLARE @ReqType NVARCHAR(64) =
        CASE WHEN @Actor = N'Provider'
             THEN N'BOOKING_MODIFICATION_REQUESTED_BY_PROVIDER'
             ELSE N'BOOKING_MODIFICATION_REQUESTED_BY_PARENT' END;

    -- A booking can be modified more than once over its life, and each proposal
    -- is a distinct thing to review — so the dedupe key is scoped to the staging
    -- row rather than the booking, letting a later proposal notify again while
    -- still collapsing a retry of the same one.
    DECLARE @ReqDedupe NVARCHAR(64) =
        CAST((SELECT TOP 1 [BookingModificationId] FROM @InsertedModification) AS NVARCHAR(36));

    -- The proposal as a single UTC instant; the renderer localises it into the
    -- newServiceDate + newStartTime the copy quotes.
    DECLARE @ProposedStartUtc DATETIME2(0) =
        DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @ProposedStartTime),
                CAST(@ProposedBookingDate AS DATETIME2(0)));

    EXEC [Notification].[EnqueueBookingNotification]
        @BookingId = @BookingId,
        @IsNightStay = 0,
        @Audience = @ReqAudience,
        @NotificationType = @ReqType,
        @NewServiceStartUtc = @ProposedStartUtc,
        @DedupeSuffix = @ReqDedupe;

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
