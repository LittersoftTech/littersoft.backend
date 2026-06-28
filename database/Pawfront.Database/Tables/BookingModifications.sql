-- Staging area for a single-day booking's pending schedule-change proposal.
-- Holds ONLY the open proposal: when either party requests a date/time change a
-- row is inserted and the booking flips to MODIFICATION_REQUEST_BY_{PARENT|PROVIDER};
-- when the counterparty responds the row is DELETED — on accept the proposed
-- values are first copied onto the [Booking].[Bookings] row ("staging -> main"),
-- on decline they are simply discarded. At most one staging row per booking
-- (UNIQUE BookingId). Editing is limited to date + time (no service-item change).
-- Driven by [Booking].[RequestBookingModification] /
-- [Booking].[RespondBookingModification]; read by
-- [Booking].[GetPendingBookingModification].
CREATE TABLE [Booking].[BookingModifications]
(
    [BookingModificationId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_BookingModifications_Id] DEFAULT NEWSEQUENTIALID(),
    [BookingId] UNIQUEIDENTIFIER NOT NULL,
    -- Who proposed the change: 'Provider' or 'Parent'.
    [RequestedByActor] NVARCHAR(16) NOT NULL,
    [RequestedByActorId] UNIQUEIDENTIFIER NOT NULL,
    -- Proposed new schedule (date + time window only).
    [ProposedBookingDate] DATE NOT NULL,
    [ProposedStartTime] TIME(0) NOT NULL,
    [ProposedEndTime] TIME(0) NOT NULL,
    [RequestNote] NVARCHAR(500) NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_BookingModifications_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_BookingModifications] PRIMARY KEY CLUSTERED ([BookingModificationId] ASC),
    CONSTRAINT [UQ_BookingModifications_BookingId] UNIQUE ([BookingId]),
    CONSTRAINT [FK_BookingModifications_Bookings_BookingId]
        FOREIGN KEY ([BookingId]) REFERENCES [Booking].[Bookings] ([BookingId])
        ON DELETE CASCADE,
    CONSTRAINT [CK_BookingModifications_RequestedByActor]
        CHECK ([RequestedByActor] IN (N'Provider', N'Parent')),
    CONSTRAINT [CK_BookingModifications_TimeOrder]
        CHECK ([ProposedStartTime] < [ProposedEndTime])
);
