-- One row per ticket booking transaction against an event. Attendee names live
-- in [Event].[EventBookingTickets] (one row per ticket). The transaction here
-- represents WHO bought + payment + total ticket count — the per-ticket detail
-- is the child table.
--
-- Capacity is enforced in [Event].[CreateEventBooking] by summing TicketCount
-- across rows where Status = N'Confirmed' for the same EventId, under
-- UPDLOCK + HOLDLOCK so concurrent buyers serialise on the last seat.
CREATE TABLE [Event].[EventBookings]
(
    [BookingId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_EventBookings_BookingId] DEFAULT NEWSEQUENTIALID(),
    [EventId] UNIQUEIDENTIFIER NOT NULL,
    -- Booker contact — free text, not validated against any user table.
    [BookerName] NVARCHAR(200) NOT NULL,
    [BookerEmail] NVARCHAR(320) NOT NULL,
    [BookerMobile] NVARCHAR(32) NULL,
    -- Denormalised count of child ticket rows — feeds the race-safe capacity
    -- check without forcing a JOIN to the tickets table.
    [TicketCount] INT NOT NULL,
    [PaymentMethod] NVARCHAR(32) NOT NULL,
    [PaymentStatus] NVARCHAR(32) NOT NULL
        CONSTRAINT [DF_EventBookings_PaymentStatus] DEFAULT N'Pending',
    -- External gateway reference (set on payment confirmation callback). NULL
    -- while the booking is still Pending.
    [PaymentReference] NVARCHAR(200) NULL,
    -- Snapshot of price-per-ticket * TicketCount at booking time. Stored even
    -- for free events (0) for shape consistency.
    [TotalAmount] DECIMAL(18, 2) NOT NULL
        CONSTRAINT [DF_EventBookings_TotalAmount] DEFAULT (0),
    [Status] NVARCHAR(32) NOT NULL
        CONSTRAINT [DF_EventBookings_Status] DEFAULT N'Confirmed',
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_EventBookings_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [UpdatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_EventBookings_UpdatedAtUtc] DEFAULT SYSUTCDATETIME(),
    [CancelledAtUtc] DATETIME2(7) NULL,

    CONSTRAINT [PK_EventBookings] PRIMARY KEY CLUSTERED ([BookingId] ASC),
    CONSTRAINT [FK_EventBookings_Events_EventId]
        FOREIGN KEY ([EventId]) REFERENCES [Event].[Events] ([EventId]),
    CONSTRAINT [CK_EventBookings_TicketCount] CHECK ([TicketCount] >= 1),
    CONSTRAINT [CK_EventBookings_TotalAmount] CHECK ([TotalAmount] >= 0),
    CONSTRAINT [CK_EventBookings_PaymentMethod]
        CHECK ([PaymentMethod] IN (N'CreditCard', N'Twint', N'Cash', N'Free')),
    CONSTRAINT [CK_EventBookings_PaymentStatus]
        CHECK ([PaymentStatus] IN (N'Pending', N'Paid', N'Failed')),
    CONSTRAINT [CK_EventBookings_Status]
        CHECK ([Status] IN (N'Confirmed', N'Cancelled')),
    CONSTRAINT [CK_EventBookings_CancelledRequiresTimestamp] CHECK (
        ([Status] = N'Cancelled' AND [CancelledAtUtc] IS NOT NULL)
        OR ([Status] <> N'Cancelled')
    )
);

GO

-- Capacity check + listings by event filter on (EventId, Status) and read
-- TicketCount as an output column.
CREATE INDEX [IX_EventBookings_Event_Status]
    ON [Event].[EventBookings] ([EventId], [Status])
    INCLUDE ([TicketCount], [BookingId], [BookerEmail], [PaymentStatus], [CreatedAtUtc]);
