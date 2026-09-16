-- Provider edits a Custom walk-in they recorded earlier ("edit this private job").
-- Walk-ins were create-only until 2026-08-24, so a provider's later corrections to
-- the price, the service or the notes lived on their phone and never reached the
-- server — which meant the amount the app showed and the amount in
-- [Booking].[BookingAmounts] (and therefore in earnings) could differ with nothing
-- to reconcile them.
--
-- CUSTOM WALK-INS ONLY. An App booking is a two-party agreement and is changed
-- through the modification flow, where the counterparty gets to accept or decline.
-- A walk-in is the provider's own record of their own job, so there is nobody to
-- ask — which is exactly why this is a plain edit and not a modification.
--
-- WHAT CAN BE EDITED WHEN:
--   * price, customer details, pet details, location text and notes — while the
--     booking is CONFIRMED, IN_PROGRESS **or COMPLETED**. Allowing a completed job
--     to be re-priced is deliberate: correcting the money after the fact is the
--     main thing this exists for, and a walk-in can never be marked PAID, so there
--     is no ledger row to contradict and no [PayoutId] to invalidate.
--   * the SERVICE and the SCHEDULE (date / times) — only while CONFIRMED. Moving
--     the window of a job that has already started or finished is incoherent, and
--     would mean re-running a capacity check against a slot in the past. THROW
--     51384 rather than silently ignoring those fields, so a client cannot believe
--     it saved a change it did not.
--
-- Statuses that mean the job did NOT happen (cancelled, no-show, expired) refuse
-- the edit outright: there is nothing left to correct, and re-pricing a cancelled
-- job would put money back into a figure that has already settled.
--
-- Capacity is re-checked exactly as [Booking].[CreateCustomBooking] does, under the
-- same UPDLOCK + HOLDLOCK, and MUST stay identical to it — Custom and App bookings
-- share one per-service bucket. The one difference is that this booking excludes
-- ITSELF from the count, or moving a job by five minutes would collide with itself.
--
-- THROWs: 51380 not found, 51381 not the provider on this booking, 51382 not a
-- walk-in, 51383 the job did not happen and cannot be edited, 51384 schedule or
-- service change after the job started; plus 51066 (unknown/inactive service) and
-- 51062 (no capacity) shared with the create path.
CREATE OR ALTER PROCEDURE [Booking].[UpdateCustomBooking]
    @BookingId                 UNIQUEIDENTIFIER,
    @ProviderId                UNIQUEIDENTIFIER,
    @ServiceId                 UNIQUEIDENTIFIER,
    @ServiceCategory           NVARCHAR(64),
    @SubCategory               NVARCHAR(64),
    @CustomerName              NVARCHAR(200),
    @CustomerMobileCountryCode NVARCHAR(8),
    @CustomerMobile            NVARCHAR(32),
    @AnimalType                NVARCHAR(32),
    @PetName                   NVARCHAR(100),
    @BookingDate               DATE,
    @StartTime                 TIME(0),
    @EndTime                   TIME(0),
    @ServiceLocation           NVARCHAR(32),
    @CustomerLocation          NVARCHAR(500) = NULL,
    @PricePerHour              DECIMAL(10, 2),
    @JobNotes                  NVARCHAR(2000) = NULL,
    @Capacity                  INT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @CurrentStatus NVARCHAR(48);
    DECLARE @RowProvider UNIQUEIDENTIFIER;
    DECLARE @Source NVARCHAR(16);
    DECLARE @CurrentServiceId UNIQUEIDENTIFIER;
    DECLARE @CurrentBookingDate DATE;
    DECLARE @CurrentStartTime TIME(0);
    DECLARE @CurrentEndTime TIME(0);

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status],
           @RowProvider = [ProviderId],
           @Source = [Source],
           @CurrentServiceId = [ServiceId],
           @CurrentBookingDate = [BookingDate],
           @CurrentStartTime = [StartTime],
           @CurrentEndTime = [EndTime]
    FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
    WHERE [BookingId] = @BookingId;

    IF @CurrentStatus IS NULL
    BEGIN
        THROW 51380, 'Booking was not found.', 1;
    END

    IF @RowProvider <> @ProviderId
    BEGIN
        THROW 51381, 'You are not the provider on this booking.', 1;
    END

    IF @Source <> N'Custom'
    BEGIN
        THROW 51382, 'Only a private walk-in booking can be edited here.', 1;
    END

    IF @CurrentStatus IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED',
                          N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED',
                          N'OTP_MAX_ATTEMPTS_EXCEEDED')
    BEGIN
        THROW 51383, 'This job did not happen and can no longer be edited.', 1;
    END

    DECLARE @ScheduleChanged BIT =
        CASE WHEN @ServiceId <> @CurrentServiceId
                  OR @BookingDate <> @CurrentBookingDate
                  OR @StartTime <> @CurrentStartTime
                  OR @EndTime <> @CurrentEndTime
             THEN 1 ELSE 0 END;

    IF @ScheduleChanged = 1 AND @CurrentStatus <> N'CONFIRMED'
    BEGIN
        THROW 51384, 'The service and schedule can only be changed before the job starts.', 1;
    END

    -- Only worth re-validating when the window or the service actually moved. An
    -- edit that merely corrects the price must not fail because the provider has
    -- since deactivated that service, or because the slot has filled up with the
    -- bookings that came after this one.
    IF @ScheduleChanged = 1
    BEGIN
        IF NOT EXISTS (
            SELECT 1
            FROM [Provider].[ProviderServices] WITH (UPDLOCK, HOLDLOCK)
            WHERE [ServiceId] = @ServiceId
              AND [ProviderId] = @ProviderId
              AND [IsActive] = 1
        )
        BEGIN
            THROW 51066, 'Service is not valid or active for this provider.', 1;
        END

        DECLARE @Concurrent INT;
        SELECT @Concurrent = COUNT(*)
        FROM [Booking].[Bookings] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ServiceId] = @ServiceId
          AND [BookingDate] = @BookingDate
          AND [BookingId] <> @BookingId
          AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
          AND [StartTime] < @EndTime
          AND COALESCE([ActualEndTime], [EndTime]) > @StartTime;

        IF @Concurrent >= @Capacity
        BEGIN
            THROW 51062, 'No remaining capacity for this slot.', 1;
        END
    END

    UPDATE [Booking].[Bookings]
    SET [ServiceId]                 = @ServiceId,
        [ServiceCategory]           = @ServiceCategory,
        [SubCategory]               = @SubCategory,
        [CustomerName]              = @CustomerName,
        [CustomerMobileCountryCode] = @CustomerMobileCountryCode,
        [CustomerMobile]            = @CustomerMobile,
        [AnimalType]                = @AnimalType,
        [PetName]                   = @PetName,
        [BookingDate]               = @BookingDate,
        [StartTime]                 = @StartTime,
        [EndTime]                   = @EndTime,
        [ServiceLocation]           = @ServiceLocation,
        [CustomerLocation]          = @CustomerLocation,
        [PricePerHour]              = @PricePerHour,
        [JobNotes]                  = @JobNotes,
        [UpdatedAtUtc]              = @Now
    WHERE [BookingId] = @BookingId;

    -- The status does not move, so this is not a transition — but the audit trail
    -- is the only record that the job's terms were rewritten, and on a one-party
    -- booking there is no counterparty who would otherwise notice. A row with
    -- FromStatus = ToStatus reads correctly as "edited, not transitioned".
    INSERT INTO [Booking].[BookingStatusHistory]
        ([BookingId], [FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note])
    VALUES
        (@BookingId, @CurrentStatus, @CurrentStatus, N'Provider', @ProviderId,
         CASE WHEN @ScheduleChanged = 1
              THEN N'Walk-in edited by the provider (service or schedule changed)'
              ELSE N'Walk-in edited by the provider' END);

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
