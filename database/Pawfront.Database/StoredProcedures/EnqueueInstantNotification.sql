-- Enqueues a notification that the CALLER intends to send itself, right now,
-- instead of leaving it for the 1-minute NotificationDispatchFunction.
--
-- This exists for chat. Every other notification in the product is a side effect
-- of a booking or a ticket, where a minute of latency costs nothing and
-- durability is everything — so those go through [Notification].[EnqueueNotification]
-- and wait for the timer. A chat message is the opposite: the latency IS the
-- feature.
--
-- The row is inserted ALREADY CLAIMED — [Status] = 'Sending' with the lease
-- pushed forward — which is the whole trick:
--   * the timer's claim predicate skips it, so the recipient is not pushed twice;
--   * if the caller dies before reporting the outcome, the lease lapses and the
--     ordinary dispatcher picks it up on its next tick and sends it after all.
-- So the outbox stops being the delivery path here and becomes the RETRY
-- BACKSTOP, with no extra machinery and no second code path to keep in step.
--
-- The caller renders copy with the C# NotificationTemplateCatalog, sends via FCM,
-- and reports through the existing [Notification].[CompleteNotificationDelivery]
-- exactly as the dispatcher does — which is what keeps chat copy and payload
-- shape identical to everything else.
--
-- Returns TWO result sets: the notification id, then the recipient's active FCM
-- tokens (same join [Notification].[ClaimPendingNotifications] uses), so the
-- caller needs no follow-up round trip to find the devices.
--
-- Never THROWs, for the same reason [Notification].[EnqueueNotification] never
-- does: a notification must not be the reason the thing that caused it fails.
CREATE OR ALTER PROCEDURE [Notification].[EnqueueInstantNotification]
    @Audience NVARCHAR(16),
    @RecipientId UNIQUEIDENTIFIER,
    @NotificationType NVARCHAR(64),
    @EntityType NVARCHAR(32) = NULL,
    @EntityId UNIQUEIDENTIFIER = NULL,
    @DataJson NVARCHAR(MAX) = NULL,
    @ImageUrl NVARCHAR(1000) = NULL,
    @DedupeKey NVARCHAR(200) = NULL,
    -- How long the caller has to render, send and report before the timer
    -- considers the row abandoned and takes it over. Generous relative to an FCM
    -- call, because a premature takeover means a duplicate push.
    @LeaseMinutes INT = 5,
    -- Set by sproc-to-sproc callers ([Chat].[CommitMessageAppend]), for the same reason
    -- [Notification].[EnqueueNotification] has it: a nested EXEC's result set
    -- propagates to the client and would corrupt the caller's own reader. Such a
    -- caller reads the id back through @NotificationId OUTPUT instead.
    @SuppressResultSet BIT = 0,
    @NotificationId UNIQUEIDENTIFIER = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

    SET @NotificationId = NULL;

    IF @DedupeKey IS NOT NULL
    BEGIN
        SELECT @NotificationId = [NotificationId]
        FROM [Notification].[NotificationOutbox] WITH (UPDLOCK, HOLDLOCK)
        WHERE [DedupeKey] = @DedupeKey;
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
                [Status],
                [AttemptCount],
                [NextAttemptAtUtc],
                [CreatedAtUtc],
                [UpdatedAtUtc]
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
                -- Claimed on insert. AttemptCount starts at 1 because this row's
                -- first attempt is the one the caller is about to make.
                N'Sending',
                1,
                DATEADD(MINUTE, @LeaseMinutes, @Now),
                @Now,
                @Now
            );

            SELECT @NotificationId = [NotificationId] FROM @Inserted;
        END TRY
        BEGIN CATCH
            -- 2601/2627 = the dedupe race the lock above almost always prevents.
            IF ERROR_NUMBER() NOT IN (2601, 2627) OR @DedupeKey IS NULL
            BEGIN
                THROW;
            END

            SELECT @NotificationId = [NotificationId]
            FROM [Notification].[NotificationOutbox]
            WHERE [DedupeKey] = @DedupeKey;
        END CATCH
    END

    IF @SuppressResultSet = 1
    BEGIN
        RETURN;
    END

    SELECT @NotificationId AS [NotificationId];

    IF @Audience = N'Provider'
    BEGIN
        SELECT [FcmToken], [DevicePlatform]
        FROM [Provider].[ProviderDeviceTokens]
        WHERE [ProviderId] = @RecipientId
          AND [IsActive] = 1;
    END
    ELSE
    BEGIN
        SELECT [FcmToken], [DevicePlatform]
        FROM [Parent].[ParentDeviceTokens]
        WHERE [PetParentId] = @RecipientId
          AND [IsActive] = 1;
    END
END;
