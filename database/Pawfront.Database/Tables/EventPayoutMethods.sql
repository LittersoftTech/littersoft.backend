CREATE TABLE [Event].[EventPayoutMethods]
(
    [EventId] UNIQUEIDENTIFIER NOT NULL,
    [PayoutMethod] NVARCHAR(32) NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_EventPayoutMethods_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_EventPayoutMethods] PRIMARY KEY CLUSTERED ([EventId] ASC, [PayoutMethod] ASC),
    CONSTRAINT [FK_EventPayoutMethods_Events_EventId]
        FOREIGN KEY ([EventId]) REFERENCES [Event].[Events] ([EventId]) ON DELETE CASCADE,
    CONSTRAINT [CK_EventPayoutMethods_PayoutMethod]
        CHECK ([PayoutMethod] IN (N'Cash', N'Digital'))
);
