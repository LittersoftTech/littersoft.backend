-- Capture new outcomes transactionally from the existing audit writes. No historical backfill.
CREATE OR ALTER TRIGGER [Booking].[CaptureSingleDayReliability]
ON [Booking].[BookingStatusHistory]
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Marks TABLE (BookingId UNIQUEIDENTIFIER PRIMARY KEY, PetParentId UNIQUEIDENTIFIER,
        Outcome NVARCHAR(32), OccurredAtUtc DATETIME2(7), ServiceDate DATE);
    ;WITH candidates AS
    (
        SELECT h.BookingId AS BookingId, b.PetParentId, h.ChangedAtUtc, b.BookingDate AS ServiceDate,
            CASE h.ToStatus
                WHEN N'PAID' THEN N'Paid'
                WHEN N'PAYMENT_NOT_RECEIVED' THEN N'Unpaid'
                WHEN N'PARENT_NO_SHOW' THEN N'NoShow'
                WHEN N'PARENT_CANCELLED' THEN N'LateCancellation'
            END AS Outcome,
            ROW_NUMBER() OVER (PARTITION BY h.BookingId ORDER BY h.ChangedAtUtc DESC,
                CASE h.ToStatus WHEN N'PAID' THEN 4 WHEN N'PAYMENT_NOT_RECEIVED' THEN 3 WHEN N'PARENT_NO_SHOW' THEN 2 ELSE 1 END DESC) AS Position
        FROM inserted h
        JOIN [Booking].[Bookings] b ON b.BookingId = h.BookingId
        WHERE b.PetParentId IS NOT NULL
          AND b.Source = N'App'
          AND b.ServiceCategory IN (N'PetSitter', N'PetGroomer', N'PetTrainer', N'Vet')
          AND (b.PricePerHour IS NULL OR b.PricePerHour > 0) -- Free introductory meetings are excluded.
          AND h.ToStatus IN (N'PAID', N'PAYMENT_NOT_RECEIVED', N'PARENT_NO_SHOW', N'PARENT_CANCELLED')
          AND (h.ToStatus <> N'PARENT_CANCELLED' OR
            (COALESCE(b.CancellationReason, N'') <> N'CancelledAfterModificationDeclined'
             AND h.FromStatus <> N'CREATED' AND b.CancellationPolicyHours IS NOT NULL
             AND h.ChangedAtUtc > DATEADD(hour, -b.CancellationPolicyHours,
                 DATEADD(second, DATEDIFF(second, CAST('00:00' AS TIME), b.StartTime), CAST(b.BookingDate AS DATETIME2(7))))))
    )
    INSERT @Marks SELECT BookingId, PetParentId, Outcome, ChangedAtUtc, ServiceDate FROM candidates WHERE Position = 1;

    -- An unpaid booking subsequently paid contributes one positive mark.
    UPDATE m SET Outcome = n.Outcome, OccurredAtUtc = n.OccurredAtUtc, ServiceDate = n.ServiceDate
    FROM [Booking].[ServiceReliabilityMarks] m JOIN @Marks n ON n.BookingId = m.BookingId
    WHERE m.BookingType = N'SingleDay';

    INSERT [Booking].[ServiceReliabilityMarks] (BookingType, BookingId, PetParentId, Outcome, OccurredAtUtc, ServiceDate)
    SELECT N'SingleDay', n.BookingId, n.PetParentId, n.Outcome, n.OccurredAtUtc, n.ServiceDate
    FROM @Marks n WHERE NOT EXISTS
        (SELECT 1 FROM [Booking].[ServiceReliabilityMarks] m WITH (UPDLOCK, HOLDLOCK)
         WHERE m.BookingType = N'SingleDay' AND m.BookingId = n.BookingId);
END;

