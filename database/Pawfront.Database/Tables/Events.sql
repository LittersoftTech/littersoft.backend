CREATE TABLE [Event].[Events]
(
    [EventId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_Events_EventId] DEFAULT NEWSEQUENTIALID(),
    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
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
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Events_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Events_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_Events] PRIMARY KEY CLUSTERED ([EventId] ASC),
    CONSTRAINT [FK_Events_Providers_ProviderId]
        FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
    CONSTRAINT [CK_Events_EventCategory] CHECK ([EventCategory] IN (
        N'AdoptionAndRescue', N'PetTraining', N'Charity', N'Volunteering',
        N'HealthAndWellness', N'SocialAndCultural', N'OutdoorActivities', N'ParentEducation')),
    CONSTRAINT [CK_Events_EventType] CHECK ([EventType] IN (N'Physical', N'Online')),
    CONSTRAINT [CK_Events_DateRange] CHECK ([StartDate] <= [EndDate])
);

GO

CREATE INDEX [IX_Events_ProviderId_StartDate]
    ON [Event].[Events] ([ProviderId], [StartDate] DESC);

GO

CREATE INDEX [IX_Events_Category_StartDate]
    ON [Event].[Events] ([EventCategory], [StartDate] DESC)
    INCLUDE ([ProviderId], [Title], [EventType]);
