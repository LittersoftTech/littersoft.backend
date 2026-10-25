CREATE OR ALTER PROCEDURE [Booking].[GetAccountReliability]
    @AccountId UNIQUEIDENTIFIER,
    @AccountType NVARCHAR(16)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    IF @AccountType NOT IN (N'PetParent', N'Provider')
        THROW 51468, 'Unknown reliability account type.', 1;

    SELECT @Now AS CalculatedAtUtc,
        CAST(CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS BIT) AS HasPublishedEvents,
        COALESCE(SUM(CASE WHEN e.EventStatus = N'Active'
            AND [Event].[EventStartUtc](e.EndDate, e.EndTime) BETWEEN DATEADD(month,-12,@Now) AND @Now THEN 1 ELSE 0 END),0) AS EventsRun,
        COALESCE(SUM(CASE WHEN e.EventStatus = N'Cancelled' AND e.CancelledWithBookings = 1
            AND e.CancelledAtUtc BETWEEN DATEADD(month,-12,@Now) AND @Now THEN 1 ELSE 0 END),0) AS EventsCancelled
    FROM [Event].[Events] e
    WHERE (@AccountType = N'PetParent' AND e.PetParentId = @AccountId)
       OR (@AccountType = N'Provider' AND e.ProviderId = @AccountId);

    EXEC [Event].[GetReliability] @AccountId;

    SELECT BookingType, BookingId, Outcome, OccurredAtUtc
    FROM [Booking].[ServiceReliabilityMarks]
    WHERE @AccountType = N'PetParent' AND PetParentId = @AccountId
      AND OccurredAtUtc BETWEEN DATEADD(day,-180,@Now) AND @Now;
END;
