-- Job-completion evidence photos for single-day bookings. The provider uploads
-- one or more photos (blob storage under the [BookingEvidence] folder); the
-- presence of >= 1 row here gates the move to COMPLETED (enforced in
-- [Booking].[UpdateBookingStatus]). One row per uploaded photo — same shape as
-- [Provider].[ProviderPhotos].
CREATE TABLE [Booking].[BookingEvidence]
(
    [BookingEvidenceId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_BookingEvidence_Id] DEFAULT NEWSEQUENTIALID(),
    [BookingId] UNIQUEIDENTIFIER NOT NULL,
    [PhotoUrl] NVARCHAR(1000) NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_BookingEvidence_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_BookingEvidence] PRIMARY KEY CLUSTERED ([BookingEvidenceId] ASC),
    CONSTRAINT [FK_BookingEvidence_Bookings_BookingId]
        FOREIGN KEY ([BookingId]) REFERENCES [Booking].[Bookings] ([BookingId])
        ON DELETE CASCADE
);

GO

CREATE INDEX [IX_BookingEvidence_Booking_Created]
    ON [Booking].[BookingEvidence] ([BookingId], [CreatedAtUtc] ASC)
    INCLUDE ([PhotoUrl]);
