-- Append-only audit trail of every service-booking status change. One row per
-- transition (plus a seeded creation row with FromStatus = NULL). Written
-- atomically alongside the Booking.Bookings status update by
-- Booking.UpdateBookingStatus / CancelBooking / CreateBooking(/Custom).
CREATE TABLE [Booking].[BookingStatusHistory]
(
    [BookingStatusHistoryId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_BookingStatusHistory_Id] DEFAULT NEWSEQUENTIALID(),
    [BookingId] UNIQUEIDENTIFIER NOT NULL,
    -- NULL only for the initial creation entry (no prior status).
    [FromStatus] NVARCHAR(32) NULL,
    [ToStatus] NVARCHAR(32) NOT NULL,
    -- Who drove the change: 'Provider', 'Parent', or 'System' (the creation seed).
    [ChangedByActor] NVARCHAR(16) NOT NULL,
    -- The ProviderId / PetParentId behind the change; NULL for System entries.
    [ChangedByActorId] UNIQUEIDENTIFIER NULL,
    [Note] NVARCHAR(500) NULL,
    [ChangedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_BookingStatusHistory_ChangedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_BookingStatusHistory] PRIMARY KEY CLUSTERED ([BookingStatusHistoryId] ASC),
    CONSTRAINT [FK_BookingStatusHistory_Bookings_BookingId]
        FOREIGN KEY ([BookingId]) REFERENCES [Booking].[Bookings] ([BookingId])
        ON DELETE CASCADE,
    CONSTRAINT [CK_BookingStatusHistory_Actor]
        CHECK ([ChangedByActor] IN (N'Provider', N'Parent', N'System'))
);

GO

CREATE INDEX [IX_BookingStatusHistory_Booking_ChangedAt]
    ON [Booking].[BookingStatusHistory] ([BookingId], [ChangedAtUtc] ASC)
    INCLUDE ([FromStatus], [ToStatus], [ChangedByActor], [ChangedByActorId], [Note]);
