-- Full-replace edit of a parent-organised event. Mirror of
-- [Event].[UpdateEvent] keyed by @PetParentId (THROW 51217 when the event
-- doesn't exist or isn't owned by the parent → API 404 EventNotFound).
--
-- Returns the same four result sets as [Event].[GetEvent]:
--   1 event row (incl. TotalBookings), 2 amenities, 3 payment options,
--   4 attendee names.
CREATE OR ALTER PROCEDURE [Event].[UpdatePetParentEvent]
    @EventId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @EventCategory NVARCHAR(64),
    @IsChildFriendly BIT,
    @Title NVARCHAR(200),
    @Description NVARCHAR(MAX),
    @BannerImageUrl NVARCHAR(1000) = NULL,
    @EventType NVARCHAR(32),
    @StartDate DATE,
    @EndDate DATE,
    @StartTime TIME(0),
    @EndTime TIME(0),
    @IsPaid BIT = 0,
    @Price DECIMAL(18, 2) = NULL,
    @CancellationPolicy NVARCHAR(32) = NULL,
    @EventLink NVARCHAR(1000) = NULL,
    @AmenitiesJson NVARCHAR(MAX) = N'[]',
    @MaximumCapacity INT = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult = sys.sp_getapplock @Resource=N'EventLifecycle', @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
    IF @LockResult < 0 THROW 51468, 'Event operation is busy. Retry the request.', 1;


    IF NOT EXISTS (
        SELECT 1
        FROM [Event].[Events]
        WHERE [EventId] = @EventId AND [PetParentId] = @PetParentId
    )
    BEGIN
        THROW 51217, 'Event was not found for this pet parent.', 1;
    END


    IF EXISTS (SELECT 1 FROM [Event].[EventBookings] WHERE EventId=@EventId)
        THROW 51465, 'Events are locked after the first booking, including cancelled bookings.', 1;
    IF EXISTS (SELECT 1 FROM [Event].[Events] WHERE EventId=@EventId AND EventStatus<>N'Active')
        THROW 51465, 'Cancelled events cannot be edited.', 1;
    IF EXISTS (
        SELECT @EventCategory,@IsChildFriendly,@Title,@EventType,@StartDate,@EndDate,@StartTime,@EndTime,@IsPaid,@Price,@CancellationPolicy,@EventLink
        EXCEPT
        SELECT EventCategory,IsChildFriendly,Title,EventType,StartDate,EndDate,StartTime,EndTime,IsPaid,Price,CancellationPolicy,EventLink
        FROM [Event].[Events] WHERE EventId=@EventId)
        THROW 51468, 'Only banner, description, capacity and amenities can be edited.', 1;
    DECLARE @PreviousCapacity INT=(SELECT MaximumCapacity FROM [Event].[Events] WHERE EventId=@EventId);

    UPDATE [Event].[Events]
    SET [MaximumCapacity] = @MaximumCapacity,
        [EventCategory]      = @EventCategory,
        [IsChildFriendly]    = @IsChildFriendly,
        [Title]              = @Title,
        [Description]        = @Description,
        [BannerImageUrl]     = @BannerImageUrl,
        [EventType]          = @EventType,
        [StartDate]          = @StartDate,
        [EndDate]            = @EndDate,
        [StartTime]          = @StartTime,
        [EndTime]            = @EndTime,
        [IsPaid]             = @IsPaid,
        [Price]              = CASE WHEN @IsPaid = 1 THEN @Price ELSE NULL END,
        [CancellationPolicy] = @CancellationPolicy,
        [EventLink] = @EventLink,
        [UpdatedAtUtc]       = SYSUTCDATETIME()
    WHERE [EventId] = @EventId;

    -- Replace amenities wholesale.
    DELETE FROM [Event].[EventAmenities] WHERE [EventId] = @EventId;
    INSERT INTO [Event].[EventAmenities] ([EventId], [Amenity])
    SELECT DISTINCT @EventId, [value]
    FROM OPENJSON(@AmenitiesJson)
    WHERE [value] IS NOT NULL AND LEN(LTRIM(RTRIM([value]))) > 0;

    -- Payout methods only apply to paid events.
    IF @IsPaid = 0
    BEGIN
        DELETE FROM [Event].[EventPayoutMethods] WHERE [EventId] = @EventId;
    END

    -- Result set 1: event row (incl. TotalBookings).
    SELECT e.[EventId],
           e.[ProviderId],
           e.[PetParentId],
           e.[EventCategory],
           e.[IsChildFriendly],
           e.[Title],
           e.[Description],
           e.[BannerImageUrl],
           e.[EventType],
           e.[StartDate],
           e.[EndDate],
           e.[StartTime],
           e.[EndTime],
           e.[CreatedAtUtc],
           e.[UpdatedAtUtc],
           e.[ViewCount],
           e.[ShareCount],
           e.[InquiryCount],
           e.[IsPaid],
           e.[Price],
           e.[CancellationPolicy],
           COALESCE(org_pr.[FirstName] + N' ' + org_pr.[LastName],
                    org_pp.[FirstName] + N' ' + org_pp.[LastName]) AS [OrganizerName],
           org_pp.[ProfilePhotoUrl] AS [OrganizerImageUrl],
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] IN (N'Confirmed',N'Attended')) AS [TotalBookings],
           e.[EventLink], e.EventStatus,
           (SELECT COUNT(*) FROM [Event].[EventBookingTickets] t JOIN [Event].[EventBookings] b ON b.BookingId=t.BookingId
            WHERE t.EventId=e.EventId AND t.PaymentStatus=N'CashReceived' AND b.Status IN (N'Confirmed',N'Attended',N'NoShow')) AS AttendedCount,
           CAST((SELECT ISNULL(SUM(b.TotalAmount/b.TicketCount),0) FROM [Event].[EventBookingTickets] t JOIN [Event].[EventBookings] b ON b.BookingId=t.BookingId
            WHERE t.EventId=e.EventId AND t.PaymentStatus=N'CashReceived' AND b.Status IN (N'Confirmed',N'Attended',N'NoShow')) AS DECIMAL(18,2)) AS EarnedAmount,
           (SELECT COUNT(*) FROM [Event].[Events] ce WHERE ce.CancelledWithBookings=1 AND
             (ce.PetParentId=e.PetParentId OR ce.ProviderId=e.ProviderId)) AS EventsCancelledCount,
           e.MaximumCapacity
    FROM [Event].[Events] e
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    WHERE e.[EventId] = @EventId;

    -- Result set 2: amenities.
    SELECT [Amenity]
    FROM [Event].[EventAmenities]
    WHERE [EventId] = @EventId
    ORDER BY [Amenity];

    -- Result set 3: payment options (payout methods).
    SELECT [PayoutMethod]
    FROM [Event].[EventPayoutMethods]
    WHERE [EventId] = @EventId
    ORDER BY [PayoutMethod];

    -- Result set 4: attendees — names only (+ ticket number), non-cancelled.
    SELECT t.[AttendeeName],
           t.[TicketNumber], b.BookingId, t.PaymentStatus,
           CAST(b.TotalAmount/b.TicketCount AS DECIMAL(18,2)) AS Amount,
           (SELECT ProfilePhotoUrl FROM [Parent].[PetParents] WHERE PetParentId=b.BookerAccountId) AS ProfileImageUrl
    FROM [Event].[EventBookingTickets] t
    INNER JOIN [Event].[EventBookings] b ON b.[BookingId] = t.[BookingId]
    WHERE t.[EventId] = @EventId
      AND b.[Status] IN (N'Confirmed',N'Attended',N'NoShow')
    ORDER BY b.[CreatedAtUtc] ASC, t.[TicketNumber] ASC;

    IF @MaximumCapacity>@PreviousCapacity EXEC [Event].[NotifyEventWaitlist] @EventId;
    COMMIT TRANSACTION;
END;
