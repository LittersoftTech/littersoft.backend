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
    -- predicates are kept identical; change one, change the other. That now
    -- includes the early-finish rule: a job that ended before its booked end
    -- occupies only [StartTime, COALESCE([ActualEndTime], [EndTime])), so the
    -- freed remainder shows on the agenda as bookable rather than as a job that
    -- is somehow still running. ([ActualEndTime] is NULL for everything that did
    -- not finish early, so this is a no-op for those.) The block still reports
    -- the booking's real lifecycle status — it is COMPLETED, and shown as such.
    --
    -- [PetParentId] is what lets the caller's OWN jobs be told apart from
    -- everyone else's: the API masks the status + job id of rows belonging to a
    -- different parent, so one parent never reads another's appointments. It is
    -- NULL for Custom walk-ins (provider-entered private jobs), which therefore
    -- always mask.
    --
    -- Night stays live in [Booking].[NightStayBookings] and occupy their bucket
    -- for the WHOLE night, so they surface as a full-day window. Those rows only
    -- match a NightStay ServiceId, so DayCare & co. see none. A stay collected
    -- early stops covering nights at [ActualCheckOutDate], same rule as above.
    SELECT N'SingleDay' AS [BookingType],
           [BookingId],
           [JobNumber],
           [PetParentId],
           [StartTime],
           COALESCE([ActualEndTime], [EndTime]) AS [EndTime],
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
      AND COALESCE([ActualCheckOutDate], [CheckOutDate]) > @BookingDate

    ORDER BY [StartTime], [EndTime];
END;
