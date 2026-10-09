CREATE TABLE [Event].[ReliabilityOutcomes] (
 BookingId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY REFERENCES [Event].[EventBookings](BookingId),
 AccountId UNIQUEIDENTIFIER NOT NULL,
 Outcome NVARCHAR(16) NOT NULL CHECK (Outcome IN (N'Negative', N'PaidAttendance', N'Attendance')),
 RecordedAtUtc DATETIME2(7) NOT NULL DEFAULT SYSUTCDATETIME()
);
