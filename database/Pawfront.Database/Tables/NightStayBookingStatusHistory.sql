-- Append-only audit trail of every night-stay-booking status change. One row
-- per transition (plus a seeded creation row with FromStatus = NULL). Written
-- atomically alongside the [Booking].[NightStayBookings] status update by
-- Booking.UpdateNightStayBookingStatus / CancelNightStayBooking /
-- CreateNightStayBooking. Parallel to [Booking].[BookingStatusHistory], which
-- FKs to the single-day [Booking].[Bookings] table.
CREATE TABLE [Booking].[NightStayBookingStatusHistory]
(
    [NightStayBookingStatusHistoryId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_NightStayBookingStatusHistory_Id] DEFAULT NEWSEQUENTIALID(),
    [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
    -- NULL only for the initial creation entry (no prior status).
    [FromStatus] NVARCHAR(48) NULL,
    [ToStatus] NVARCHAR(48) NOT NULL,
    -- Who drove the change: 'Provider', 'Parent', or 'System' (the creation seed).
    [ChangedByActor] NVARCHAR(16) NOT NULL,
    -- The ProviderId / PetParentId behind the change; NULL for System entries.
    [ChangedByActorId] UNIQUEIDENTIFIER NULL,
    [Note] NVARCHAR(500) NULL,
    [ChangedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_NightStayBookingStatusHistory_ChangedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_NightStayBookingStatusHistory] PRIMARY KEY CLUSTERED ([NightStayBookingStatusHistoryId] ASC),
    CONSTRAINT [FK_NightStayBookingStatusHistory_NightStayBookings]
        FOREIGN KEY ([NightStayBookingId]) REFERENCES [Booking].[NightStayBookings] ([NightStayBookingId])
        ON DELETE CASCADE,
    CONSTRAINT [CK_NightStayBookingStatusHistory_Actor]
        CHECK ([ChangedByActor] IN (N'Provider', N'Parent', N'System'))
);

GO

CREATE INDEX [IX_NightStayBookingStatusHistory_Booking_ChangedAt]
    ON [Booking].[NightStayBookingStatusHistory] ([NightStayBookingId], [ChangedAtUtc] ASC)
    INCLUDE ([FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note]);
