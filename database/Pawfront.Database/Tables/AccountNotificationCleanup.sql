CREATE TABLE [Notification].[AccountNotificationCleanup]
(
    [Audience] NVARCHAR(16) NOT NULL,
    [AccountId] UNIQUEIDENTIFIER NOT NULL,
    -- Retained only until the remote inbox has been removed, then cleared.
    [FirebaseUserId] NVARCHAR(128) NULL,
    [DeletedAtUtc] DATETIME2(7) NOT NULL,
    [NextAttemptAtUtc] DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME(),
    [AttemptCount] INT NOT NULL DEFAULT 0,
    [CompletedAtUtc] DATETIME2(7) NULL,
    CONSTRAINT [PK_AccountNotificationCleanup] PRIMARY KEY ([Audience], [AccountId]),
    CONSTRAINT [CK_AccountNotificationCleanup_Audience] CHECK ([Audience] IN (N'Provider',N'PetParent'))
);
