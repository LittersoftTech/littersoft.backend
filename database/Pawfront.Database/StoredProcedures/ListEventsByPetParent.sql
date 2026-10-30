CREATE OR ALTER PROCEDURE [Event].[ListEventsByPetParent]
    @PetParentId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    -- Result set 1: event rows (ProviderId is always NULL on rows returned
    -- here because the filter is on PetParentId — but included for
    -- column-shape consistency with GetEvent / ListEvents readers).
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
    WHERE e.[PetParentId] = @PetParentId AND e.RemovedByHost=0
    ORDER BY e.[StartDate] DESC, e.[StartTime] DESC;

    -- Result set 2: amenities for this parent's events (EventId + Amenity)
    SELECT a.[EventId], a.[Amenity]
    FROM [Event].[EventAmenities] a
    INNER JOIN [Event].[Events] e ON e.[EventId] = a.[EventId]
    WHERE e.[PetParentId] = @PetParentId AND e.RemovedByHost=0
    ORDER BY a.[EventId], a.[Amenity];
END;
