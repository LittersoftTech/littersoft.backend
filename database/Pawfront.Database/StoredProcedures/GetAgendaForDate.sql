CREATE OR ALTER PROCEDURE [Booking].[GetAgendaForDate]
    @ServiceId   UNIQUEIDENTIFIER,
    @BookingDate DATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Backs the parent-facing daily-agenda surface. Same rows (and the same
    -- "a booking holds its slot unless cancelled / declined / no-show / expired"
    -- predicate) as [Booking].[GetBookingsForDate] -- which the slot service
    -- uses for its overlap count -- but carries the identity the agenda needs:
    -- who booked it, its job number, and its lifecycle status.
    --
    -- The agenda MUST agree with the slot grid on what is occupied, so the two
    -- predicates are kept identical; change one, change the other.
    --
    -- [PetParentId] is what lets the caller's OWN jobs be told apart from
    -- everyone else's: the API masks the status + job id of rows belonging to a
    -- different parent, so one parent never reads another's appointments. It is
    -- NULL for Custom walk-ins (provider-entered private jobs), which therefore
    -- always mask.
    --
    -- Night stays live in [Booking].[NightStayBookings] and occupy their bucket
    -- for the WHOLE night, so they surface as a full-day window. Those rows only
    -- match a NightStay ServiceId, so DayCare & co. see none.
    SELECT N'SingleDay' AS [BookingType],
           [BookingId],
           [JobNumber],
           [PetParentId],
           [StartTime],
           [EndTime],
           [Status]
    FROM [Booking].[Bookings]
    WHERE [ServiceId] = @ServiceId
      AND [BookingDate] = @BookingDate
      AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')

    UNION ALL

    SELECT N'NightStay' AS [BookingType],
           [NightStayBookingId] AS [BookingId],
           [JobNumber],
           [PetParentId],
           CAST(N'00:00:00' AS TIME(0)) AS [StartTime],
           CAST(N'23:59:59' AS TIME(0)) AS [EndTime],
           [Status]
    FROM [Booking].[NightStayBookings]
    WHERE [ServiceId] = @ServiceId
      AND [Status] NOT IN (N'PROVIDER_CANCELLED', N'PARENT_CANCELLED', N'PROVIDER_DECLINED', N'PARENT_NO_SHOW', N'PROVIDER_NO_SHOW', N'EXPIRED', N'JOB_EXPIRED', N'OTP_MAX_ATTEMPTS_EXCEEDED')
      AND [CheckInDate] <= @BookingDate
      AND [CheckOutDate] > @BookingDate

    ORDER BY [StartTime], [EndTime];
END;
