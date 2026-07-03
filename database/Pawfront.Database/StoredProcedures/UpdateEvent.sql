-- Full-replace edit of a provider-organised event. Verifies the event belongs
-- to @ProviderId (THROW 51216 otherwise → API 404 EventNotFound), rewrites the
-- editable columns + amenities, and clears payout methods if the event is
-- edited to free. The Cosmos physical extension (capacity + venue location) is
-- reconciled by the application layer, not here.
--
-- Returns the same four result sets as [Event].[GetEvent]:
--   1 event row (incl. TotalBookings), 2 amenities, 3 payment options,
--   4 attendee names.
CREATE OR ALTER PROCEDURE [Event].[UpdateEvent]
    @EventId UNIQUEIDENTIFIER,
    @ProviderId UNIQUEIDENTIFIER,
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
    @AmenitiesJson NVARCHAR(MAX) = N'[]'
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    IF NOT EXISTS (
        SELECT 1
        FROM [Event].[Events]
        WHERE [EventId] = @EventId AND [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51216, 'Event was not found for this provider.', 1;
    END

    UPDATE [Event].[Events]
    SET [EventCategory]      = @EventCategory,
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

    -- Payout methods only apply to paid events — drop any stale rows if this
    -- edit turned the event free.
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
              AND eb.[Status] = N'Confirmed') AS [TotalBookings],
           e.[EventLink]
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
           t.[TicketNumber]
    FROM [Event].[EventBookingTickets] t
    INNER JOIN [Event].[EventBookings] b ON b.[BookingId] = t.[BookingId]
    WHERE t.[EventId] = @EventId
      AND b.[Status] = N'Confirmed'
    ORDER BY b.[CreatedAtUtc] ASC, t.[TicketNumber] ASC;

    COMMIT TRANSACTION;
END;
