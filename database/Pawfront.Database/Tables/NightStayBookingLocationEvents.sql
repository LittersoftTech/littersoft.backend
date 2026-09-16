-- Night-stay twin of [Booking].[BookingLocationEvents] — see that file for the
-- full rationale (why this is not columns on the status-history table, why each
-- app reports only its own position, and why the coordinates are NOT NULL).
--
-- Twinned rather than discriminated by a BookingType column because that is what
-- every other lifecycle child table here does ([NightStayBookingStatusHistory],
-- [NightStayBookingEvidence], [NightStayBookingStartOtps]) — the discriminated
-- shape is reserved for the tables that deliberately carry no FK, such as
-- [Booking].[BookingPayments]. Here the FK + cascade is worth having.
--
-- The trigger vocabulary is identical to the single-day one: a stay's hand-over
-- is the same set of moments (the provider confirms arrival at drop-off, the
-- parent shows the start code, cash changes hands at the end, evidence is taken).
-- ArrivalConfirmed is gated on the CHECK-IN day rather than a service date, in
-- keeping with how the rest of the night-stay lifecycle treats [CheckInDate].
CREATE TABLE [Booking].[NightStayBookingLocationEvents]
(
    [NightStayBookingLocationEventId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_NightStayBookingLocationEvents_Id] DEFAULT NEWSEQUENTIALID(),
    [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
    [Trigger] NVARCHAR(32) NOT NULL,
    [CapturedByType] NVARCHAR(16) NOT NULL,
    [CapturedById] UNIQUEIDENTIFIER NOT NULL,
    [Latitude] DECIMAL(9, 6) NOT NULL,
    [Longitude] DECIMAL(9, 6) NOT NULL,
    [AccuracyMetres] DECIMAL(9, 2) NULL,
    [DeviceCapturedAtUtc] DATETIME2(7) NULL,
    [RecordedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_NightStayBookingLocationEvents_RecordedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_NightStayBookingLocationEvents] PRIMARY KEY CLUSTERED ([NightStayBookingLocationEventId] ASC),
    CONSTRAINT [FK_NightStayBookingLocationEvents_NightStayBookings]
        FOREIGN KEY ([NightStayBookingId]) REFERENCES [Booking].[NightStayBookings] ([NightStayBookingId])
        ON DELETE CASCADE,
    CONSTRAINT [CK_NightStayBookingLocationEvents_Trigger]
        CHECK ([Trigger] IN (N'ArrivalConfirmed', N'JobStartProceeded', N'NoShowMarked',
                            N'StartOtpShown', N'CashReceived', N'CashNotReceived',
                            N'EvidenceCaptured')),
    CONSTRAINT [CK_NightStayBookingLocationEvents_CapturedByType]
        CHECK ([CapturedByType] IN (N'Provider', N'Parent')),
    CONSTRAINT [CK_NightStayBookingLocationEvents_TriggerParty]
        CHECK (NOT ([Trigger] IN (N'ArrivalConfirmed', N'JobStartProceeded') AND [CapturedByType] = N'Parent')
           AND NOT ([Trigger] = N'StartOtpShown' AND [CapturedByType] = N'Provider')),
    CONSTRAINT [CK_NightStayBookingLocationEvents_Latitude]
        CHECK ([Latitude] BETWEEN -90 AND 90),
    CONSTRAINT [CK_NightStayBookingLocationEvents_Longitude]
        CHECK ([Longitude] BETWEEN -180 AND 180),
    CONSTRAINT [CK_NightStayBookingLocationEvents_AccuracyMetres]
        CHECK ([AccuracyMetres] IS NULL OR [AccuracyMetres] >= 0)
);

GO

CREATE INDEX [IX_NightStayBookingLocationEvents_Booking_RecordedAt]
    ON [Booking].[NightStayBookingLocationEvents] ([NightStayBookingId], [RecordedAtUtc] ASC)
    INCLUDE ([Trigger], [CapturedByType], [CapturedById], [Latitude], [Longitude],
             [AccuracyMetres], [DeviceCapturedAtUtc]);
