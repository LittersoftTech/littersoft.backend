-- The counterparty accepts or declines the staged modification on a single-day
-- booking. Provider responds to a MODIFICATION_REQUEST_BY_PARENT, Parent to a
-- MODIFICATION_REQUEST_BY_PROVIDER. On accept the staged date/time is applied to
-- the booking row (after a race-safe capacity re-check on the proposed window,
-- excluding this booking) and the status becomes {PROVIDER|PARENT}_ACCEPTED_MODIFICATION;
-- on decline the booking row is left unchanged and the status becomes
-- {PROVIDER|PARENT}_DECLINED_MODIFICATION. EITHER WAY the staging row is DELETED
-- ("staging -> main" on accept, discarded on decline). THROWs: 51145 not found,
-- 51146 forbidden, 51147 no proposal awaiting your response, 51148 no capacity,
-- 51152 the proposal expired before it was answered.
--
-- An unanswered proposal — from EITHER party (widened 2026-08-02; previously
-- parent-only) — expires 2 hours before the service starts, so a response
-- landing at or after that cutoff is rejected (THROW 51152).
-- REJECT ONLY: the revert to CONFIRMED and the discard of the staging row are
-- deliberately NOT performed here. Status changes driven by elapsed time belong
-- to the scheduled external job, which is the single writer for them. Until that
-- job runs the booking stays in whichever MODIFICATION_REQUEST_BY_* status it was
-- in — not a startable state, so the job is what unblocks it.
--
-- When the proposal also staged acknowledged terms ([HasAcknowledgedTerms] = 1 —
-- the provider had changed price / cancellation policy / the selected-location
-- address since the booking was created, and the requester confirmed the change),
-- the accept re-freezes those onto the booking together with the schedule. The
-- staged values are applied verbatim, NOT re-read live, so what the requester was
-- shown is what lands. A decline applies none of it.
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
    DECLARE @BookingDate DATE;
    DECLARE @StartTime TIME(0);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @ProviderId = [ProviderId],
           @PetParentId = [PetParentId], @ServiceId = [ServiceId],
           @BookingDate = [BookingDate], @StartTime = [StartTime]
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

    -- An unanswered proposal — from either party — dies 2 hours before the
    -- service starts (all times UTC): reject the attempted response, whichever
    -- side is answering. Checked before the counterparty test so the rejection is
    -- the same whichever party calls. REJECT ONLY: the revert to CONFIRMED and
    -- the discard of the staging row are left to the scheduled external job, the
    -- single writer for time-driven status changes.
    IF @CurrentStatus IN (N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER')
    BEGIN
        DECLARE @StartsAtUtc DATETIME2(7) =
            DATEADD(SECOND, DATEDIFF(SECOND, CAST('00:00:00' AS TIME(0)), @StartTime),
                    CAST(@BookingDate AS DATETIME2(7)));

        IF @Now >= DATEADD(HOUR, -2, @StartsAtUtc)
        BEGIN
            THROW 51152, 'The modification request expired 2 hours before the service start time and can no longer be answered.', 1;
        END
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
    DECLARE @HasTerms BIT, @TPrice DECIMAL(10, 2), @TPolicyHours INT,
            @TAddressLine NVARCHAR(500), @TCity NVARCHAR(200), @TZipCode NVARCHAR(32),
            @TLatitude DECIMAL(9, 6), @TLongitude DECIMAL(9, 6);
    SELECT @ModId = [BookingModificationId], @PDate = [ProposedBookingDate],
           @PStart = [ProposedStartTime], @PEnd = [ProposedEndTime],
           @HasTerms = [HasAcknowledgedTerms], @TPrice = [AcknowledgedPricePerHour],
           @TPolicyHours = [AcknowledgedCancellationPolicyHours],
           @TAddressLine = [AcknowledgedAddressLine], @TCity = [AcknowledgedCity],
           @TZipCode = [AcknowledgedZipCode], @TLatitude = [AcknowledgedLatitude],
           @TLongitude = [AcknowledgedLongitude]
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
          AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
          AND [StartTime] < @PEnd
          AND [EndTime] > @PStart;

        IF @Concurrent >= @Capacity
        BEGIN
            THROW 51148, 'No remaining capacity for the proposed time.', 1;
        END

        SET @NewStatus = CASE WHEN @Actor = N'Provider' THEN N'PROVIDER_ACCEPTED_MODIFICATION'
                              ELSE N'PARENT_ACCEPTED_MODIFICATION' END;

        -- Staging -> main: copy the proposed date/time onto the booking, plus the
        -- acknowledged terms when the requester confirmed a drift. The Application
        -- layer stages a COMPLETE term set (falling back to the booking's own
        -- frozen value for anything it couldn't resolve live), so these are
        -- applied verbatim — including NULLs, which are meaningful here (a NULL
        -- cancellation policy is "no restriction").
        UPDATE [Booking].[Bookings]
        SET [BookingDate] = @PDate,
            [StartTime] = @PStart,
            [EndTime] = @PEnd,
            [PricePerHour] = CASE WHEN @HasTerms = 1 THEN @TPrice ELSE [PricePerHour] END,
            [CancellationPolicyHours] = CASE WHEN @HasTerms = 1 THEN @TPolicyHours ELSE [CancellationPolicyHours] END,
            [SnapshotAddressLine] = CASE WHEN @HasTerms = 1 THEN @TAddressLine ELSE [SnapshotAddressLine] END,
            [SnapshotCity] = CASE WHEN @HasTerms = 1 THEN @TCity ELSE [SnapshotCity] END,
            [SnapshotZipCode] = CASE WHEN @HasTerms = 1 THEN @TZipCode ELSE [SnapshotZipCode] END,
            [SnapshotLatitude] = CASE WHEN @HasTerms = 1 THEN @TLatitude ELSE [SnapshotLatitude] END,
            [SnapshotLongitude] = CASE WHEN @HasTerms = 1 THEN @TLongitude ELSE [SnapshotLongitude] END,
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
