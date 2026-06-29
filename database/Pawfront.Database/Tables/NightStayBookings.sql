-- Multi-night boarding bookings (PetSitter NightStay service only). Distinct
-- from [Booking].[Bookings], which is single-day (one BookingDate + time
-- window). A night stay spans [CheckInDate, CheckOutDate): the checkout day is
-- NOT a stayed night, so capacity must be free on every night in that range.
-- DropOffTime / PickUpTime are snapshotted from the provider's NightStay
-- offering at booking time. App-only (parent-booked / provider-on-behalf) —
-- there is no Custom/walk-in shape here.
CREATE TABLE [Booking].[NightStayBookings]
(
    [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_NightStayBookings_Id] DEFAULT NEWSEQUENTIALID(),
    -- Short, human-friendly sequential job number (separate sequence from the
    -- single-day [Booking].[Bookings].[JobNumber]). Surfaced on the night-stay
    -- detail read as a "PF-000123" Job ID; the GUID stays the API/route identity.
    [JobNumber] INT NOT NULL IDENTITY(1, 1),
    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
    [PetParentId] UNIQUEIDENTIFIER NOT NULL,
    -- Capacity + closures are scoped by ServiceId, exactly as for day bookings.
    [ServiceId] UNIQUEIDENTIFIER NOT NULL,
    [ServiceCategory] NVARCHAR(64) NOT NULL,
    [SubCategory] NVARCHAR(64) NOT NULL,
    -- Which of the parent's pets the stay is for. Populated for parent-app
    -- bookings; NULL for provider-host bookings that don't capture it.
    [PetId] UNIQUEIDENTIFIER NULL,
    [CheckInDate] DATE NOT NULL,
    -- Checkout day; NOT a stayed night. Stayed nights = [CheckInDate, CheckOutDate).
    [CheckOutDate] DATE NOT NULL,
    -- Snapshot of the offering's drop-off / pick-up times at booking time.
    [DropOffTime] TIME(0) NOT NULL,
    [PickUpTime] TIME(0) NOT NULL,
    -- Payout (capture-only for now — mirrors [Booking].[Bookings]). [PayoutStatus]
    -- tracks where the provider's money is in the payout pipeline; [PayoutId] is
    -- the external payout reference once issued.
    [PayoutStatus] NVARCHAR(32) NOT NULL
        CONSTRAINT [DF_NightStayBookings_PayoutStatus] DEFAULT N'Pending',
    [PayoutId] NVARCHAR(64) NULL,
    -- Same expanded "job" lifecycle as [Booking].[Bookings] (accept/decline,
    -- start-with-OTP, evidence-gated complete, parent/provider modification
    -- proposals). Capacity-freeing statuses are the two cancelled ones PLUS
    -- PROVIDER_DECLINED; every other status still holds the stay's per-night
    -- capacity. APPROVAL_NEEDED is deprecated but kept allowed for legacy rows.
    [Status] NVARCHAR(48) NOT NULL
        CONSTRAINT [DF_NightStayBookings_Status] DEFAULT N'CREATED',
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_NightStayBookings_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_NightStayBookings_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [CancelledAtUtc] DATETIME2(7) NULL,

    CONSTRAINT [PK_NightStayBookings] PRIMARY KEY CLUSTERED ([NightStayBookingId] ASC),
    CONSTRAINT [FK_NightStayBookings_Providers_ProviderId]
        FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
    CONSTRAINT [FK_NightStayBookings_PetParents_PetParentId]
        FOREIGN KEY ([PetParentId]) REFERENCES [Parent].[PetParents] ([PetParentId]),
    CONSTRAINT [FK_NightStayBookings_ProviderServices_ServiceId]
        FOREIGN KEY ([ServiceId]) REFERENCES [Provider].[ProviderServices] ([ServiceId]),
    CONSTRAINT [FK_NightStayBookings_Pets_PetId]
        FOREIGN KEY ([PetId]) REFERENCES [Parent].[Pets] ([PetId]),
    CONSTRAINT [CK_NightStayBookings_DateOrder] CHECK ([CheckOutDate] > [CheckInDate]),
    CONSTRAINT [CK_NightStayBookings_Status]
        CHECK ([Status] IN (N'CREATED', N'CONFIRMED', N'PROVIDER_DECLINED', N'JOB_STARTED',
                            N'COMPLETED', N'APPROVAL_NEEDED',
                            N'MODIFICATION_REQUEST_BY_PARENT', N'MODIFICATION_REQUEST_BY_PROVIDER',
                            N'PROVIDER_ACCEPTED_MODIFICATION', N'PROVIDER_DECLINED_MODIFICATION',
                            N'PARENT_ACCEPTED_MODIFICATION', N'PARENT_DECLINED_MODIFICATION',
                            N'PROVIDER_CANCELLED', N'PARENT_CANCELLED')),
    CONSTRAINT [CK_NightStayBookings_CancelledRequiresTimestamp] CHECK (
        ([Status] IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED') AND [CancelledAtUtc] IS NOT NULL)
        OR ([Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED'))
    ),
    CONSTRAINT [CK_NightStayBookings_PayoutStatus]
        CHECK ([PayoutStatus] IN (N'Pending', N'Processing', N'Paid', N'Failed'))
);

GO

CREATE UNIQUE INDEX [UX_NightStayBookings_JobNumber]
    ON [Booking].[NightStayBookings] ([JobNumber]);

GO

CREATE INDEX [IX_NightStayBookings_Service_Dates_Status]
    ON [Booking].[NightStayBookings] ([ServiceId], [CheckInDate], [CheckOutDate], [Status])
    INCLUDE ([NightStayBookingId], [PetParentId], [ProviderId]);

GO

CREATE INDEX [IX_NightStayBookings_Provider_Status]
    ON [Booking].[NightStayBookings] ([ProviderId], [Status])
    INCLUDE ([ServiceId], [CheckInDate], [CheckOutDate], [NightStayBookingId], [PetParentId]);

GO

CREATE INDEX [IX_NightStayBookings_PetParent_Status]
    ON [Booking].[NightStayBookings] ([PetParentId], [Status])
    INCLUDE ([ServiceId], [CheckInDate], [CheckOutDate], [NightStayBookingId], [ProviderId]);
