-- Every job between ONE provider and ONE pet parent, newest first.
--
-- Backs the chat screen's "View Jobs": the two of them are already talking, and
-- the question the screen asks is "what work have we done together?" — so unlike
-- the two "my bookings" lists this is scoped to the PAIR, and unlike the pending
-- job lists it is not filtered by status. A cancelled or completed job is part of
-- that history and belongs on the list.
--
-- Both booking kinds, merged into one feed. [BookingType] discriminates, and the
-- other kind's columns are NULL — the same shape [Booking].[BookingAmounts] and
-- the two delete refusals use, because a person thinks "our jobs", not "our two
-- kinds of jobs".
--
-- The projection is DELIBERATELY IDENTICAL, column for column and in the same
-- order, to the pending-job result sets of [Parent].[DeletePetParent] (result set
-- 3) and [Parent].[DeletePetParentPet] (result set 2). All three are read by the
-- single C# PendingJobReader, so one job card renders everywhere it appears.
-- Change the column list in one and you must change it in all of them and in that
-- reader. The last four columns are pricing inputs the caller turns into a money
-- block, since SQL cannot reach the Cosmos offering.
--
-- Custom walk-ins can never appear: they carry no PetParentId, so the pair
-- predicate excludes them without needing to say so.
--
-- Returns ONE result set — the page, with the whole-result count appended as a
-- 17th column. COUNT(*) OVER() is evaluated before OFFSET/FETCH, so it counts the
-- pair's entire history rather than the page, which is what lets the caller
-- report hasMore and a total from one round trip. The reader takes it from the
-- first row; an empty page means a total of zero.
CREATE OR ALTER PROCEDURE [Booking].[ListBookingsForParentProvider]
    @ProviderId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @Skip INT = 0,
    @Take INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    IF @Skip IS NULL OR @Skip < 0 SET @Skip = 0;
    IF @Take IS NULL OR @Take < 1 SET @Take = 20;

    WITH [Jobs] AS
    (
        SELECT b.[BookingId] AS [BookingId],
               N'SingleDay' AS [BookingType],
               N'PF-' + FORMAT(b.[JobNumber], N'D6') AS [JobId],
               b.[ProviderId] AS [ProviderId],
               NULLIF(LTRIM(RTRIM(ISNULL(pr.[FirstName], N'') + N' ' + ISNULL(pr.[LastName], N''))), N'')
                   AS [ProviderName],
               b.[ServiceCategory] AS [ServiceCategory],
               b.[SubCategory] AS [SubCategory],
               b.[Status] AS [Status],
               b.[BookingDate] AS [ServiceDate],
               b.[StartTime] AS [StartTime],
               b.[EndTime] AS [EndTime],
               pet.[PetName] AS [PetName],
               b.[ServiceId] AS [ServiceId],
               b.[ServiceItemCode] AS [ServiceItemCode],
               CAST(NULL AS DATE) AS [CheckOutDate],
               b.[PricePerHour] AS [SnapshotUnitPrice],
               b.[CreatedAtUtc] AS [CreatedAtUtc]
        FROM [Booking].[Bookings] AS b
        LEFT JOIN [Provider].[Providers] AS pr ON pr.[ProviderId] = b.[ProviderId]
        LEFT JOIN [Parent].[Pets] AS pet ON pet.[PetId] = b.[PetId]
        WHERE b.[ProviderId] = @ProviderId
          AND b.[PetParentId] = @PetParentId

        UNION ALL

        SELECT n.[NightStayBookingId],
               N'NightStay',
               N'PF-' + FORMAT(n.[JobNumber], N'D6'),
               n.[ProviderId],
               NULLIF(LTRIM(RTRIM(ISNULL(pr.[FirstName], N'') + N' ' + ISNULL(pr.[LastName], N''))), N''),
               n.[ServiceCategory],
               n.[SubCategory],
               n.[Status],
               n.[CheckInDate],
               n.[DropOffTime],
               n.[PickUpTime],
               pet.[PetName],
               n.[ServiceId],
               NULL,
               n.[CheckOutDate],
               n.[PricePerNight],
               n.[CreatedAtUtc]
        FROM [Booking].[NightStayBookings] AS n
        LEFT JOIN [Provider].[Providers] AS pr ON pr.[ProviderId] = n.[ProviderId]
        LEFT JOIN [Parent].[Pets] AS pet ON pet.[PetId] = n.[PetId]
        WHERE n.[ProviderId] = @ProviderId
          AND n.[PetParentId] = @PetParentId
    )
    SELECT [BookingId],
           [BookingType],
           [JobId],
           [ProviderId],
           [ProviderName],
           [ServiceCategory],
           [SubCategory],
           [Status],
           [ServiceDate],
           [StartTime],
           [EndTime],
           [PetName],
           [ServiceId],
           [ServiceItemCode],
           [CheckOutDate],
           [SnapshotUnitPrice],
           COUNT(*) OVER () AS [TotalCount]
    FROM [Jobs]
    -- By the date the service happens, not the date it was booked — the list is a
    -- calendar of what these two have done together. StartTime orders a day with
    -- several jobs in it; BookingId is the tie-break that stops OFFSET paging
    -- repeating or skipping a row, the same one the earnings and review lists use.
    ORDER BY [ServiceDate] DESC, [StartTime] DESC, [BookingId] DESC
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;
