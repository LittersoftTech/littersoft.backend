CREATE TABLE [Provider].[ProviderServiceRegistrations]
(
    [ProviderServiceRegistrationId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_ProviderServiceRegistrations_Id] DEFAULT NEWSEQUENTIALID(),
    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
    [ServiceCategory] NVARCHAR(64) NOT NULL,
    [SubCategory] NVARCHAR(64) NOT NULL,
    [Latitude] DECIMAL(9, 6) NOT NULL,
    [Longitude] DECIMAL(9, 6) NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ProviderServiceRegistrations_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_ProviderServiceRegistrations_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_ProviderServiceRegistrations] PRIMARY KEY CLUSTERED ([ProviderServiceRegistrationId] ASC),
    CONSTRAINT [UQ_ProviderServiceRegistrations_ProviderCategory]
        UNIQUE ([ProviderId], [ServiceCategory]),
    CONSTRAINT [FK_ProviderServiceRegistrations_Providers_ProviderId]
        FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
    CONSTRAINT [CK_ProviderServiceRegistrations_ServiceCategory]
        CHECK ([ServiceCategory] IN (N'PetSitter', N'PetGroomer', N'PetTrainer', N'PetAdoptionAndSale', N'Vet')),
    CONSTRAINT [CK_ProviderServiceRegistrations_Latitude]
        CHECK ([Latitude] BETWEEN -90 AND 90),
    CONSTRAINT [CK_ProviderServiceRegistrations_Longitude]
        CHECK ([Longitude] BETWEEN -180 AND 180)
);

GO

CREATE INDEX [IX_ProviderServiceRegistrations_Category_SubCategory]
    ON [Provider].[ProviderServiceRegistrations] ([ServiceCategory], [SubCategory])
    INCLUDE ([ProviderId], [Latitude], [Longitude]);

GO

CREATE INDEX [IX_ProviderServiceRegistrations_Location]
    ON [Provider].[ProviderServiceRegistrations] ([Latitude], [Longitude])
    INCLUDE ([ProviderId], [ServiceCategory], [SubCategory]);
