-- Records the outcome of a dispatch batch and persists the copy the dispatcher
-- rendered, in one round-trip.
--
-- Per row in @Results:
--   'Sent'     -> terminal success; stamps [SentAtUtc] and [DeliveredCount].
--   'NoDevice' -> terminal success with nothing to push to (recipient has no
--                 active token). Still stamped [SentAtUtc] so it appears in the
--                 in-app inbox — the notification happened, it just had no device.
--   'Failed'   -> retried with exponential backoff (2^AttemptCount minutes,
--                 capped at 60) until [AttemptCount] reaches @MaxAttempts, then
--                 made terminal.
--
-- [Title]/[Body]/[Route] are written on every outcome including 'Failed', so a
-- notification that can never be delivered to a device is still readable in the
-- inbox.
CREATE OR ALTER PROCEDURE [Notification].[CompleteNotificationDelivery]
    @Results [Notification].[NotificationDeliveryResultList] READONLY,
    @MaxAttempts INT = 5
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @MaxAttempts IS NULL OR @MaxAttempts < 1 SET @MaxAttempts = 5;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    UPDATE o
    SET [Title] = COALESCE(r.[Title], o.[Title]),
        [Body] = COALESCE(r.[Body], o.[Body]),
        [Route] = COALESCE(r.[Route], o.[Route]),
        [DeliveredCount] = r.[DeliveredCount],
        [LastError] = r.[LastError],
        [UpdatedAtUtc] = @Now,
        [Status] =
            CASE
                WHEN r.[Status] IN (N'Sent', N'NoDevice') THEN r.[Status]
                -- Out of retries: stop rescheduling and make it terminal.
                WHEN o.[AttemptCount] >= @MaxAttempts THEN N'Failed'
                ELSE N'Pending'
            END,
        [SentAtUtc] =
            CASE
                WHEN r.[Status] IN (N'Sent', N'NoDevice') THEN COALESCE(o.[SentAtUtc], @Now)
                ELSE o.[SentAtUtc]
            END,
        [NextAttemptAtUtc] =
            CASE
                WHEN r.[Status] IN (N'Sent', N'NoDevice') THEN o.[NextAttemptAtUtc]
                WHEN o.[AttemptCount] >= @MaxAttempts THEN o.[NextAttemptAtUtc]
                -- 2, 4, 8, 16, 32 minutes — capped at 60 so a long-running
                -- outage doesn't push a notification days into the future. The
                -- exponent is clamped first: POWER(2, n) is integer arithmetic
                -- and would overflow if a caller raised @MaxAttempts.
                ELSE DATEADD(
                        MINUTE,
                        CASE
                            WHEN o.[AttemptCount] >= 6 THEN 60
                            WHEN POWER(2, o.[AttemptCount]) > 60 THEN 60
                            ELSE POWER(2, o.[AttemptCount])
                        END,
                        @Now)
            END
    FROM [Notification].[NotificationOutbox] o
    INNER JOIN @Results r ON r.[NotificationId] = o.[NotificationId];

    SELECT @@ROWCOUNT AS [UpdatedCount];
END
