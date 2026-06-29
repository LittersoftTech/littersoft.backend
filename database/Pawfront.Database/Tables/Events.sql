CREATE TABLE [Event].[Events]
(
    [EventId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_Events_EventId] DEFAULT NEWSEQUENTIALID(),
    -- Either ProviderId or PetParentId is set on each row; the CHECK below
    -- enforces exactly one. Provider-organised and parent-organised events
    -- coexist in the same table so the discovery list, get-by-id, booking,
    -- and counter flows don't need to know which kind of organiser this is.
    [ProviderId] UNIQUEIDENTIFIER NULL,
    [PetParentId] UNIQUEIDENTIFIER NULL,
    [EventCategory] NVARCHAR(64) NOT NULL,
    [IsChildFriendly] BIT NOT NULL,
    [Title] NVARCHAR(200) NOT NULL,
    [Description] NVARCHAR(MAX) NOT NULL,
    [BannerImageUrl] NVARCHAR(1000) NULL,
    [EventType] NVARCHAR(32) NOT NULL,
    [StartDate] DATE NOT NULL,
    [EndDate] DATE NOT NULL,
    [StartTime] TIME(0) NOT NULL,
    [EndTime] TIME(0) NOT NULL,
    -- Ticketing. Stored on the main event row (not the Cosmos physical
    -- extension) so it's returned for every event type — online events have
    -- no Cosmos document but can still be paid. [Price] is NULL for free
    -- events; the application normalises it to NULL whenever IsPaid = 0.
    [IsPaid] BIT NOT NULL
        CONSTRAINT [DF_Events_IsPaid] DEFAULT 0,
    [Price] DECIMAL(18, 2) NULL,
    -- Refund policy the creator advertises. Optional (NULL when unset) — it
    -- doesn't apply to free events, so it's never required. When set it is one
    -- of FullRefundUpTo4Hours / FullRefundUpTo2Hours / NoRefund (CHECK below).
    [CancellationPolicy] NVARCHAR(32) NULL,
    -- Joining link for ONLINE events (e.g. the meeting URL). NULL for physical
    -- events, which carry a venue location in the Cosmos extension doc instead.
    [EventLink] NVARCHAR(1000) NULL,
    -- Organiser-dashboard counters. Bumped by [Event].[IncrementEventCounter]
    -- from the public increment endpoints. Read in [Event].[GetEventMetrics].
    [ViewCount] INT NOT NULL
        CONSTRAINT [DF_Events_ViewCount] DEFAULT 0,
    [ShareCount] INT NOT NULL
        CONSTRAINT [DF_Events_ShareCount] DEFAULT 0,
    [InquiryCount] INT NOT NULL
        CONSTRAINT [DF_Events_InquiryCount] DEFAULT 0,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Events_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Events_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_Events] PRIMARY KEY CLUSTERED ([EventId] ASC),
    CONSTRAINT [FK_Events_Providers_ProviderId]
        FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
    CONSTRAINT [FK_Events_PetParents_PetParentId]
        FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId]),
    CONSTRAINT [CK_Events_OrganiserExactlyOne] CHECK (
        ([ProviderId] IS NOT NULL AND [PetParentId] IS NULL)
     OR ([ProviderId] IS NULL AND [PetParentId] IS NOT NULL)
    ),
    CONSTRAINT [CK_Events_EventCategory] CHECK ([EventCategory] IN (
        N'AdoptionAndRescue', N'PetTraining', N'Charity', N'Volunteering',
        N'HealthAndWellness', N'SocialAndCultural', N'OutdoorActivities', N'ParentEducation')),
    CONSTRAINT [CK_Events_EventType] CHECK ([EventType] IN (N'Physical', N'Online')),
    CONSTRAINT [CK_Events_DateRange] CHECK ([StartDate] <= [EndDate]),
    -- A paid event must carry a non-negative price; a free event has none.
    CONSTRAINT [CK_Events_Ticketing] CHECK (
        ([IsPaid] = 0 AND [Price] IS NULL)
     OR ([IsPaid] = 1 AND [Price] IS NOT NULL AND [Price] >= 0)
    ),
    CONSTRAINT [CK_Events_CancellationPolicy] CHECK ([CancellationPolicy] IN (
        N'FullRefundUpTo4Hours', N'FullRefundUpTo2Hours', N'NoRefund'))
);

GO

CREATE INDEX [IX_Events_ProviderId_StartDate]
    ON [Event].[Events] ([ProviderId], [StartDate] DESC);

GO

CREATE INDEX [IX_Events_PetParentId_StartDate]
    ON [Event].[Events] ([PetParentId], [StartDate] DESC);

GO

CREATE INDEX [IX_Events_Category_StartDate]
    ON [Event].[Events] ([EventCategory], [StartDate] DESC)
    INCLUDE ([ProviderId], [PetParentId], [Title], [EventType]);
