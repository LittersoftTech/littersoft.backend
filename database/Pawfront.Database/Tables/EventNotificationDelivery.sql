-- Events V3 delivery policy. Re-deployment preserves operations' timing changes.
CREATE TABLE [Notification].[EventNotificationDelivery]
(
    Code NVARCHAR(16) NOT NULL PRIMARY KEY,
    NotificationType NVARCHAR(64) NOT NULL UNIQUE,
    DelaySeconds INT NOT NULL CHECK (DelaySeconds BETWEEN 0 AND 604800),
    HoldUntilHour INT NULL CHECK (HoldUntilHour BETWEEN 0 AND 23),
    ActivatedAtUtc DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME()
);
GO
INSERT [Notification].[EventNotificationDelivery](Code,NotificationType,DelaySeconds,HoldUntilHour)
SELECT v.* FROM (VALUES
    (N'A-U1',N'EVENT_CANCELLED',0,NULL),
    (N'A-U2',N'EVENT_BOOKING_CASH_RECEIVED',0,NULL),
    (N'A-U3',N'EVENT_BOOKING_PAYMENT_PENDING',0,NULL),
    (N'A-S1',N'EVENT_REMINDER_DAY_BEFORE',60,NULL),
    (N'A-S2',N'EVENT_REMINDER_STARTING_SOON',0,NULL),
    (N'A-S4',N'EVENT_SEATS_AVAILABLE',0,NULL),
    (N'A-S5',N'EVENT_ATTENDED',86400,NULL),
    (N'A-S6',N'EVENT_MISSED',0,9),
    (N'A-S7',N'EVENT_PROFILE_FLAGGED',0,9),
    (N'A-S8',N'EVENT_PROFILE_FLAG_REMOVED',0,NULL),
    (N'A-S9',N'EVENT_ISSUE_RESOLVED',60,NULL),
    (N'H-U1',N'EVENT_FIRST_BOOKING',0,NULL),
    (N'H-U4',N'EVENT_EDITABLE_AGAIN',0,NULL),
    (N'H-S1',N'EVENT_SOLD_OUT',0,NULL),
    (N'H-S2',N'EVENT_HOST_REMINDER_DAY_BEFORE',0,NULL),
    (N'H-S3',N'EVENT_HOST_REMINDER_STARTING_SOON',0,NULL),
    (N'H-S4',N'EVENT_LIVE',0,NULL),
    (N'H-S4-Free',N'EVENT_LIVE_FREE',0,NULL),
    (N'H-S5',N'EVENT_RECORD_REMAINING',0,NULL),
    (N'H-S6',N'EVENT_HOSTED',0,9),
    (N'H-S6-Free',N'EVENT_HOSTED_FREE',0,9)
) v(Code,NotificationType,DelaySeconds,HoldUntilHour)
WHERE NOT EXISTS (SELECT 1 FROM [Notification].[EventNotificationDelivery] d WHERE d.Code=v.Code);
