-- Shared payload and delivery policy for all Events V3 cards.
CREATE OR ALTER PROCEDURE [Event].[NotifyEventAccount]
    @EventId UNIQUEIDENTIFIER, @AccountId UNIQUEIDENTIFIER, @Audience NVARCHAR(16),
    @NotificationType NVARCHAR(64), @DedupeKey NVARCHAR(200), @BookingId UNIQUEIDENTIFIER=NULL,
    @ExtraData NVARCHAR(MAX)=NULL, @DueAtUtc DATETIME2(7)=NULL
AS
BEGIN
    SET NOCOUNT ON;
    IF @AccountId IS NULL RETURN;
    DECLARE @Code NVARCHAR(16), @Delay INT, @Hold INT, @Now DATETIME2(7)=SYSUTCDATETIME();
    SELECT @Code=Code,@Delay=DelaySeconds,@Hold=HoldUntilHour
    FROM [Notification].[EventNotificationDelivery] WHERE NotificationType=@NotificationType;
    IF @Code IS NULL RETURN;

    DECLARE @Data NVARCHAR(MAX),@Close DATETIME2(7);
    SELECT @Data=(SELECT N'EVENT' AS category, @Code AS notificationCode,
        e.Title AS eventTitle, CONVERT(NVARCHAR(36),e.EventId) AS eventId,
        CONVERT(NVARCHAR(36),@BookingId) AS eventBookingId,
        CONVERT(NVARCHAR(36),@BookingId) AS bookingId,
        CONVERT(NVARCHAR(36),e.PetParentId) AS parentId,
        CONVERT(NVARCHAR(36),e.ProviderId) AS providerId,
        COALESCE(NULLIF(LTRIM(RTRIM(CONCAT(p.FirstName,N' ',p.LastName))),N''),
                 NULLIF(LTRIM(RTRIM(CONCAT(pr.FirstName,N' ',pr.LastName))),N''),N'The host') AS hostName,
        CONVERT(NVARCHAR(10),e.StartDate,23) + N'T' + CONVERT(NVARCHAR(8),e.StartTime,108) AS serviceStartUtc,
        CONVERT(NVARCHAR(16),e.MaximumCapacity) AS seats
        FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),
        @Close=[Event].[EventStartUtc](DATEADD(day,1,e.EndDate),'00:00')
    FROM [Event].[Events] e
    LEFT JOIN [Parent].[PetParents] p ON p.PetParentId=e.PetParentId
    LEFT JOIN [Provider].[Providers] pr ON pr.ProviderId=e.ProviderId
    WHERE e.EventId=@EventId;
    IF @Data IS NULL RETURN;

    DECLARE @Key NVARCHAR(128),@Value NVARCHAR(MAX);
    DECLARE parameters CURSOR LOCAL FAST_FORWARD FOR SELECT [key],[value] FROM OPENJSON(COALESCE(@ExtraData,N'{}'));
    OPEN parameters; FETCH NEXT FROM parameters INTO @Key,@Value;
    WHILE @@FETCH_STATUS=0
    BEGIN
        SET @Data=JSON_MODIFY(@Data,CONCAT(N'$.',@Key),@Value);
        FETCH NEXT FROM parameters INTO @Key,@Value;
    END
    CLOSE parameters; DEALLOCATE parameters;

    DECLARE @NotBefore DATETIME2(7)=DATEADD(second,@Delay,COALESCE(@DueAtUtc,@Now));
    IF @Hold IS NOT NULL
    BEGIN
        DECLARE @Morning DATETIME2(7)=[Event].[EventStartUtc](
            CONVERT(date,@Close AT TIME ZONE 'UTC' AT TIME ZONE 'W. Europe Standard Time'),
            TIMEFROMPARTS(@Hold,0,0,0,0));
        IF @NotBefore<@Morning SET @NotBefore=@Morning;
    END
    DECLARE @EntityType NVARCHAR(32)=CASE WHEN @BookingId IS NULL THEN N'Event' ELSE N'EventBooking' END;
    DECLARE @EntityId UNIQUEIDENTIFIER=COALESCE(@BookingId,@EventId);
    EXEC [Notification].[EnqueueNotification] @Audience=@Audience,@RecipientId=@AccountId,
        @NotificationType=@NotificationType,@EntityType=@EntityType,@EntityId=@EntityId,
        @DataJson=@Data,@DedupeKey=@DedupeKey,@SuppressResultSet=1,@NotBeforeUtc=@NotBefore;
END;
