-- Organiser-dashboard metrics for one event. Combines:
--   * The 3 counter columns on [Event].[Events] (views/shares/inquiries)
--   * Aggregates over [Event].[EventBookings] for confirmed-attendees +
--     earnings (Status = N'Confirmed' AND PaymentStatus = N'Paid').
--
-- Verifies the event belongs to the requesting provider before returning
-- anything. Throws 51095 if the event is unknown or owned by someone else
-- (no existence-leak distinction by design).
CREATE OR ALTER PROCEDURE [Event].[GetEventMetrics]
    @ProviderId UNIQUEIDENTIFIER,
    @EventId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ViewCount INT;
    DECLARE @ShareCount INT;
    DECLARE @InquiryCount INT;
    SELECT @ViewCount = [ViewCount],
           @ShareCount = [ShareCount],
           @InquiryCount = [InquiryCount]
    FROM [Event].[Events]
    WHERE [EventId] = @EventId
      AND [ProviderId] = @ProviderId;

    IF @ViewCount IS NULL
    BEGIN
        THROW 51095, 'Event was not found.', 1;
    END

    DECLARE @ConfirmedAttendees INT;
    DECLARE @Earnings DECIMAL(18, 2);

    SELECT @ConfirmedAttendees = ISNULL(SUM([TicketCount]), 0),
           @Earnings = ISNULL(SUM([TotalAmount]), 0)
    FROM [Event].[EventBookings]
    WHERE [EventId] = @EventId
      AND [Status] = N'Confirmed'
      AND [PaymentStatus] = N'Paid';

    SELECT @ViewCount       AS [ViewCount],
           @ShareCount      AS [ShareCount],
           @InquiryCount    AS [InquiryCount],
           @ConfirmedAttendees AS [ConfirmedAttendees],
           @Earnings        AS [Earnings];
END;
