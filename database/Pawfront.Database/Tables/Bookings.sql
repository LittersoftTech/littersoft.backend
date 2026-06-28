CREATE TABLE [Booking].[Bookings]
(
    [BookingId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_Bookings_BookingId] DEFAULT NEWSEQUENTIALID(),
    -- Short, human-friendly sequential job number. Surfaced on the booking-detail
    -- read as a "PF-000123" Job ID; the GUID stays the API/route identity.
    [JobNumber] INT NOT NULL IDENTITY(1, 1),
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
    -- Which of the parent's pets the booking is for. Populated for
    -- parent-app bookings; NULL for legacy rows and Custom walk-ins.
    [PetId] UNIQUEIDENTIFIER NULL,
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
    -- Payout (capture-only for now — the actual provider-payout execution leg is
    -- not built yet). [PayoutStatus] tracks where the provider's money is in the
    -- payout pipeline; [PayoutId] is the external payout reference once issued.
    [PayoutStatus] NVARCHAR(32) NOT NULL
        CONSTRAINT [DF_Bookings_PayoutStatus] DEFAULT N'Pending',
    [PayoutId] NVARCHAR(64) NULL,
    -- -----------------------------------------------------------------------
    -- Lifecycle (the "job" flow): CREATED (parent booked) -> CONFIRMED (provider
    -- accepted) | PROVIDER_DECLINED (provider rejected) -> JOB_STARTED (provider
    -- started, gated by the parent's start-OTP) -> COMPLETED (provider uploaded
    -- evidence). Either party can propose a schedule change
    -- (MODIFICATION_REQUEST_BY_PARENT / _BY_PROVIDER); the counterparty resolves
    -- it (PROVIDER/PARENT_ACCEPTED_MODIFICATION applies the new details to this
    -- same row, PROVIDER/PARENT_DECLINED_MODIFICATION keeps the old ones — both
    -- are "live" resting states the job can still be started from).
    -- PROVIDER_CANCELLED / PARENT_CANCELLED are the cancellation states.
    -- APPROVAL_NEEDED is deprecated (superseded by the modification flow) but
    -- kept allowed so legacy rows stay valid. Capacity-freeing statuses are the
    -- two cancelled ones PLUS PROVIDER_DECLINED; every other status still holds
    -- the booking's capacity slot.
    [Status] NVARCHAR(48) NOT NULL
        CONSTRAINT [DF_Bookings_Status] DEFAULT N'CREATED',
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
    CONSTRAINT [FK_Bookings_Pets_PetId]
        FOREIGN KEY ([PetId]) REFERENCES [Parent].[Pets] ([PetId]),
    CONSTRAINT [CK_Bookings_TimeOrder] CHECK ([StartTime] < [EndTime]),
    CONSTRAINT [CK_Bookings_Status]
        CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED', N'JOB_STARTED',
                            N'COMPLETED', N'APPROVAL_NEEDED',
                            N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                            N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                            N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                            N'PROVIDER_CANCELLED', N'PARENT_CANCELLED')),
    CONSTRAINT [CK_Bookings_CancelledRequiresTimestamp] CHECK (
        ([Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED') AND [CancelledAtUtc] IS NOT NULL)
        OR ([Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED'))
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
    CONSTRAINT [CK_Bookings_PayoutStatus]
        CHECK ([PayoutStatus] IN (N'Pending', N'Processing', N'Paid', N'Failed')),
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

GO

CREATE UNIQUE INDEX [UX_Bookings_JobNumber]
    ON [Booking].[Bookings] ([JobNumber]);
