CREATE TABLE [Event].[EventAmenities]
(
    [EventId] UNIQUEIDENTIFIER NOT NULL,
    [Amenity] NVARCHAR(64) NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_EventAmenities_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_EventAmenities] PRIMARY KEY CLUSTERED ([EventId] ASC, [Amenity] ASC),
    CONSTRAINT [FK_EventAmenities_Events_EventId]
        FOREIGN KEY ([EventId]) REFERENCES [Event].[Events] ([EventId]) ON DELETE CASCADE,
    CONSTRAINT [CK_EventAmenities_Amenity] CHECK ([Amenity] IN (
        N'FreeParking', N'PaidParking', N'Restrooms', N'DrinkingWater',
        N'FoodAndBeverage', N'SeatingAreas', N'FirstAidBooth', N'None'))
);
