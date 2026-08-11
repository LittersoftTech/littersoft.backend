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
    -- When the job actually finished, if it finished EARLY. Stamped by
    -- [Booking].[CompleteBooking] and left NULL whenever the booked window ran
    -- its course (completed at/after [EndTime], or on a later date), so NULL
    -- reads as "the booking occupied everything it booked".
    --
    -- This exists ONLY to release the unused remainder back to the provider's
    -- capacity: a 6-hour day care wrapped up after 3 hours should not keep the
    -- other 3 hours blocked. Occupancy is therefore [StartTime, effective end)
    -- where the effective end is COALESCE([ActualEndTime], [EndTime]) — the
    -- expression every capacity, slot, and agenda query uses.
    --
    -- It is deliberately NOT part of what the booking COST or was AGREED to be.
    -- Pricing ([Booking].[BookingAmounts], the booking-detail read), the terms
    -- snapshots, and every wire contract keep reading [EndTime], so finishing
    -- early frees the slot without re-pricing a job the parent already agreed
    -- to. Changing that is a product decision, not a capacity one.
    [ActualEndTime] TIME(0) NULL,
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
    -- Where the service is delivered, as chosen by the parent at booking time:
    -- 'ParentLocation' (the provider comes to the parent's address) or
    -- 'ProviderLocation' (the parent goes to the provider's place). NULL for
    -- Custom walk-ins (which carry their own ServiceLocation) and legacy rows.
    -- The booking-detail read resolves the matching address live.
    [LocationType] NVARCHAR(32) NULL,
    -- Snapshot of the provider's advertised cancellation policy at booking time
    -- (minimum hours before a cancellation is allowed: 24/48/72/96, or NULL for
    -- no restriction). Locked in so a later policy change never re-rules an
    -- already-created booking. NULL is itself a valid snapshot ("no restriction").
    [CancellationPolicyHours] INT NULL,
    -- Snapshot of the SELECTED service-location address at booking time, driven by
    -- [LocationType]: the parent's profile address (ParentLocation) or the
    -- provider's business address (ProviderLocation). Frozen so a later edit to
    -- either party's address never moves an existing booking. NULL for Custom
    -- walk-ins and legacy rows — the detail read resolves the address live then.
    [SnapshotAddressLine] NVARCHAR(500) NULL,
    [SnapshotCity] NVARCHAR(200) NULL,
    [SnapshotZipCode] NVARCHAR(32) NULL,
    [SnapshotLatitude] DECIMAL(9, 6) NULL,
    [SnapshotLongitude] DECIMAL(9, 6) NULL,
    -- -----------------------------------------------------------------------
    -- Payout (capture-only for now — the actual provider-payout execution leg is
    -- not built yet). [PayoutStatus] tracks where the provider's money is in the
    -- payout pipeline; [PayoutId] is the external payout reference once issued.
    --
    -- 'NO_PAYOUT' is TERMINAL and means no money can ever move on this booking:
    -- written when the job ends as PARENT_NO_SHOW / PROVIDER_NO_SHOW / EXPIRED,
    -- which are exactly the outcomes where nobody performed and nobody owes. It
    -- exists because 'Pending' read as "the money is on its way" on a job that
    -- was never going to produce any, and both apps had to special-case it.
    [PayoutStatus] NVARCHAR(32) NOT NULL
        CONSTRAINT [DF_Bookings_PayoutStatus] DEFAULT N'Pending',
    [PayoutId] NVARCHAR(64) NULL,
    -- -----------------------------------------------------------------------
    -- Lifecycle (the "job" flow): CREATED (parent booked) -> CONFIRMED (provider
    -- accepted) | PROVIDER_DECLINED (provider rejected) -> START_JOB (provider
    -- tapped "Start Job"; a start-OTP is issued to the parent) -> IN_PROGRESS
    -- (provider entered the parent's start-OTP) -> COMPLETED (provider marked the
    -- job done; no OTP) -> PAID (the parent has paid the provider; a payment row
    -- is written to [Booking].[BookingPayments]). JOB_STARTED (the single
    -- direct-start state) and ENDING (the "End Job" end-OTP state) are retired,
    -- kept allowed for legacy rows.
    -- Either party can propose a schedule change
    -- (MODIFICATION_REQUEST_BY_PARENT / _BY_PROVIDER); the counterparty resolves
    -- it (PROVIDER/PARENT_ACCEPTED_MODIFICATION applies the new details to this
    -- same row, PROVIDER/PARENT_DECLINED_MODIFICATION keeps the old ones — both
    -- are "live" resting states the job can still be started from).
    -- PROVIDER_CANCELLED / PARENT_CANCELLED are the cancellation states.
    -- PARENT_NO_SHOW / PROVIDER_NO_SHOW record the counterparty failing to
    -- appear (reportable 30+ minutes after the scheduled start; terminal). The
    -- scheduled external job also sets them on its own when the PROVIDER'S
    -- WORKING DAY ends with the accepted job still unstarted (their closing time
    -- for that weekday, or the booking's own EndTime if that is later): from
    -- START_JOB -> PARENT_NO_SHOW (the start code was issued but never handed
    -- back), from a confirmed-equivalent state -> PROVIDER_NO_SHOW (Start was
    -- never tapped).
    -- EXPIRED = the booking sat in CREATED for 24+ hours without the provider
    -- accepting (terminal, frees capacity — the provider can no longer accept).
    -- JOB_EXPIRED = LEGACY (2026-07-29), no longer produced: the provider
    -- accepted but never started the job and the scheduled window fully
    -- elapsed. That is now a no-show (above); the value stays allowed for
    -- existing rows (terminal, frees capacity).
    --
    -- NOTE (2026-08-02): the time-driven statuses above (EXPIRED, the two
    -- no-shows, and legacy JOB_EXPIRED) are written by a SCHEDULED EXTERNAL JOB.
    -- No sproc in this database changes a booking's status on the basis of
    -- elapsed time; the time checks that remain reject a late transition without
    -- writing. A row can therefore sit in CREATED past 24 hours (or in an
    -- unstarted accepted state past its window) until that job runs — reads show
    -- the stored status, while the API still refuses the transition.
    -- OTP_MAX_ATTEMPTS_EXCEEDED = the provider entered the wrong start-OTP 6 times,
    -- cancelling the job; set by the start-with-OTP sproc (terminal, frees
    -- capacity). APPROVAL_NEEDED is deprecated (superseded by the modification
    -- flow) but kept allowed so legacy rows stay valid. Capacity-freeing
    -- statuses are the two cancelled ones PLUS PROVIDER_DECLINED, the two
    -- no-show statuses, EXPIRED, JOB_EXPIRED, and OTP_MAX_ATTEMPTS_EXCEEDED; every
    -- other status still holds the booking's capacity slot.
    -- COMPLETED / PAID hold their slot too, but only up to [ActualEndTime] when
    -- the job finished early — a partial release, not a status-level one.
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
    -- An early finish can only ever SHRINK the occupied window, never extend or
    -- invert it. Equality with [StartTime] is allowed and means the whole slot is
    -- released (the job ended before its booked start — possible, since starting
    -- a job is gated on the service DATE, not the time of day). Equality with
    -- [EndTime] is allowed but is what NULL already means.
    CONSTRAINT [CK_Bookings_ActualEndTime] CHECK (
        [ActualEndTime] IS NULL
        OR ([ActualEndTime] >= [StartTime] AND [ActualEndTime] <= [EndTime])
    ),
    CONSTRAINT [CK_Bookings_Status]
        CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED',
                            N'START_JOB', N'IN_PROGRESS', N'ENDING', N'JOB_STARTED',
                            N'COMPLETED', N'PAID', N'APPROVAL_NEEDED',
                            N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                            N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                            N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                            N'PROVIDER_CANCELLED', N'PARENT_CANCELLED',
                            N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW',
                            N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')),
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
    CONSTRAINT [CK_Bookings_LocationType]
        CHECK ([LocationType] IS NULL
            OR [LocationType] IN (N'ParentLocation', N'ProviderLocation')),
    CONSTRAINT [CK_Bookings_PricePerHour_NonNegative]
        CHECK ([PricePerHour] IS NULL OR [PricePerHour] >= 0),
    CONSTRAINT [CK_Bookings_CancellationPolicyHours]
        CHECK ([CancellationPolicyHours] IS NULL
               OR [CancellationPolicyHours] IN (24, 48, 72, 96)),
    CONSTRAINT [CK_Bookings_PayoutStatus]
        CHECK ([PayoutStatus] IN (N'Pending', N'Processing', N'Paid', N'Failed', N'NO_PAYOUT')),
    -- Discriminator shape: App rows carry PetParentId only; Custom rows carry
    -- the full custom payload and no PetParentId.
    CONSTRAINT [CK_Bookings_SourceShape] CHECK
    (
        -- Note: [PricePerHour] is now populated for App rows too — it snapshots the
        -- offering's unit rate at booking time (price-lock), so it is NOT asserted
        -- NULL here. Only the Custom-identity columns discriminate the two shapes.
        ([Source] = N'App'
            AND [PetParentId] IS NOT NULL
            AND [CustomerName] IS NULL
            AND [CustomerMobileCountryCode] IS NULL
            AND [CustomerMobile] IS NULL
            AND [AnimalType] IS NULL
            AND [PetName] IS NULL
            AND [ServiceLocation] IS NULL
            AND [CustomerLocation] IS NULL)
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

-- [ActualEndTime] is INCLUDEd because the race-safe capacity count in
-- [Booking].[CreateBooking] — the hottest query on this table, running under
-- UPDLOCK + HOLDLOCK — now reads COALESCE([ActualEndTime], [EndTime]). Without
-- it every candidate row would need a clustered-index lookup while holding
-- those locks.
CREATE INDEX [IX_Bookings_Service_Date_Status]
    ON [Booking].[Bookings] ([ServiceId], [BookingDate], [Status])
    INCLUDE ([StartTime], [EndTime], [ActualEndTime], [BookingId], [PetParentId], [ProviderId]);

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
