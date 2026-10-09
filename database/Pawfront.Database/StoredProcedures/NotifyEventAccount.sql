CREATE OR ALTER PROCEDURE [Event].[NotifyEventAccount]
    @EventId UNIQUEIDENTIFIER, @AccountId UNIQUEIDENTIFIER, @Audience NVARCHAR(16),
    @NotificationType NVARCHAR(64), @DedupeKey NVARCHAR(200), @BookingId UNIQUEIDENTIFIER=NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Title NVARCHAR(200)=(SELECT Title FROM [Event].[Events] WHERE EventId=@EventId);
    DECLARE @Data NVARCHAR(MAX)=(SELECT N'EVENT' AS category, @Title AS eventTitle,
        CONVERT(NVARCHAR(36),@EventId) AS eventId, CONVERT(NVARCHAR(36),@BookingId) AS eventBookingId
        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
    EXEC [Notification].[EnqueueNotification] @Audience=@Audience, @RecipientId=@AccountId,
        @NotificationType=@NotificationType, @EntityType=N'Event', @EntityId=@EventId,
        @DataJson=@Data, @DedupeKey=@DedupeKey, @SuppressResultSet=1;

END;
