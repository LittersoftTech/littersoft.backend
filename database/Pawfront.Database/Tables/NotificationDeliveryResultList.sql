-- Table type carrying one dispatch outcome per claimed notification, sent from
-- NotificationDispatchFunction to Notification.CompleteNotificationDelivery as a
-- SqlParameter with TypeName 'Notification.NotificationDeliveryResultList'.
--
-- It carries the rendered [Title]/[Body]/[Route] as well as the outcome because
-- the dispatcher is the only component that renders copy (from the C#
-- NotificationTemplateCatalog), and one round-trip per tick beats one per row.
--
-- NOTE: a table type cannot be ALTERed. Changing this shape means dropping and
-- recreating it, which requires dropping every sproc that references it first —
-- DeployAll.sql handles that ordering.
CREATE TYPE [Notification].[NotificationDeliveryResultList] AS TABLE
(
    [NotificationId] UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
    -- 'Sent' | 'NoDevice' | 'Failed' — 'Failed' is retried until the attempt
    -- ceiling, then made terminal by the sproc.
    [Status] NVARCHAR(16) NOT NULL,
    [Title] NVARCHAR(200) NULL,
    [Body] NVARCHAR(1000) NULL,
    [Route] NVARCHAR(200) NULL,
    [DeliveredCount] INT NOT NULL,
    [LastError] NVARCHAR(2000) NULL
);
