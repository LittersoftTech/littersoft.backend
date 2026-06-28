-- Staging area for a multi-night booking's pending date-range change proposal.
-- Mirror of [Booking].[BookingModifications]; the editable fields are the
-- check-in / check-out range (the "date and time" of an overnight stay).
-- Holds ONLY the open proposal (DELETED on accept/decline); at most one per
-- booking (UNIQUE NightStayBookingId).
CREATE TABLE [Booking].[NightStayBookingModifications]
(
    [NightStayBookingModificationId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_NightStayBookingModifications_Id] DEFAULT NEWSEQUENTIALID(),
    [NightStayBookingId] UNIQUEIDENTIFIER NOT NULL,
    [RequestedByActor] NVARCHAR(16) NOT NULL,
    [RequestedByActorId] UNIQUEIDENTIFIER NOT NULL,
    [ProposedCheckInDate] DATE NOT NULL,
    [ProposedCheckOutDate] DATE NOT NULL,
    [RequestNote] NVARCHAR(500) NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_NightStayBookingModifications_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_NightStayBookingModifications] PRIMARY KEY CLUSTERED ([NightStayBookingModificationId] ASC),
    CONSTRAINT [UQ_NightStayBookingModifications_BookingId] UNIQUE ([NightStayBookingId]),
    CONSTRAINT [FK_NightStayBookingModifications_NightStayBookings]
        FOREIGN KEY ([NightStayBookingId]) REFERENCES [Booking].[NightStayBookings] ([NightStayBookingId])
        ON DELETE CASCADE,
    CONSTRAINT [CK_NightStayBookingModifications_RequestedByActor]
        CHECK ([RequestedByActor] IN (N'Provider', N'Parent')),
    CONSTRAINT [CK_NightStayBookingModifications_DateOrder]
        CHECK ([ProposedCheckOutDate] > [ProposedCheckInDate])
);
