-- The counterparty accepts or declines the staged modification on a multi-night
-- booking. Mirror of [Booking].[RespondBookingModification]; the accept-path
-- capacity re-check is PER NIGHT across the proposed range (excluding this
-- booking). The staging row is DELETED either way. THROWs: 51265 not found,
-- 51266 forbidden, 51267 no proposal awaiting your response, 51268 no capacity.
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

    BEGIN TRANSACTION;

    SELECT @CurrentStatus = [Status], @ProviderId = [ProviderId],
           @PetParentId = [PetParentId], @ServiceId = [ServiceId]
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

    DECLARE @ExpectedStatus NVARCHAR(48) =
        CASE WHEN @Actor = N'Provider' THEN N'MODIFICATION_REQUEST_BY_PARENT'
             ELSE N'MODIFICATION_REQUEST_BY_PROVIDER' END;

    IF @CurrentStatus <> @ExpectedStatus
    BEGIN
        THROW 51267, 'There is no modification request awaiting your response.', 1;
    END

    DECLARE @ModId UNIQUEIDENTIFIER, @PIn DATE, @POut DATE;
    SELECT @ModId = [NightStayBookingModificationId], @PIn = [ProposedCheckInDate], @POut = [ProposedCheckOutDate]
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
           AND b.[Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED')
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

        UPDATE [Booking].[NightStayBookings]
        SET [CheckInDate] = @PIn,
            [CheckOutDate] = @POut,
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
