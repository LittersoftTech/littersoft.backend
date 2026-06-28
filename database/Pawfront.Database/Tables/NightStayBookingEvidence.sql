-- Job-completion evidence photos for multi-night boarding bookings. Mirror of
-- [Booking].[BookingEvidence], FK'd to [Booking].[NightStayBookings]. Presence
-- of >= 1 row gates the move to COMPLETED in
-- [Booking].[UpdateNightStayBookingStatus].
CREATE TABLE [Booking].[NightStayBookingEvidence]
(
    [NightStayBookingEvidenceId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_NightStayBookingEvidence_Id] DEFAULT NEWSEQUENTIALID(),
    [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
    [PhotoUrl] NVARCHAR(1000) NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_NightStayBookingEvidence_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_NightStayBookingEvidence] PRIMARY KEY CLUSTERED ([NightStayBookingEvidenceId] ASC),
    CONSTRAINT [FK_NightStayBookingEvidence_NightStayBookings]
        FOREIGN KEY ([NightStayBookingId]) REFERENCES [Booking].[NightStayBookings] ([NightStayBookingId])
        ON DELETE CASCADE
);

GO

CREATE INDEX [IX_NightStayBookingEvidence_Booking_Created]
    ON [Booking].[NightStayBookingEvidence] ([NightStayBookingId], [CreatedAtUtc] ASC)
    INCLUDE ([PhotoUrl]);
