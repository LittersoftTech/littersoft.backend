CREATE OR ALTER PROCEDURE [Provider].[SetProviderActiveStatus]
    @ProviderId UNIQUEIDENTIFIER,
    @IsActive BIT,
    -- "Honour bookings & deactivate": the provider has been shown the future
    -- bookings and commits to serving them, so deactivate for NEW bookings and
    -- leave the existing ones alone. Defaults to 0, which keeps the historic
    -- refusal — the app shows the conflict list first, then resubmits with 1.
    @AcknowledgeExistingBookings BIT = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    -- Lock the provider row so a concurrent SetProviderActiveStatus / booking
    -- create on the same provider serialises behind us.
    IF NOT EXISTS (
        SELECT 1
        FROM [Provider].[Providers] WITH (UPDLOCK, HOLDLOCK)
        WHERE [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51100, 'Provider profile was not found.', 1;
    END

    -- A deleted account stays disabled permanently — reactivating it would make
    -- an anonymised provider bookable again.
    IF EXISTS (SELECT 1 FROM [Provider].[Providers]
               WHERE [ProviderId] = @ProviderId AND [IsDeleted] = 1)
    BEGIN
        THROW 51115, 'This provider account has been deleted.', 1;
    END

    DECLARE @HonouredBookingCount INT = 0;

    -- When DEACTIVATING, collect the future active (non-cancelled) bookings
    -- across ALL of this provider's services. A booking is "in the future"
    -- when its date is strictly after today, OR it's today but hasn't ended yet —
    -- "ended" meaning COALESCE([ActualEndTime], [EndTime]), so a job the provider
    -- already finished early today stops counting as an outstanding commitment
    -- the moment it is completed rather than when it was scheduled to end.
    -- UPDLOCK + HOLDLOCK serialises us against concurrent Booking.CreateBooking so
    -- no booking can sneak in between the check and the flip. That still matters
    -- on the acknowledge path: the provider agreed to honour the bookings they
    -- were SHOWN, so one landing mid-flip must be rejected by the flag rather
    -- than silently added to their commitments.
    IF @IsActive = 0
    BEGIN
        DECLARE @Today DATE = CAST(SYSUTCDATETIME() AS DATE);
        DECLARE @NowTime TIME(0) = CAST(SYSUTCDATETIME() AS TIME(0));

        DECLARE @Conflicts TABLE (
            BookingId UNIQUEIDENTIFIER NOT NULL,
            ServiceId UNIQUEIDENTIFIER NOT NULL,
            ServiceCategory NVARCHAR(64) NOT NULL,
            SubCategory NVARCHAR(64) NOT NULL,
            PetParentId UNIQUEIDENTIFIER NULL,
            Source NVARCHAR(16) NOT NULL,
            CustomerName NVARCHAR(200) NULL,
            BookingDate DATE NOT NULL,
            StartTime TIME(0) NOT NULL,
            EndTime TIME(0) NOT NULL
        );

        INSERT INTO @Conflicts (BookingId, ServiceId, ServiceCategory, SubCategory,
                                PetParentId, Source, CustomerName, BookingDate, StartTime, EndTime)
        SELECT b.[BookingId], b.[ServiceId], b.[ServiceCategory], b.[SubCategory],
               b.[PetParentId], b.[Source], b.[CustomerName],
               b.[BookingDate], b.[StartTime], b.[EndTime]
        FROM [Booking].[Bookings] AS b WITH (UPDLOCK, HOLDLOCK)
        WHERE b.[ProviderId] = @ProviderId
          AND b.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
          AND (
              b.[BookingDate] > @Today
              OR (b.[BookingDate] = @Today AND COALESCE(b.[ActualEndTime], b.[EndTime]) > @NowTime)
          );

        SELECT @HonouredBookingCount = COUNT(*) FROM @Conflicts;

        -- Without the acknowledgement the bookings BLOCK the deactivation, as
        -- they always have. With it they are honoured: the flip goes ahead and
        -- the count travels back on the success row so the caller can confirm
        -- how many jobs the provider just committed to. Nothing about those
        -- bookings changes — [IsActive] is only ever read by the three booking
        -- CREATE procedures, so an existing job stays startable and completable.
        IF @HonouredBookingCount > 0 AND @AcknowledgeExistingBookings = 0
        BEGIN
            -- Conflict-shape result set: 10 columns (was 8 before custom-job
            -- support landed). The Application reader detects this shape vs
            -- the 4-column success shape and emits the BookingsExist variant.
            -- No write happened — rollback to release the UPDLOCK + HOLDLOCK.
            SELECT BookingId, ServiceId, ServiceCategory, SubCategory,
                   PetParentId, Source, CustomerName,
                   BookingDate, StartTime, EndTime
            FROM @Conflicts
            ORDER BY BookingDate ASC, StartTime ASC;

            ROLLBACK TRANSACTION;
            RETURN;
        END
    END

    UPDATE [Provider].[Providers]
    SET [IsActive] = @IsActive,
        [UpdatedAtUtc] = SYSUTCDATETIME()
    WHERE [ProviderId] = @ProviderId;

    -- Success-shape result set: 4 columns. [HonouredBookingCount] is 0 on
    -- activation and on a deactivation with nothing outstanding.
    SELECT @ProviderId AS [ProviderId],
           @IsActive AS [IsActive],
           SYSUTCDATETIME() AS [UpdatedAtUtc],
           @HonouredBookingCount AS [HonouredBookingCount];

    COMMIT TRANSACTION;
END;
