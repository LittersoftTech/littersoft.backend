CREATE TABLE [Booking].[ServiceReliabilityMarks]
(
    [BookingType] NVARCHAR(16) NOT NULL,
    [BookingId] UNIQUEIDENTIFIER NOT NULL,
    [PetParentId] UNIQUEIDENTIFIER NOT NULL,
    [Outcome] NVARCHAR(32) NOT NULL,
    [OccurredAtUtc] DATETIME2(7) NOT NULL,
    [ServiceDate] DATE NOT NULL,
    CONSTRAINT [PK_ServiceReliabilityMarks] PRIMARY KEY ([BookingType], [BookingId]),
    CONSTRAINT [CK_ServiceReliabilityMarks_Type] CHECK ([BookingType] IN (N'SingleDay', N'NightStay')),
    CONSTRAINT [CK_ServiceReliabilityMarks_Outcome] CHECK ([Outcome] IN (N'Paid', N'LateCancellation', N'NoShow', N'Unpaid'))
);
GO
CREATE INDEX [IX_ServiceReliabilityMarks_Parent_Date]
    ON [Booking].[ServiceReliabilityMarks] ([PetParentId], [OccurredAtUtc] DESC)
    INCLUDE ([Outcome], [ServiceDate]);
