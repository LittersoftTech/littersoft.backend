CREATE TABLE [Booking].[Bookings]
(
    [BookingId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_Bookings_BookingId] DEFAULT NEWSEQUENTIALID(),
    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
    [PetParentId] UNIQUEIDENTIFIER NOT NULL,
    [ServiceCategory] NVARCHAR(64) NOT NULL,
    [SubCategory] NVARCHAR(64) NOT NULL,
    [BookingDate] DATE NOT NULL,
    [StartTime] TIME(0) NOT NULL,
    [EndTime] TIME(0) NOT NULL,
    [Status] NVARCHAR(32) NOT NULL
        CONSTRAINT [DF_Bookings_Status] DEFAULT N'Confirmed',
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Bookings_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_Bookings_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [CancelledAtUtc] DATETIME2(7) NULL,

    CONSTRAINT [PK_Bookings] PRIMARY KEY CLUSTERED ([BookingId] ASC),
    CONSTRAINT [FK_Bookings_Providers_ProviderId]
        FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]),
    CONSTRAINT [FK_Bookings_PetParents_PetParentId]
        FOREIGN KEY ([PetParentId]) REFERENCES [Customer].[PetParents] ([PetParentId]),
    CONSTRAINT [CK_Bookings_TimeOrder] CHECK ([StartTime] < [EndTime]),
    CONSTRAINT [CK_Bookings_Status]
        CHECK ([Status] IN (N'Confirmed', N'Cancelled', N'Completed', N'NoShow')),
    CONSTRAINT [CK_Bookings_CancelledRequiresTimestamp] CHECK (
        ([Status] = N'Cancelled' AND [CancelledAtUtc] IS NOT NULL)
        OR ([Status] <> N'Cancelled')
    )
);

GO

CREATE INDEX [IX_Bookings_Provider_Date_Status]
    ON [Booking].[Bookings] ([ProviderId], [BookingDate], [Status])
    INCLUDE ([StartTime], [EndTime], [BookingId], [PetParentId]);

GO

CREATE INDEX [IX_Bookings_PetParent_Status]
    ON [Booking].[Bookings] ([PetParentId], [Status])
    INCLUDE ([BookingDate], [StartTime], [EndTime], [ProviderId]);
