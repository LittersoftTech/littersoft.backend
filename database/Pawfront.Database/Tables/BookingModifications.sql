-- Staging area for a single-day booking's pending schedule-change proposal.
-- Holds ONLY the open proposal: when either party requests a date/time change a
-- row is inserted and the booking flips to MODIFICATION_REQUEST_BY_{PARENT|PROVIDER};
-- when the counterparty responds the row is DELETED — on accept the proposed
-- values are first copied onto the [Booking].[Bookings] row ("staging -> main"),
-- on decline they are simply discarded. At most one staging row per booking
-- (UNIQUE BookingId). Editing is limited to date + time (no service-item change),
-- but the row ALSO stages the provider's current terms when they have drifted
-- since the booking was created and the requester acknowledged the drift — those
-- are copied onto the booking by the same accept ("staging -> main").
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
    -- The provider's CURRENT terms, as shown to and acknowledged by the requester
    -- when the proposal was made. The booking froze its own terms at creation; if
    -- the provider has since changed them the requester sees a "these changed"
    -- sheet, and the values they agreed to are staged here so the counterparty's
    -- accept applies exactly what was shown — not whatever is live at that later
    -- moment. [HasAcknowledgedTerms] = 0 means "nothing staged, leave the
    -- booking's frozen terms alone"; it is NOT the same as staging NULLs, since a
    -- NULL cancellation policy legitimately means "no restriction".
    [HasAcknowledgedTerms] BIT NOT NULL
        CONSTRAINT [DF_BookingModifications_HasAcknowledgedTerms] DEFAULT 0,
    [AcknowledgedPricePerHour] DECIMAL(10, 2) NULL,
    [AcknowledgedCancellationPolicyHours] INT NULL,
    [AcknowledgedAddressLine] NVARCHAR(500) NULL,
    [AcknowledgedCity] NVARCHAR(200) NULL,
    [AcknowledgedZipCode] NVARCHAR(32) NULL,
    [AcknowledgedLatitude] DECIMAL(9, 6) NULL,
    [AcknowledgedLongitude] DECIMAL(9, 6) NULL,
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
        CHECK ([ProposedStartTime] < [ProposedEndTime]),
    CONSTRAINT [CK_BookingModifications_AcknowledgedPrice]
        CHECK ([AcknowledgedPricePerHour] IS NULL OR [AcknowledgedPricePerHour] >= 0),
    CONSTRAINT [CK_BookingModifications_AcknowledgedCancellationPolicy]
        CHECK ([AcknowledgedCancellationPolicyHours] IS NULL
               OR [AcknowledgedCancellationPolicyHours] IN (24, 48, 72, 96))
);
