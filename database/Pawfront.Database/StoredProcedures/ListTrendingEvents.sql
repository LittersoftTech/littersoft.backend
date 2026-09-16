-- Trending events: ranked by engagement = ViewCount + ShareCount + total
-- (non-cancelled / Confirmed) ticket bookings, highest first. Returns the same
-- two result sets as [Event].[ListEvents] (event rows + amenities) so the
-- application reader (ReadEventRow) is shared. @Take caps the number of rows
-- (default 20, clamped to 1..100).
CREATE OR ALTER PROCEDURE [Event].[ListTrendingEvents]
    @Take INT = 20,
    -- The caller, so a blocked pair never see each other's events. Both NULL
    -- for a legacy or unauthenticated caller, which filters nothing.
    @ViewerType      NVARCHAR(16)     = NULL,
    @ViewerId        UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF (@Take IS NULL OR @Take < 1) SET @Take = 20;
    IF (@Take > 100) SET @Take = 100;

    -- Pick the top events once so both result sets share the same set.
    DECLARE @TopEvents TABLE
    (
        [EventId]       UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        [TrendingScore] INT              NOT NULL,
        [StartDate]     DATE             NOT NULL
    );

    INSERT INTO @TopEvents ([EventId], [TrendingScore], [StartDate])
    SELECT TOP (@Take)
           e.[EventId],
           e.[ViewCount] + e.[ShareCount] +
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed'),
           e.[StartDate]
    FROM [Event].[Events] e
    -- Filtered BEFORE the TOP, so an event the caller cannot see does not
    -- consume one of the @Take slots and silently shorten their list.
    -- A block hides each party's events from the other, both ways. The
    -- organiser is whichever of the two id columns is set (a CHECK enforces
    -- exactly one), and GUIDs are globally unique, so matching on the id alone
    -- is safe without branching on organiser type -- the same reasoning the
    -- self-booking check uses.
    WHERE (@ViewerId IS NULL OR NOT EXISTS (
          SELECT 1
          FROM [Block].[BlockedParticipants] bp
          WHERE (bp.[BlockerType] = @ViewerType AND bp.[BlockerId] = @ViewerId
                 AND bp.[BlockedId] = COALESCE(e.[ProviderId], e.[PetParentId]))
             OR (bp.[BlockedType] = @ViewerType AND bp.[BlockedId] = @ViewerId
                 AND bp.[BlockerId] = COALESCE(e.[ProviderId], e.[PetParentId]))
        ))
    ORDER BY e.[ViewCount] + e.[ShareCount] +
             (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
              FROM [Event].[EventBookings] eb
              WHERE eb.[EventId] = e.[EventId]
                AND eb.[Status] = N'Confirmed') DESC,
             e.[StartDate] DESC,
             e.[EventId] ASC;

    -- Result set 1: event rows (same column shape as [Event].[ListEvents]),
    -- ordered by the trending score (most engaging first).
    SELECT e.[EventId], e.[ProviderId], e.[PetParentId], e.[EventCategory], e.[IsChildFriendly],
           e.[Title], e.[Description], e.[BannerImageUrl], e.[EventType],
           e.[StartDate], e.[EndDate], e.[StartTime], e.[EndTime],
           e.[CreatedAtUtc], e.[UpdatedAtUtc],
           e.[ViewCount], e.[ShareCount], e.[InquiryCount],
           e.[IsPaid], e.[Price], e.[CancellationPolicy],
           COALESCE(org_pr.[FirstName] + N' ' + org_pr.[LastName],
                    org_pp.[FirstName] + N' ' + org_pp.[LastName]) AS [OrganizerName],
           org_pp.[ProfilePhotoUrl] AS [OrganizerImageUrl],
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed') AS [TotalBookings],
           e.[EventLink]
    FROM [Event].[Events] e
    INNER JOIN @TopEvents te ON te.[EventId] = e.[EventId]
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    ORDER BY te.[TrendingScore] DESC, te.[StartDate] DESC, e.[EventId] ASC;

    -- Result set 2: (EventId, Amenity) pairs for the events above.
    SELECT a.[EventId], a.[Amenity]
    FROM [Event].[EventAmenities] a
    INNER JOIN @TopEvents te ON te.[EventId] = a.[EventId]
    ORDER BY a.[EventId], a.[Amenity];
END;
