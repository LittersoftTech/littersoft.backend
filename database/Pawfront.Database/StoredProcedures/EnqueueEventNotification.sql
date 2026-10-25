-- Host transition adapter, also used by account deletion. No ordinary booking
-- or cancellation notices in Events V3: only first booking, unlock and sold out.
CREATE OR ALTER PROCEDURE [Notification].[EnqueueEventNotification]
    @EventId UNIQUEIDENTIFIER, @EventBookingId UNIQUEIDENTIFIER=NULL,
    @NotificationType NVARCHAR(64), @BookerName NVARCHAR(200)=NULL,
    @TicketCount INT=NULL, @DedupeSuffix NVARCHAR(64)=NULL
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Id UNIQUEIDENTIFIER,@Audience NVARCHAR(16),@Start DATETIME2(7),@Status NVARCHAR(16);
    SELECT @Id=COALESCE(PetParentId,ProviderId),
        @Audience=CASE WHEN PetParentId IS NULL THEN N'Provider' ELSE N'PetParent' END,
        @Start=[Event].[EventStartUtc](StartDate,StartTime),@Status=EventStatus
    FROM [Event].[Events] WHERE EventId=@EventId;
    IF @Id IS NULL OR @Status<>N'Active' RETURN;
    DECLARE @Reserved INT=(SELECT ISNULL(SUM(TicketCount),0) FROM [Event].[EventBookings]
        WHERE EventId=@EventId AND Status IN (N'Confirmed',N'Attended'));
    DECLARE @Type NVARCHAR(64)=@NotificationType,@Key NVARCHAR(200),@Data NVARCHAR(MAX);
    IF @Type=N'EVENT_TICKET_CANCELLED'
    BEGIN
        IF @Reserved<>0 OR @Start<=SYSUTCDATETIME() RETURN;
        SET @Type=N'EVENT_EDITABLE_AGAIN';
    END
    ELSE IF @Type=N'EVENT_TICKET_SOLD'
    BEGIN
        IF @Reserved<>@TicketCount RETURN;
        SET @Type=N'EVENT_FIRST_BOOKING';
    END
    SET @Key=CONCAT(@Type,N':',COALESCE(@EventBookingId,@EventId),N':',COALESCE(@DedupeSuffix,N''));
    SET @Data=(SELECT @BookerName AS parentName FOR JSON PATH,WITHOUT_ARRAY_WRAPPER);
    EXEC [Event].[NotifyEventAccount] @EventId,@Id,@Audience,@Type,@Key,@EventBookingId,@Data;
END;
