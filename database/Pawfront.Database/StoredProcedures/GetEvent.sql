CREATE OR ALTER PROCEDURE [Event].[GetEvent]
    @EventId UNIQUEIDENTIFIER,
    -- The caller, so a blocked pair never see each other's events. Both NULL
    -- for a legacy or unauthenticated caller, which filters nothing.
    @ViewerType NVARCHAR(16)     = NULL,
    @ViewerId   UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- A block hides each party's events from the other, both ways. Rather than
    -- repeat the predicate on all four result sets below -- and risk one being
    -- forgotten, leaking the attendee list for an event the caller must not see
    -- -- the id itself is cleared. Every result set then returns nothing and the
    -- caller maps the empty first one to 404, exactly as it does for an unknown
    -- id. A blocked event and a non-existent one are deliberately the same
    -- answer: distinguishing them would confirm the other party acted.
    IF @ViewerId IS NOT NULL AND EXISTS (
        SELECT 1
        FROM [Event].[Events] e
        INNER JOIN [Block].[BlockedParticipants] bp
            ON (bp.[BlockerType] = @ViewerType AND bp.[BlockerId] = @ViewerId
                AND bp.[BlockedId] = COALESCE(e.[ProviderId], e.[PetParentId]))
            OR (bp.[BlockedType] = @ViewerType AND bp.[BlockedId] = @ViewerId
                AND bp.[BlockerId] = COALESCE(e.[ProviderId], e.[PetParentId]))
        WHERE e.[EventId] = @EventId)
    BEGIN
        SET @EventId = NULL;
    END

    -- Result set 1: event row (zero or one). One of ProviderId / PetParentId is NULL.
    -- OrganizerName / OrganizerImageUrl are joined from whichever organiser
    -- created the event (image is null for provider organisers).
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
           -- Total tickets booked (non-cancelled) — the "total bookings done".
           (SELECT ISNULL(SUM(eb.[TicketCount]), 0)
            FROM [Event].[EventBookings] eb
            WHERE eb.[EventId] = e.[EventId]
              AND eb.[Status] = N'Confirmed') AS [TotalBookings],
           e.[EventLink]
    FROM [Event].[Events] e
    LEFT JOIN [Provider].[Providers] org_pr ON org_pr.[ProviderId] = e.[ProviderId]
    LEFT JOIN [Parent].[PetParents]  org_pp ON org_pp.[PetParentId] = e.[PetParentId]
    WHERE e.[EventId] = @EventId;

    -- Result set 2: amenities
    SELECT [Amenity]
    FROM [Event].[EventAmenities]
    WHERE [EventId] = @EventId
    ORDER BY [Amenity];

    -- Result set 3: payment options (payout methods Cash / Digital).
    SELECT [PayoutMethod]
    FROM [Event].[EventPayoutMethods]
    WHERE [EventId] = @EventId
    ORDER BY [PayoutMethod];

    -- Result set 4: attendees — names only (+ ticket number), non-cancelled
    -- bookings. Booker contact / payment stay on the organiser-only dashboard.
    SELECT t.[AttendeeName],
           t.[TicketNumber]
    FROM [Event].[EventBookingTickets] t
    INNER JOIN [Event].[EventBookings] b ON b.[BookingId] = t.[BookingId]
    WHERE t.[EventId] = @EventId
      AND b.[Status] = N'Confirmed'
    ORDER BY b.[CreatedAtUtc] ASC, t.[TicketNumber] ASC;
END;
