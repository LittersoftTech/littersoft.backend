-- Verification OTPs for single-day bookings: the parent-facing start code that
-- gates the START_JOB -> IN_PROGRESS transition. The server issues (or reuses) a
-- short-lived 6-digit code and returns it to the parent, who reads it to the
-- provider; the provider enters it to begin the job. The code is a low-secrecy
-- SHARE code (already shown to the parent), so it is stored in plaintext to
-- allow reuse-while-valid. This table is the telemetry record of every issuance
-- + consumption (status, timestamps, failed attempts).
CREATE TABLE [Booking].[BookingStartOtps]
(
    [BookingStartOtpId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_BookingStartOtps_Id] DEFAULT NEWSEQUENTIALID(),
    [BookingId] UNIQUEIDENTIFIER NOT NULL,
    [OtpCode] NVARCHAR(6) NOT NULL,
    -- 'Pending' (issued, awaiting use), 'Consumed' (used to start the job),
    -- 'Expired' (superseded by a fresh issue, or past ExpiresAtUtc).
    [Status] NVARCHAR(16) NOT NULL
        CONSTRAINT [DF_BookingStartOtps_Status] DEFAULT N'Pending',
    [FailedAttemptCount] INT NOT NULL
        CONSTRAINT [DF_BookingStartOtps_FailedAttemptCount] DEFAULT 0,
    [IssuedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_BookingStartOtps_IssuedAtUtc] DEFAULT SYSUTCDATETIME(),
    [ExpiresAtUtc] DATETIME2(7) NOT NULL,
    [ConsumedAtUtc] DATETIME2(7) NULL,

    CONSTRAINT [PK_BookingStartOtps] PRIMARY KEY CLUSTERED ([BookingStartOtpId] ASC),
    CONSTRAINT [FK_BookingStartOtps_Bookings_BookingId]
        FOREIGN KEY ([BookingId]) REFERENCES [Booking].[Bookings] ([BookingId])
        ON DELETE CASCADE,
    CONSTRAINT [CK_BookingStartOtps_Status]
        CHECK ([Status] IN (N'Pending', N'Consumed', N'Expired'))
);

GO

CREATE INDEX [IX_BookingStartOtps_Booking_Issued]
    ON [Booking].[BookingStartOtps] ([BookingId], [IssuedAtUtc] DESC)
    INCLUDE ([OtpCode], [Status], [ExpiresAtUtc]);
