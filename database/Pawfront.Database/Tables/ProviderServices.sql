CREATE TABLE [Provider].[ProviderServices]
(
    [ServiceId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_ProviderServices_ServiceId] DEFAULT NEWSEQUENTIALID(),
    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
    [ServiceCategory] NVARCHAR(64) NOT NULL,
    [SubCategory] NVARCHAR(64) NOT NULL,
    -- The specific service the provider offers within the category:
    --   PetSitter   → 'DayCare' or 'NightStay'
    --   PetGroomer  → 'GroomingSession'
    --   PetTrainer  → 'TrainingSession'
    --   Vet         → 'VetAppointment'
    [ServiceType] NVARCHAR(64) NOT NULL,
    -- A service is soft-deactivated (not deleted) when the provider removes the
    -- matching sub-offering on a later save, so historical closures/bookings
    -- pointing at this ServiceId stay readable.
    [IsActive] BIT NOT NULL
        CONSTRAINT [DF_ProviderServices_IsActive] DEFAULT 1,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ProviderServices_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ProviderServices_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_ProviderServices] PRIMARY KEY CLUSTERED ([ServiceId] ASC),
    CONSTRAINT [FK_ProviderServices_Providers_ProviderId]
        FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]) ON DELETE CASCADE,
    -- One row per (provider, service-type) — a provider has at most one DayCare,
    -- one NightStay, etc.
    CONSTRAINT [UQ_ProviderServices_Provider_ServiceType] UNIQUE ([ProviderId], [ServiceType]),
    CONSTRAINT [CK_ProviderServices_ServiceCategory]
        CHECK ([ServiceCategory] IN (N'PetSitter', N'PetGroomer', N'PetTrainer', N'Vet')),
    CONSTRAINT [CK_ProviderServices_ServiceType]
        CHECK ([ServiceType] IN (N'DayCare', N'NightStay', N'GroomingSession', N'TrainingSession', N'VetAppointment')),
    -- Enforce that ServiceType is compatible with ServiceCategory.
    CONSTRAINT [CK_ProviderServices_ServiceType_MatchesCategory] CHECK (
        ([ServiceCategory] = N'PetSitter'  AND [ServiceType] IN (N'DayCare', N'NightStay'))
        OR ([ServiceCategory] = N'PetGroomer' AND [ServiceType] = N'GroomingSession')
        OR ([ServiceCategory] = N'PetTrainer' AND [ServiceType] = N'TrainingSession')
        OR ([ServiceCategory] = N'Vet'        AND [ServiceType] = N'VetAppointment')
    )
);

GO

CREATE INDEX [IX_ProviderServices_Provider_Active]
    ON [Provider].[ProviderServices] ([ProviderId], [IsActive])
    INCLUDE ([ServiceType], [ServiceCategory], [SubCategory]);
