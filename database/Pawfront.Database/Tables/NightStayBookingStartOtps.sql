-- Start-job OTPs for multi-night boarding bookings. Mirror of
-- [Booking].[BookingStartOtps], FK'd to [Booking].[NightStayBookings].
CREATE TABLE [Booking].[NightStayBookingStartOtps]
(
    [NightStayBookingStartOtpId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_NightStayBookingStartOtps_Id] DEFAULT NEWSEQUENTIALID(),
    [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
    [OtpCode] NVARCHAR(6) NOT NULL,
    [Status] NVARCHAR(16) NOT NULL
        CONSTRAINT [DF_NightStayBookingStartOtps_Status] DEFAULT N'Pending',
    [FailedAttemptCount] INT NOT NULL
        CONSTRAINT [DF_NightStayBookingStartOtps_FailedAttemptCount] DEFAULT 0,
    [IssuedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_NightStayBookingStartOtps_IssuedAtUtc] DEFAULT SYSUTCDATETIME(),
    [ExpiresAtUtc] DATETIME2(7) NOT NULL,
    [ConsumedAtUtc] DATETIME2(7) NULL,

    CONSTRAINT [PK_NightStayBookingStartOtps] PRIMARY KEY CLUSTERED ([NightStayBookingStartOtpId] ASC),
    CONSTRAINT [FK_NightStayBookingStartOtps_NightStayBookings]
        FOREIGN KEY ([NightStayBookingId]) REFERENCES [Booking].[NightStayBookings] ([NightStayBookingId])
        ON DELETE CASCADE,
    CONSTRAINT [CK_NightStayBookingStartOtps_Status]
        CHECK ([Status] IN (N'Pending', N'Consumed', N'Expired'))
);

GO

CREATE INDEX [IX_NightStayBookingStartOtps_Booking_Issued]
    ON [Booking].[NightStayBookingStartOtps] ([NightStayBookingId], [IssuedAtUtc] DESC)
    INCLUDE ([OtpCode], [Status], [ExpiresAtUtc]);
