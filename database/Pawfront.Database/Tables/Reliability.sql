CREATE TABLE [Event].[Reliability] (
 AccountId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
 LastNegativeAtUtc DATETIME2(7) NULL,
 ConsecutiveNegativeMarks INT NOT NULL DEFAULT 0 CHECK (ConsecutiveNegativeMarks BETWEEN 0 AND 1),
 RedFlagExpiresAt DATETIME2(7) NULL,
 ConsecutivePaidEventsAttended INT NOT NULL DEFAULT 0 CHECK (ConsecutivePaidEventsAttended BETWEEN 0 AND 2)
);
