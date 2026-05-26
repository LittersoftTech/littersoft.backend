-- Atomic single-column increment for the three event-level counters.
-- Called by the public increment endpoints (any signed-in user). Throws
-- 51096 if the event id is unknown so the mobile client gets a clear
-- signal rather than silently incrementing nothing.
CREATE OR ALTER PROCEDURE [Event].[IncrementEventCounter]
    @EventId UNIQUEIDENTIFIER,
    @CounterType NVARCHAR(16)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @CounterType NOT IN (N'View', N'Share', N'Inquiry')
    BEGIN
        THROW 51097, 'CounterType must be View, Share, or Inquiry.', 1;
    END

    DECLARE @RowsAffected INT;

    IF @CounterType = N'View'
    BEGIN
        UPDATE [Event].[Events]
        SET [ViewCount] = [ViewCount] + 1
        WHERE [EventId] = @EventId;
        SET @RowsAffected = @@ROWCOUNT;
    END
    ELSE IF @CounterType = N'Share'
    BEGIN
        UPDATE [Event].[Events]
        SET [ShareCount] = [ShareCount] + 1
        WHERE [EventId] = @EventId;
        SET @RowsAffected = @@ROWCOUNT;
    END
    ELSE
    BEGIN
        UPDATE [Event].[Events]
        SET [InquiryCount] = [InquiryCount] + 1
        WHERE [EventId] = @EventId;
        SET @RowsAffected = @@ROWCOUNT;
    END

    IF @RowsAffected = 0
    BEGIN
        THROW 51096, 'Event was not found.', 1;
    END

    -- Return the new value so the client can render the updated count
    -- without a follow-up read.
    SELECT [ViewCount], [ShareCount], [InquiryCount]
    FROM [Event].[Events]
    WHERE [EventId] = @EventId;
END;
