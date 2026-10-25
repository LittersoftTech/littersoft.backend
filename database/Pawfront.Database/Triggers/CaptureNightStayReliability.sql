-- Capture new outcomes transactionally from the existing audit writes. No historical backfill.
CREATE OR ALTER TRIGGER [Booking].[CaptureNightStayReliability]
ON [Booking].[NightStayBookingStatusHistory]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Marks TABLE (BookingId UNIQUEIDENTIFIER PRIMARY KEY, PetParentId UNIQUEIDENTIFIER,
        Outcome NVARCHAR(32), OccurredAtUtc DATETIME2(7), ServiceDate DATE);
    ;WITH candidates AS
    (
        SELECT h.NightStayBookingId AS BookingId, b.PetParentId, h.ChangedAtUtc, b.CheckInDate AS ServiceDate,
            CASE h.ToStatus
                WHEN N'PAID' THEN N'Paid'
                WHEN N'PAYMENT_NOT_RECEIVED' THEN N'Unpaid'
                WHEN N'PARENT_NO_SHOW' THEN N'NoShow'
                WHEN N'PARENT_CANCELLED' THEN N'LateCancellation'
            END AS Outcome,
            ROW_NUMBER() OVER (PARTITION BY h.NightStayBookingId ORDER BY h.ChangedAtUtc DESC,
                CASE h.ToStatus WHEN N'PAID' THEN 4 WHEN N'PAYMENT_NOT_RECEIVED' THEN 3 WHEN N'PARENT_NO_SHOW' THEN 2 ELSE 1 END DESC) AS Position
        FROM inserted h
        JOIN [Booking].[NightStayBookings] b ON b.NightStayBookingId = h.NightStayBookingId
        WHERE b.PetParentId IS NOT NULL

          AND b.ServiceCategory IN (N'PetSitter', N'PetGroomer', N'PetTrainer', N'Vet')

          AND h.ToStatus IN (N'PAID', N'PAYMENT_NOT_RECEIVED', N'PARENT_NO_SHOW', N'PARENT_CANCELLED')
          AND (h.ToStatus <> N'PARENT_CANCELLED' OR
            (COALESCE(b.CancellationReason, N'') <> N'CancelledAfterModificationDeclined'
             AND h.FromStatus <> N'CREATED' AND b.CancellationPolicyHours IS NOT NULL
             AND h.ChangedAtUtc > DATEADD(hour, -b.CancellationPolicyHours,
                 DATEADD(second, DATEDIFF(second, CAST('00:00' AS TIME), b.DropOffTime), CAST(b.CheckInDate AS DATETIME2(7))))))
    )
    INSERT @Marks SELECT BookingId, PetParentId, Outcome, ChangedAtUtc, ServiceDate FROM candidates WHERE Position = 1;

    -- An unpaid booking subsequently paid contributes one positive mark.
    UPDATE m SET Outcome = n.Outcome, OccurredAtUtc = n.OccurredAtUtc, ServiceDate = n.ServiceDate
    FROM [Booking].[ServiceReliabilityMarks] m JOIN @Marks n ON n.BookingId = m.BookingId
    WHERE m.BookingType = N'NightStay';

    INSERT [Booking].[ServiceReliabilityMarks] (BookingType, BookingId, PetParentId, Outcome, OccurredAtUtc, ServiceDate)
    SELECT N'NightStay', n.BookingId, n.PetParentId, n.Outcome, n.OccurredAtUtc, n.ServiceDate
    FROM @Marks n WHERE NOT EXISTS
        (SELECT 1 FROM [Booking].[ServiceReliabilityMarks] m WITH (UPDLOCK, HOLDLOCK)
         WHERE m.BookingType = N'NightStay' AND m.BookingId = n.BookingId);
END;

