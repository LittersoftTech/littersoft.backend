-- The full location timeline for one booking, oldest first — the read the
-- admin/support panel needs when working a dispute ("was the provider actually
-- there when they said they had arrived?").
--
-- ONE procedure for both booking kinds, like [Review].[UpsertBookingReview] and
-- unlike the twinned write paths: the only difference between them is which
-- table supplies the rows, and a support screen should not have to know which
-- kind it is holding before it can ask. @BookingType is 'SingleDay' or
-- 'NightStay' — the same vocabulary [Booking].[BookingPayments] uses.
--
-- Deliberately NOT exposed on either app. These are both parties' precise
-- coordinates: handing a provider the parent's position (or the reverse) is a
-- safety problem, and the stated purpose of the capture is to confirm things to
-- Pawfront, not to the counterparty. It ships ahead of the panel for the same
-- reason [Support].[CloseTicket] did — so the panel is a single call away.
--
-- Returns an empty set for an unknown booking rather than throwing: this is a
-- read, and a support screen showing "no location was recorded" is a better
-- answer than an error.
CREATE OR ALTER PROCEDURE [Booking].[ListBookingLocationEvents]
    @BookingType NVARCHAR(16),
    @BookingId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    IF @BookingType = N'NightStay'
    BEGIN
        SELECT [NightStayBookingLocationEventId] AS [BookingLocationEventId],
               [NightStayBookingId] AS [BookingId],
               [Trigger],
               [CapturedByType],
               [CapturedById],
               [Latitude],
               [Longitude],
               [AccuracyMetres],
               [DeviceCapturedAtUtc],
               [RecordedAtUtc]
        FROM [Booking].[NightStayBookingLocationEvents]
        WHERE [NightStayBookingId] = @BookingId
        ORDER BY [RecordedAtUtc] ASC, [NightStayBookingLocationEventId] ASC;
    END
    ELSE
    BEGIN
        SELECT [BookingLocationEventId],
               [BookingId],
               [Trigger],
               [CapturedByType],
               [CapturedById],
               [Latitude],
               [Longitude],
               [AccuracyMetres],
               [DeviceCapturedAtUtc],
               [RecordedAtUtc]
        FROM [Booking].[BookingLocationEvents]
        WHERE [BookingId] = @BookingId
        ORDER BY [RecordedAtUtc] ASC, [BookingLocationEventId] ASC;
    END
END;
