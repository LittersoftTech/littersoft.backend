CREATE TABLE [Provider].[ProviderWeeklyAvailability]
(
    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
    [DayOfWeek] TINYINT NOT NULL,            -- 0 = Sunday .. 6 = Saturday (matches System.DayOfWeek)
    [IsOpen] BIT NOT NULL,
    [StartTime] TIME(0) NULL,
    [EndTime] TIME(0) NULL,
    [BreakStartTime] TIME(0) NULL,
    [BreakEndTime] TIME(0) NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ProviderWeeklyAvailability_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ProviderWeeklyAvailability_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_ProviderWeeklyAvailability]
        PRIMARY KEY CLUSTERED ([ProviderId] ASC, [DayOfWeek] ASC),
    CONSTRAINT [FK_ProviderWeeklyAvailability_Providers_ProviderId]
        FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]) ON DELETE CASCADE,
    CONSTRAINT [CK_ProviderWeeklyAvailability_DayOfWeek]
        CHECK ([DayOfWeek] BETWEEN 0 AND 6),
    -- When closed: all time columns must be NULL.
    CONSTRAINT [CK_ProviderWeeklyAvailability_Closed_NullTimes] CHECK (
        [IsOpen] = 1 OR (
            [StartTime] IS NULL AND [EndTime] IS NULL
            AND [BreakStartTime] IS NULL AND [BreakEndTime] IS NULL
        )
    ),
    -- When open: start + end must both be set.
    CONSTRAINT [CK_ProviderWeeklyAvailability_Open_HasWindow] CHECK (
        [IsOpen] = 0 OR ([StartTime] IS NOT NULL AND [EndTime] IS NOT NULL)
    ),
    -- Start < End.
    CONSTRAINT [CK_ProviderWeeklyAvailability_WindowOrder] CHECK (
        [StartTime] IS NULL OR [EndTime] IS NULL OR [StartTime] < [EndTime]
    ),
    -- Break: both null or both set.
    CONSTRAINT [CK_ProviderWeeklyAvailability_Break_BothOrNeither] CHECK (
        ([BreakStartTime] IS NULL AND [BreakEndTime] IS NULL)
        OR ([BreakStartTime] IS NOT NULL AND [BreakEndTime] IS NOT NULL)
    ),
    -- Break start < break end.
    CONSTRAINT [CK_ProviderWeeklyAvailability_BreakOrder] CHECK (
        [BreakStartTime] IS NULL OR [BreakEndTime] IS NULL
        OR [BreakStartTime] < [BreakEndTime]
    ),
    -- Break must fit inside the working window.
    CONSTRAINT [CK_ProviderWeeklyAvailability_BreakInsideWindow] CHECK (
        [BreakStartTime] IS NULL OR [BreakEndTime] IS NULL
        OR ([BreakStartTime] >= [StartTime] AND [BreakEndTime] <= [EndTime])
    )
);
