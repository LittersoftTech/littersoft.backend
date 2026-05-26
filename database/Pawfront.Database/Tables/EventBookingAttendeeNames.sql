-- Table type used by [Event].[CreateEventBooking] to receive the list of
-- attendee names in a single round-trip. Sent over from .NET as a
-- SqlParameter with TypeName 'Event.EventBookingAttendeeNames'.
--
-- TicketNumber is 1..N — the caller assigns the position so the order on the
-- wire is preserved.
CREATE TYPE [Event].[EventBookingAttendeeNames] AS TABLE
(
    [TicketNumber] INT NOT NULL PRIMARY KEY,
    [AttendeeName] NVARCHAR(200) NOT NULL
);
