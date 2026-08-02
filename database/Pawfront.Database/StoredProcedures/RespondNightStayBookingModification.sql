-- The counterparty accepts or declines the staged modification on a multi-night
-- booking. Mirror of [Booking].[RespondBookingModification]; the accept-path
-- capacity re-check is PER NIGHT across the proposed range (excluding this
-- booking). The staging row is DELETED either way. THROWs: 51265 not found,
-- 51266 forbidden, 51267 no proposal awaiting your response, 51268 no capacity,
-- 51272 the proposal expired before it was answered.
--
-- An unanswered proposal — from EITHER party (widened 2026-08-02; previously
-- parent-only) — expires 2 hours before drop-off on the check-in day, so a
-- response landing at or after that cutoff is rejected (THROW 51272).
-- Mirror of 51152. REJECT ONLY: the revert to CONFIRMED and the discard of the
-- staging row are left to the scheduled external job, the single writer for
-- time-driven status changes.
-- An accept also re-freezes the acknowledged terms staged with the proposal
-- (price per night, cancellation policy, drop-off / pick-up times, and the
-- selected-location address) when the requester confirmed a drift; a decline
-- applies none of them.
CREATE OR ALTER PROCEDURE [Booking].[RespondNightStayBookingModification]
    @NightStayBookingId UNIQUEIDENTIFIER,
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
    DECLARE @CheckInDate DATE;
    DECLARE @DropOffTime TIME(0);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @ProviderId = [ProviderId],
           @PetParentId = [PetParentId], @ServiceId = [ServiceId],
           @CheckInDate = [CheckInDate], @DropOffTime = [DropOffTime]
    FROM [Booking].[NightStayBookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51265, 'Night stay booking was not found.', 1;
    END

    IF (@Actor = N'Provider' AND @ActorId <> @ProviderId)
       OR (@Actor = N'Parent' AND (@PetParentId IS NULL OR @ActorId <> @PetParentId))
    BEGIN
        THROW 51266, 'You are not a party to this booking.', 1;
    END

    -- An unanswered proposal — from either party — dies 2 hours before drop-off
    -- on the check-in day (all times UTC): reject the attempted response,
    -- whichever side is answering. Checked before the counterparty test so the
    -- rejection is the same whichever party calls. REJECT ONLY: the revert to
    -- CONFIRMED and the discard of the staging row are left to the scheduled
    -- external job, the single writer for time-driven status changes.
    IF @CurrentStatus IN (N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER')
    BEGIN
        DECLARE @StartsAtUtc DATETIME2(7) =
            DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @DropOffTime),
                    CAST(@CheckInDate AS DATETIME2(7)));

        IF @Now >= DATEADD(HOUR, -2, @StartsAtUtc)
        BEGIN
            THROW 51272, 'The modification request expired 2 hours before the service start time and can no longer be answered.', 1;
        END
    END

    DECLARE @ExpectedStatus NVARCHAR(48) =
        CASE WHEN @Actor = N'Provider' THEN N'MODIFICATION_REQUEST_BY_PARENT'
             ELSE N'MODIFICATION_REQUEST_BY_PROVIDER' END;

    IF @CurrentStatus <> @ExpectedStatus
    BEGIN
        THROW 51267, 'There is no modification request awaiting your response.', 1;
    END

    DECLARE @ModId UNIQUEIDENTIFIER, @PIn DATE, @POut DATE;
    DECLARE @HasTerms BIT, @TPrice DECIMAL(10, 2), @TPolicyHours INT,
            @TDropOff TIME(0), @TPickUp TIME(0),
            @TAddressLine NVARCHAR(500), @TCity NVARCHAR(200), @TZipCode NVARCHAR(32),
            @TLatitude DECIMAL(9, 6), @TLongitude DECIMAL(9, 6);
    SELECT @ModId = [NightStayBookingModificationId], @PIn = [ProposedCheckInDate], @POut = [ProposedCheckOutDate],
           @HasTerms = [HasAcknowledgedTerms], @TPrice = [AcknowledgedPricePerNight],
           @TPolicyHours = [AcknowledgedCancellationPolicyHours],
           @TDropOff = [AcknowledgedDropOffTime], @TPickUp = [AcknowledgedPickUpTime],
           @TAddressLine = [AcknowledgedAddressLine], @TCity = [AcknowledgedCity],
           @TZipCode = [AcknowledgedZipCode], @TLatitude = [AcknowledgedLatitude],
           @TLongitude = [AcknowledgedLongitude]
    FROM [Booking].[NightStayBookingModifications] WITH (UPDLOCK, HOLDLOCK)
    WHERE [NightStayBookingId] = @NightStayBookingId;

    IF @ModId IS NULL
    BEGIN
        THROW 51267, 'There is no modification request awaiting your response.', 1;
    END

    DECLARE @NewStatus NVARCHAR(48);

    IF @Accept = 1
    BEGIN
        -- Per-night capacity re-check on the proposed range, excluding this stay.
        DECLARE @FullNight DATE;
        ;WITH [Nights] AS
        (
            SELECT @PIn AS [Night]
            UNION ALL
            SELECT DATEADD(DAY, 1, [Night]) FROM [Nights] WHERE DATEADD(DAY, 1, [Night]) < @POut
        )
        SELECT TOP (1) @FullNight = n.[Night]
        FROM [Nights] n
        LEFT JOIN [Booking].[NightStayBookings] b WITH (UPDLOCK, HOLDLOCK)
            ON b.[ServiceId] = @ServiceId
           AND b.[NightStayBookingId] <> @NightStayBookingId
           AND b.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
           AND b.[CheckInDate] <= n.[Night]
           AND b.[CheckOutDate] > n.[Night]
        GROUP BY n.[Night]
        HAVING COUNT(b.[NightStayBookingId]) >= @Capacity
        OPTION (MAXRECURSION 366);

        IF @FullNight IS NOT NULL
        BEGIN
            THROW 51268, 'No remaining capacity for one or more proposed nights.', 1;
        END

        SET @NewStatus = CASE WHEN @Actor = N'Provider' THEN N'PROVIDER_ACCEPTED_MODIFICATION'
                              ELSE N'PARENT_ACCEPTED_MODIFICATION' END;

        -- Staging -> main: the proposed range, plus the acknowledged terms when the
        -- requester confirmed a drift. Drop-off / pick-up are NOT NULL on the stay,
        -- so a staged NULL falls back to the frozen value rather than failing.
        UPDATE [Booking].[NightStayBookings]
        SET [CheckInDate] = @PIn,
            [CheckOutDate] = @POut,
            [PricePerNight] = CASE WHEN @HasTerms = 1 THEN @TPrice ELSE [PricePerNight] END,
            [CancellationPolicyHours] = CASE WHEN @HasTerms = 1 THEN @TPolicyHours ELSE [CancellationPolicyHours] END,
            [DropOffTime] = CASE WHEN @HasTerms = 1 AND @TDropOff IS NOT NULL THEN @TDropOff ELSE [DropOffTime] END,
            [PickUpTime] = CASE WHEN @HasTerms = 1 AND @TPickUp IS NOT NULL THEN @TPickUp ELSE [PickUpTime] END,
            [SnapshotAddressLine] = CASE WHEN @HasTerms = 1 THEN @TAddressLine ELSE [SnapshotAddressLine] END,
            [SnapshotCity] = CASE WHEN @HasTerms = 1 THEN @TCity ELSE [SnapshotCity] END,
            [SnapshotZipCode] = CASE WHEN @HasTerms = 1 THEN @TZipCode ELSE [SnapshotZipCode] END,
            [SnapshotLatitude] = CASE WHEN @HasTerms = 1 THEN @TLatitude ELSE [SnapshotLatitude] END,
            [SnapshotLongitude] = CASE WHEN @HasTerms = 1 THEN @TLongitude ELSE [SnapshotLongitude] END,
            [Status] = @NewStatus,
            [UpdatedAtUtc] = @Now
        WHERE [NightStayBookingId] = @NightStayBookingId;
    END
    ELSE
    BEGIN
        SET @NewStatus = CASE WHEN @Actor = N'Provider' THEN N'PROVIDER_DECLINED_MODIFICATION'
                              ELSE N'PARENT_DECLINED_MODIFICATION' END;

        UPDATE [Booking].[NightStayBookings]
        SET [Status] = @NewStatus, [UpdatedAtUtc] = @Now
        WHERE [NightStayBookingId] = @NightStayBookingId;
    END

    DELETE FROM [Booking].[NightStayBookingModifications] WHERE [NightStayBookingModificationId] = @ModId;

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
