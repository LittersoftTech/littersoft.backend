-- One row per attendee/ticket inside an event booking. AttendeeName is free
-- text and is not validated against any user table — the spec is that anyone
-- can be named on a ticket.
--
-- TicketNumber is 1..N within the parent booking and is assigned at insert
-- time by [Event].[CreateEventBooking]. EventId is denormalised so the GET
-- shape ("4 tickets, 4 entries") can be served without joining back.
CREATE TABLE [Event].[EventBookingTickets]
(
    [TicketId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_EventBookingTickets_TicketId] DEFAULT NEWSEQUENTIALID(),
    [BookingId] UNIQUEIDENTIFIER NOT NULL,
    [EventId] UNIQUEIDENTIFIER NOT NULL,
    [TicketNumber] INT NOT NULL,
    [AttendeeName] NVARCHAR(200) NOT NULL,
    [CreatedAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_EventBookingTickets_CreatedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_EventBookingTickets] PRIMARY KEY CLUSTERED ([TicketId] ASC),
    CONSTRAINT [FK_EventBookingTickets_EventBookings_BookingId]
        FOREIGN KEY ([BookingId]) REFERENCES [Event].[EventBookings] ([BookingId])
        ON DELETE CASCADE,
    CONSTRAINT [FK_EventBookingTickets_Events_EventId]
        FOREIGN KEY ([EventId]) REFERENCES [Event].[Events] ([EventId]),
    CONSTRAINT [CK_EventBookingTickets_TicketNumber] CHECK ([TicketNumber] >= 1),
    CONSTRAINT [UQ_EventBookingTickets_Booking_Number]
        UNIQUE ([BookingId], [TicketNumber])
);

GO

CREATE INDEX [IX_EventBookingTickets_Booking]
    ON [Event].[EventBookingTickets] ([BookingId])
    INCLUDE ([TicketNumber], [AttendeeName], [EventId]);
