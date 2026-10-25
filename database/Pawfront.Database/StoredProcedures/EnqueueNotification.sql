-- Enqueues one push notification onto the outbox. Callable from C# (the API
-- hosts, via SqlNotificationPublisher) AND from other stored procedures — the
-- booking sweeps call it inside their own transaction so the notification is
-- committed atomically with the status flip it describes.
--
-- Copy is NOT passed in: the dispatcher renders [Title]/[Body]/[Route] from
-- @NotificationType + @DataJson via the C# NotificationTemplateCatalog, so all
-- user-facing wording lives in one place instead of being duplicated across C#
-- call sites and T-SQL sweeps.
--
-- @DedupeKey (e.g. N'BOOKING_ACCEPTED:<bookingId>') makes the call idempotent:
-- a second enqueue returns the existing row instead of creating a duplicate
-- inbox entry. Pass NULL when repeats are legitimately distinct events.
--
-- Never THROWs — a notification must never be the reason a booking transaction
-- rolls back. An unknown recipient simply produces a row the dispatcher will
-- resolve to 'NoDevice'.
CREATE OR ALTER PROCEDURE [Notification].[EnqueueNotification]
    @Audience NVARCHAR(16),
    @RecipientId UNIQUEIDENTIFIER,
    @NotificationType NVARCHAR(64),
    @EntityType NVARCHAR(32) = NULL,
    @EntityId UNIQUEIDENTIFIER = NULL,
    @DataJson NVARCHAR(MAX) = NULL,
    @ImageUrl NVARCHAR(1000) = NULL,
    @DedupeKey NVARCHAR(200) = NULL,
    -- Set by sproc-to-sproc callers (the booking transitions and the sweeps).
    -- A result set from a nested EXEC propagates all the way to the client, so
    -- without this an enqueue inside e.g. Booking.UpdateBookingStatus would append
    -- a phantom result set after the booking row and break the C# reader.
    -- Defaults to 0 so SqlNotificationPublisher, which reads the id back, is
    -- unaffected.
    @SuppressResultSet BIT = 0,
    @NotBeforeUtc DATETIME2(7) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    IF (@Audience=N'Provider' AND EXISTS(SELECT 1 FROM Provider.Providers WHERE ProviderId=@RecipientId AND IsDeleted=1))
       OR (@Audience=N'PetParent' AND EXISTS(SELECT 1 FROM Parent.PetParents WHERE PetParentId=@RecipientId AND IsDeleted=1))
    BEGIN
        IF @SuppressResultSet=0 SELECT CAST('00000000-0000-0000-0000-000000000000' AS UNIQUEIDENTIFIER) AS NotificationId, CAST(0 AS BIT) AS WasDuplicate;
        RETURN;
    END

    DECLARE @NotificationId UNIQUEIDENTIFIER;
    DECLARE @WasDuplicate BIT = 0;

    IF @DedupeKey IS NOT NULL
    BEGIN
        -- UPDLOCK + HOLDLOCK so two concurrent enqueues of the same key
        -- serialise rather than both passing the check; the filtered UNIQUE
        -- index UX_NotificationOutbox_DedupeKey is the backstop below.
        SELECT @NotificationId = [NotificationId]
        FROM [Notification].[NotificationOutbox] WITH (UPDLOCK, HOLDLOCK)
        WHERE [DedupeKey] = @DedupeKey;

        IF @NotificationId IS NOT NULL
        BEGIN
            SET @WasDuplicate = 1;
        END
    END

    IF @NotificationId IS NULL
    BEGIN
        DECLARE @Inserted TABLE ([NotificationId] UNIQUEIDENTIFIER);

        BEGIN TRY
            INSERT INTO [Notification].[NotificationOutbox]
            (
                [Audience],
                [RecipientId],
                [NotificationType],
                [EntityType],
                [EntityId],
                [DataJson],
                [ImageUrl],
                [DedupeKey],
                [NextAttemptAtUtc]
            )
            OUTPUT inserted.[NotificationId] INTO @Inserted
            VALUES
            (
                @Audience,
                @RecipientId,
                @NotificationType,
                @EntityType,
                @EntityId,
                @DataJson,
                @ImageUrl,
                @DedupeKey,
                COALESCE(@NotBeforeUtc, SYSUTCDATETIME())
            );

            SELECT @NotificationId = [NotificationId] FROM @Inserted;
        END TRY
        BEGIN CATCH
            -- 2601/2627 = the dedupe race the lock above almost always prevents.
            -- Anything else is a real error and must surface.
            IF ERROR_NUMBER() NOT IN (2601, 2627) OR @DedupeKey IS NULL
            BEGIN
                THROW;
            END

            SELECT @NotificationId = [NotificationId]
            FROM [Notification].[NotificationOutbox]
            WHERE [DedupeKey] = @DedupeKey;

            SET @WasDuplicate = 1;
        END CATCH
    END

    IF @SuppressResultSet = 0
    BEGIN
        SELECT @NotificationId AS [NotificationId], @WasDuplicate AS [WasDuplicate];
    END
END
