CREATE TABLE [Provider].[ProviderClosures]
(
    [ClosureId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_ProviderClosures_ClosureId] DEFAULT NEWSEQUENTIALID(),
    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
    -- A closure targets ONE specific service the provider offers. To close multiple
    -- services across the same date range, the caller submits N closures in one
    -- request and the API persists N rows (see [Provider].[CreateClosures]).
    [ServiceId] UNIQUEIDENTIFIER NOT NULL,
    [StartDate] DATE NOT NULL,
    [EndDate] DATE NOT NULL,
    -- Partial-day window. Both NULL = full-day closure across the date range.
    [StartTime] TIME(0) NULL,
    [EndTime] TIME(0) NULL,
    [Reason] NVARCHAR(500) NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ProviderClosures_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_ProviderClosures] PRIMARY KEY CLUSTERED ([ClosureId] ASC),
    CONSTRAINT [FK_ProviderClosures_Providers_ProviderId]
        FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]) ON DELETE CASCADE,
    CONSTRAINT [FK_ProviderClosures_ProviderServices_ServiceId]
        FOREIGN KEY ([ServiceId]) REFERENCES [Provider].[ProviderServices] ([ServiceId]),
    CONSTRAINT [CK_ProviderClosures_DateOrder] CHECK ([EndDate] >= [StartDate]),
    -- Both time fields together or neither.
    CONSTRAINT [CK_ProviderClosures_Time_BothOrNeither] CHECK (
        ([StartTime] IS NULL AND [EndTime] IS NULL)
        OR ([StartTime] IS NOT NULL AND [EndTime] IS NOT NULL AND [StartTime] < [EndTime])
    ),
    -- Partial-day windows only make sense on a single calendar day. Multi-day partial
    -- windows would create ambiguous semantics (does "10:00-14:00" repeat each day?).
    CONSTRAINT [CK_ProviderClosures_PartialDayIsSingleDate] CHECK (
        [StartTime] IS NULL OR [StartDate] = [EndDate]
    )
);

GO

CREATE INDEX [IX_ProviderClosures_Service_Range]
    ON [Provider].[ProviderClosures] ([ServiceId], [StartDate], [EndDate])
    INCLUDE ([StartTime], [EndTime], [Reason]);

GO

CREATE INDEX [IX_ProviderClosures_Provider_Range]
    ON [Provider].[ProviderClosures] ([ProviderId], [StartDate], [EndDate])
    INCLUDE ([ServiceId], [StartTime], [EndTime], [Reason]);
