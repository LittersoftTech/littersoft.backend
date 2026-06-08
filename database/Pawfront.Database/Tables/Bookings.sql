CREATE TABLE [Booking].[Bookings]
(
    [BookingId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_Bookings_BookingId] DEFAULT NEWSEQUENTIALID(),
    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
    -- Nullable so private (off-app) custom bookings created by the provider can
    -- live alongside regular pet-parent bookings. Discriminator is [Source]:
    --   'App'    -> PetParentId NOT NULL, all custom-* columns NULL
    --   'Custom' -> PetParentId NULL,     all custom-* columns NOT NULL
    [PetParentId] UNIQUEIDENTIFIER NULL,
    -- The specific service the booking targets — closures and capacity are scoped
    -- by ServiceId, so DayCare and NightStay bookings on the same provider are
    -- counted and gated independently.
    [ServiceId] UNIQUEIDENTIFIER NOT NULL,
    [ServiceCategory] NVARCHAR(64) NOT NULL,
    [SubCategory] NVARCHAR(64) NOT NULL,
    -- Per-category menu-item selector. Required for PetGroomer bookings (the
    -- canonical service code, e.g. 'BathAndDry'); NULL for every other category
    -- where the ServiceId already fully identifies what was booked. Resolved
    -- server-side from the provider's offering, never trusted from the request.
    [ServiceItemCode] NVARCHAR(64) NULL,
    [BookingDate] DATE NOT NULL,
    [StartTime] TIME(0) NOT NULL,
    [EndTime] TIME(0) NOT NULL,
    -- 'App'    = booked via the consumer app by a registered pet parent (PetParentId set)
    -- 'Custom' = provider-added private job for an unregistered walk-in customer
    [Source] NVARCHAR(16) NOT NULL
        CONSTRAINT [DF_Bookings_Source] DEFAULT N'App',
    -- Custom-booking fields (populated only when Source = 'Custom') ---------
    [CustomerName] NVARCHAR(200) NULL,
    [CustomerMobileCountryCode] NVARCHAR(8) NULL,
    [CustomerMobile] NVARCHAR(32) NULL,
    [AnimalType] NVARCHAR(32) NULL,
    [PetName] NVARCHAR(100) NULL,
    [ServiceLocation] NVARCHAR(32) NULL,
    -- Free-text street address; required only when ServiceLocation = 'CustomerLocation'.
    [CustomerLocation] NVARCHAR(500) NULL,
    [PricePerHour] DECIMAL(10, 2) NULL,
    [JobNotes] NVARCHAR(2000) NULL,
    -- -----------------------------------------------------------------------
    [Status] NVARCHAR(32) NOT NULL
        CONSTRAINT [DF_Bookings_Status] DEFAULT N'Confirmed',
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Bookings_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Bookings_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [CancelledAtUtc] DATETIME2(7) NULL,

    CONSTRAINT [PK_Bookings] PRIMARY KEY CLUSTERED ([BookingId] ASC),
    CONSTRAINT [FK_Bookings_Providers_ProviderId]
        FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
    CONSTRAINT [FK_Bookings_PetParents_PetParentId]
        FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId]),
    CONSTRAINT [FK_Bookings_ProviderServices_ServiceId]
        FOREIGN KEY ([ServiceId]) REFERENCES [Provider].[ProviderServices] ([ServiceId]),
    CONSTRAINT [CK_Bookings_TimeOrder] CHECK ([StartTime] < [EndTime]),
    CONSTRAINT [CK_Bookings_Status]
        CHECK ([Status] IN (N'Confirmed', N'Cancelled', N'Completed', N'NoShow')),
    CONSTRAINT [CK_Bookings_CancelledRequiresTimestamp] CHECK (
        ([Status] = N'Cancelled' AND [CancelledAtUtc] IS NOT NULL)
        OR ([Status] <> N'Cancelled')
    ),
    CONSTRAINT [CK_Bookings_Source]
        CHECK ([Source] IN (N'App', N'Custom')),
    CONSTRAINT [CK_Bookings_AnimalType]
        CHECK ([AnimalType] IS NULL
            OR [AnimalType] IN (N'Dog', N'Cat', N'Hamster', N'GuineaPig')),
    CONSTRAINT [CK_Bookings_ServiceLocation]
        CHECK ([ServiceLocation] IS NULL
            OR [ServiceLocation] IN (N'MyLocation', N'CustomerLocation')),
    CONSTRAINT [CK_Bookings_PricePerHour_NonNegative]
        CHECK ([PricePerHour] IS NULL OR [PricePerHour] >= 0),
    -- Discriminator shape: App rows carry PetParentId only; Custom rows carry
    -- the full custom payload and no PetParentId.
    CONSTRAINT [CK_Bookings_SourceShape] CHECK
    (
        ([Source] = N'App'
            AND [PetParentId] IS NOT NULL
            AND [CustomerName] IS NULL
            AND [CustomerMobileCountryCode] IS NULL
            AND [CustomerMobile] IS NULL
            AND [AnimalType] IS NULL
            AND [PetName] IS NULL
            AND [ServiceLocation] IS NULL
            AND [CustomerLocation] IS NULL
            AND [PricePerHour] IS NULL)
     OR ([Source] = N'Custom'
            AND [PetParentId] IS NULL
            AND [CustomerName] IS NOT NULL
            AND [CustomerMobileCountryCode] IS NOT NULL
            AND [CustomerMobile] IS NOT NULL
            AND [AnimalType] IS NOT NULL
            AND [PetName] IS NOT NULL
            AND [ServiceLocation] IS NOT NULL
            AND [PricePerHour] IS NOT NULL)
    ),
    -- CustomerLocation is required iff ServiceLocation = 'CustomerLocation';
    -- must be NULL for 'MyLocation' rows and App rows (where ServiceLocation is NULL).
    CONSTRAINT [CK_Bookings_CustomerLocationShape] CHECK
    (
        ([ServiceLocation] = N'CustomerLocation' AND [CustomerLocation] IS NOT NULL)
     OR ([ServiceLocation] = N'MyLocation'       AND [CustomerLocation] IS NULL)
     OR ([ServiceLocation] IS NULL               AND [CustomerLocation] IS NULL)
    )
);

GO

CREATE INDEX [IX_Bookings_Service_Date_Status]
    ON [Booking].[Bookings] ([ServiceId], [BookingDate], [Status])
    INCLUDE ([StartTime], [EndTime], [BookingId], [PetParentId], [ProviderId]);

GO

CREATE INDEX [IX_Bookings_Provider_Date_Status]
    ON [Booking].[Bookings] ([ProviderId], [BookingDate], [Status])
    INCLUDE ([ServiceId], [StartTime], [EndTime], [BookingId], [PetParentId]);

GO

CREATE INDEX [IX_Bookings_PetParent_Status]
    ON [Booking].[Bookings] ([PetParentId], [Status])
    INCLUDE ([ServiceId], [BookingDate], [StartTime], [EndTime], [ProviderId]);
