-- Time-driven Events V3 notifications. All event mutations and this sweep use
-- the same transaction lock; counts, state transitions and outbox entries agree.
CREATE OR ALTER PROCEDURE [Event].[SendEventNotifications]
    @NowUtc DATETIME2(7)=NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    SET @NowUtc=COALESCE(@NowUtc,SYSUTCDATETIME());
    BEGIN TRANSACTION;
    DECLARE @LockResult INT;
    EXEC @LockResult=sys.sp_getapplock @Resource=N'EventLifecycle',@LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
    IF @LockResult<0 THROW 51468,'Event operation is busy. Retry the request.',1;

    DECLARE @Due TABLE(EventId UNIQUEIDENTIFIER,AccountId UNIQUEIDENTIFIER,Audience NVARCHAR(16),
        NotificationType NVARCHAR(64),BookingId UNIQUEIDENTIFIER,DueAtUtc DATETIME2(7),ExtraData NVARCHAR(MAX),Suffix NVARCHAR(64));
    DECLARE @Events TABLE(EventId UNIQUEIDENTIFIER PRIMARY KEY,HostId UNIQUEIDENTIFIER,Audience NVARCHAR(16),
        IsPaid BIT,StartUtc DATETIME2(7),EndUtc DATETIME2(7),CloseUtc DATETIME2(7),CreatedAtUtc DATETIME2(7),
        Booked INT,Recorded INT,Pending INT,TotalAmount DECIMAL(18,2));
    INSERT @Events
    SELECT e.EventId,COALESCE(e.PetParentId,e.ProviderId),
        CASE WHEN e.PetParentId IS NULL THEN N'Provider' ELSE N'PetParent' END,e.IsPaid,
        [Event].[EventStartUtc](e.StartDate,e.StartTime),[Event].[EventStartUtc](e.EndDate,e.EndTime),
        [Event].[EventStartUtc](DATEADD(day,1,e.EndDate),'00:00'),e.CreatedAtUtc,
        ISNULL(a.Booked,0),ISNULL(a.Recorded,0),ISNULL(a.Pending,0),ISNULL(a.TotalAmount,0)
    FROM [Event].[Events] e OUTER APPLY (
        SELECT COUNT(*) AS Booked,
            SUM(CASE WHEN t.PaymentStatus=N'CashReceived' THEN 1 ELSE 0 END) AS Recorded,
            SUM(CASE WHEN t.PaymentStatus=N'Pending' THEN 1 ELSE 0 END) AS Pending,
            SUM(CASE WHEN t.PaymentStatus=N'CashReceived' THEN b.TotalAmount/b.TicketCount ELSE 0 END) AS TotalAmount
        FROM [Event].[EventBookings] b JOIN [Event].[EventBookingTickets] t ON t.BookingId=b.BookingId
        WHERE b.EventId=e.EventId AND b.Status IN (N'Confirmed',N'Attended',N'NoShow')
    ) a WHERE e.EventStatus=N'Active'
        AND [Event].[EventStartUtc](e.StartDate,e.StartTime)<=DATEADD(hour,24,@NowUtc)
        AND ([Event].[EventStartUtc](DATEADD(day,1,e.EndDate),'00:00') >=
            (SELECT MIN(ActivatedAtUtc) FROM [Notification].[EventNotificationDelivery])
            OR e.CreatedAtUtc >= (SELECT MIN(ActivatedAtUtc) FROM [Notification].[EventNotificationDelivery]));

    -- A-S1/A-S2. Bookings created after a reminder's trigger do not receive it.
    INSERT @Due
    SELECT e.EventId,b.BookerAccountId,b.BookerType,v.NotificationType,b.BookingId,v.DueAtUtc,NULL,NULL
    FROM @Events e JOIN [Event].[EventBookings] b ON b.EventId=e.EventId
    CROSS APPLY (VALUES
        (N'EVENT_REMINDER_DAY_BEFORE',DATEADD(hour,-24,e.StartUtc),DATEADD(hour,-1,e.StartUtc)),
        (N'EVENT_REMINDER_STARTING_SOON',DATEADD(hour,-1,e.StartUtc),e.StartUtc)
    ) v(NotificationType,DueAtUtc,UntilUtc)
    JOIN [Notification].[EventNotificationDelivery] d ON d.NotificationType=v.NotificationType
    WHERE b.Status IN (N'Confirmed',N'Attended') AND b.BookerAccountId IS NOT NULL
        AND b.CreatedAtUtc<=v.DueAtUtc AND v.DueAtUtc<=@NowUtc AND @NowUtc<v.UntilUtc
        AND v.DueAtUtc>=d.ActivatedAtUtc
        AND NOT EXISTS (SELECT 1 FROM [Notification].[NotificationOutbox] n
            WHERE n.DedupeKey=CONCAT(v.NotificationType,N':',b.BookingId,N':',b.BookerAccountId,N':'));

    -- H-S2/H-S3/H-S4/H-S4-Free/H-S5/H-S6/H-S6-Free.
    INSERT @Due
    SELECT e.EventId,e.HostId,e.Audience,v.NotificationType,NULL,v.DueAtUtc,
        (SELECT CONVERT(NVARCHAR(16),v.Attendees) AS attendeeCount,
                FORMAT(e.TotalAmount,'0.##','en-US') AS totalAmount FOR JSON PATH,WITHOUT_ARRAY_WRAPPER),NULL
    FROM @Events e CROSS APPLY (VALUES
        (N'EVENT_HOST_REMINDER_DAY_BEFORE',DATEADD(hour,-24,e.StartUtc),DATEADD(hour,-1,e.StartUtc),e.Booked,CASE WHEN e.Booked>0 THEN 1 ELSE 0 END),
        (N'EVENT_HOST_REMINDER_STARTING_SOON',DATEADD(hour,-1,e.StartUtc),e.StartUtc,e.Booked,CASE WHEN e.Booked>0 THEN 1 ELSE 0 END),
        (N'EVENT_LIVE',e.StartUtc,e.EndUtc,e.Booked,CASE WHEN e.IsPaid=1 THEN 1 ELSE 0 END),
        (N'EVENT_LIVE_FREE',e.StartUtc,e.EndUtc,e.Booked,CASE WHEN e.IsPaid=0 THEN 1 ELSE 0 END),
        (N'EVENT_RECORD_REMAINING',e.EndUtc,e.CloseUtc,e.Pending,CASE WHEN e.IsPaid=1 AND e.Pending>0 THEN 1 ELSE 0 END),
        (N'EVENT_HOSTED',e.CloseUtc,CONVERT(DATETIME2(7),'9999-12-31'),e.Recorded,CASE WHEN e.IsPaid=1 AND e.Recorded>0 THEN 1 ELSE 0 END),
        (N'EVENT_HOSTED_FREE',e.CloseUtc,CONVERT(DATETIME2(7),'9999-12-31'),e.Booked,CASE WHEN e.IsPaid=0 AND e.Booked>0 THEN 1 ELSE 0 END)
    ) v(NotificationType,DueAtUtc,UntilUtc,Attendees,SendIt)
    JOIN [Notification].[EventNotificationDelivery] d ON d.NotificationType=v.NotificationType
    WHERE v.SendIt=1 AND v.DueAtUtc<=@NowUtc AND @NowUtc<v.UntilUtc
        AND (v.NotificationType<>N'EVENT_HOST_REMINDER_DAY_BEFORE' OR
            (e.CreatedAtUtc<=v.DueAtUtc AND EXISTS (SELECT 1 FROM [Event].[EventBookings] b
                WHERE b.EventId=e.EventId AND b.Status IN (N'Confirmed',N'Attended') AND b.CreatedAtUtc<=v.DueAtUtc)))
        AND (v.DueAtUtc>=d.ActivatedAtUtc OR e.CreatedAtUtc>=d.ActivatedAtUtc)
        AND NOT EXISTS (SELECT 1 FROM [Notification].[NotificationOutbox] n
            WHERE n.DedupeKey=CONCAT(v.NotificationType,N':',e.EventId,N':',e.HostId,N':'));

    -- A-S8: expire flags atomically with their one removal notice. Keep the old
    -- expiry in the key so a later flag can generate another removal notice.
    INSERT @Due
    SELECT b.EventId,r.AccountId,b.BookerType,N'EVENT_PROFILE_FLAG_REMOVED',NULL,r.RedFlagExpiresAt,NULL,
        CONVERT(NVARCHAR(33),r.RedFlagExpiresAt,126)
    FROM [Event].[Reliability] r CROSS APPLY (
        SELECT TOP (1) EventId,BookerType FROM [Event].[EventBookings]
        WHERE BookerAccountId=r.AccountId ORDER BY CreatedAtUtc DESC,BookingId
    ) b WHERE r.RedFlagExpiresAt<=@NowUtc;
    UPDATE [Event].[Reliability] SET RedFlagExpiresAt=NULL,ConsecutiveNegativeMarks=0,
        ConsecutivePaidEventsAttended=0,LastNegativeAtUtc=NULL WHERE RedFlagExpiresAt<=@NowUtc;

    DECLARE @Event UNIQUEIDENTIFIER,@Id UNIQUEIDENTIFIER,@Audience NVARCHAR(16),@Type NVARCHAR(64),
        @Booking UNIQUEIDENTIFIER,@At DATETIME2(7),@Data NVARCHAR(MAX),@Suffix NVARCHAR(64),@Key NVARCHAR(200);
    DECLARE notifications CURSOR LOCAL FAST_FORWARD FOR
        SELECT EventId,AccountId,Audience,NotificationType,BookingId,DueAtUtc,ExtraData,Suffix FROM @Due;
    OPEN notifications; FETCH NEXT FROM notifications INTO @Event,@Id,@Audience,@Type,@Booking,@At,@Data,@Suffix;
    WHILE @@FETCH_STATUS=0
    BEGIN
        SET @Key=CONCAT(@Type,N':',COALESCE(@Booking,@Event),N':',@Id,N':',COALESCE(@Suffix,N''));
        EXEC [Event].[NotifyEventAccount] @Event,@Id,@Audience,@Type,@Key,@Booking,@Data,@At;
        FETCH NEXT FROM notifications INTO @Event,@Id,@Audience,@Type,@Booking,@At,@Data,@Suffix;
    END
    CLOSE notifications; DEALLOCATE notifications;
    COMMIT TRANSACTION;
END;
