-- The single definition of "what is this booking worth, and has the money moved" —
-- used by BOTH the provider earnings sprocs and the pet-parent spend/history
-- sprocs, so the two sides can never report different figures for the same
-- booking. Unifies the two booking tables behind one shape.
--
-- Pass exactly one of @ProviderId / @PetParentId; leave the other NULL. Every
-- booking of that party is returned regardless of status — callers narrow it via
-- [IsEarned] (money actually moved) or [Status] (the parent's history list shows
-- cancelled and upcoming bookings too, each with its expected amount).
--
-- [IsEarned] = the job reached COMPLETED or PAID. COMPLETED-not-yet-PAID counts as
-- earned/spent on purpose: with cash the provider has done the work and the parent
-- has typically already handed the money over — only the provider's "mark paid" tap
-- is outstanding, and the parent has no control over that. [IsPaid] separates the
-- two for callers that need it.
--
-- WHICH DATE: [ServiceDate] is the SERVICE date — [BookingDate] for single-day,
-- [CheckOutDate] for a stay (the checkout day is the pickup day, not a stayed
-- night, so the stay is over then). Deliberately NOT the payment date: a job done
-- on Sunday and marked paid on Monday belongs to Sunday's week, and an unpaid or
-- cancelled booking has no payment date at all yet still has to land in a period.
--
-- HOW MUCH: the ledger row wins whenever one exists ([Booking].[BookingPayments]
-- freezes Amount + PawfrontFee at payment time, so a later change to the platform
-- fee percentage cannot rewrite history). Anything without a ledger row is priced
-- from its own creation-time price-lock, and that arithmetic MIRRORS
-- BookingService.GetDetailAsync / NightStayBookingService exactly:
--   * single-day PetSitter (DayCare)  -> PricePerHour x hours
--   * every other single-day service  -> PricePerHour is already the flat fee
--   * night-stay                      -> PricePerNight x nights
-- Change the C# and you must change this, or the earnings screen will disagree
-- with the booking-detail screen.
--
-- A legacy row with no price snapshot yields [Amount] NULL rather than 0 — callers
-- surface that as an "unpriced" count instead of silently under-reporting.
--
-- Custom walk-ins ([IsPrivate] = 1) are off-platform: Pawfront takes no commission
-- (fee 0, matching the 0% the booking detail shows) and they can never be marked
-- PAID. They are emitted so a provider's own private jobs can be shown, but callers
-- keep them OUT of platform earnings totals. They carry no PetParentId, so a
-- parent-side query never sees them.
CREATE OR ALTER FUNCTION [Booking].[BookingAmounts]
(
    @ProviderId UNIQUEIDENTIFIER,
    @PetParentId UNIQUEIDENTIFIER,
    @FeePercentage DECIMAL(9, 4)
)
RETURNS TABLE
AS
RETURN
(
    SELECT [BookingType]   = N'SingleDay',
           [BookingId]     = b.[BookingId],
           [ProviderId]    = b.[ProviderId],
           [PetParentId]   = b.[PetParentId],
           [PetId]         = b.[PetId],
           [ServiceDate]   = b.[BookingDate],
           [Status]        = b.[Status],
           [IsEarned]      = CAST(CASE WHEN b.[Status] IN (N'COMPLETED', N'PAID') THEN 1 ELSE 0 END AS BIT),
           [IsPaid]        = CAST(CASE WHEN b.[Status] = N'PAID' THEN 1 ELSE 0 END AS BIT),
           [IsPrivate]     = CAST(CASE WHEN b.[Source] = N'Custom' THEN 1 ELSE 0 END AS BIT),
           [Amount]        = amt.[Amount],
           [Fee]           = CAST(COALESCE(
                                 pay.[PawfrontFee],
                                 CASE
                                     WHEN amt.[Amount] IS NULL THEN NULL
                                     WHEN b.[Source] = N'Custom' THEN 0
                                     ELSE ROUND(amt.[Amount] * @FeePercentage / 100.0, 2)
                                 END) AS DECIMAL(12, 2)),
           [PaidAtUtc]     = pay.[PaidAtUtc],
           [PaymentMethod] = pay.[PaymentMethod]
    FROM [Booking].[Bookings] b
    LEFT JOIN [Booking].[BookingPayments] pay
        ON pay.[BookingType] = N'SingleDay'
       AND pay.[BookingId] = b.[BookingId]
    CROSS APPLY
    (
        SELECT [Amount] = CAST(COALESCE(
            pay.[Amount],
            CASE
                WHEN b.[PricePerHour] IS NULL THEN NULL
                -- A Custom walk-in is ALWAYS rate x hours, whatever the category:
                -- [PricePerHour] there is the hourly rate the provider typed for
                -- that one job, not an offering's flat fee. (Mirrors
                -- BookingService.ResolveCustomPricing, which does not branch on
                -- category at all — branching here would make the earnings screen
                -- disagree with the booking detail for, say, a Vet walk-in.)
                WHEN b.[Source] = N'Custom' THEN
                    ROUND(b.[PricePerHour] * (DATEDIFF(MINUTE, b.[StartTime], b.[EndTime]) / 60.0), 2)
                -- App bookings: only PetSitter DayCare bills per hour; every other
                -- single-day service snapshots a flat fee.
                WHEN b.[ServiceCategory] = N'PetSitter'
                    THEN ROUND(b.[PricePerHour] * (DATEDIFF(MINUTE, b.[StartTime], b.[EndTime]) / 60.0), 2)
                ELSE ROUND(b.[PricePerHour], 2)
            END) AS DECIMAL(12, 2))
    ) amt
    WHERE (@ProviderId IS NULL OR b.[ProviderId] = @ProviderId)
      AND (@PetParentId IS NULL OR b.[PetParentId] = @PetParentId)

    UNION ALL

    -- Night-stay bookings are always App bookings (there is no Custom walk-in
    -- boarding flow), so [IsPrivate] is constant 0 here.
    SELECT [BookingType]   = N'NightStay',
           [BookingId]     = n.[NightStayBookingId],
           [ProviderId]    = n.[ProviderId],
           [PetParentId]   = n.[PetParentId],
           [PetId]         = n.[PetId],
           [ServiceDate]   = n.[CheckOutDate],
           [Status]        = n.[Status],
           [IsEarned]      = CAST(CASE WHEN n.[Status] IN (N'COMPLETED', N'PAID') THEN 1 ELSE 0 END AS BIT),
           [IsPaid]        = CAST(CASE WHEN n.[Status] = N'PAID' THEN 1 ELSE 0 END AS BIT),
           [IsPrivate]     = CAST(0 AS BIT),
           [Amount]        = amt.[Amount],
           [Fee]           = CAST(COALESCE(
                                 pay.[PawfrontFee],
                                 CASE
                                     WHEN amt.[Amount] IS NULL THEN NULL
                                     ELSE ROUND(amt.[Amount] * @FeePercentage / 100.0, 2)
                                 END) AS DECIMAL(12, 2)),
           [PaidAtUtc]     = pay.[PaidAtUtc],
           [PaymentMethod] = pay.[PaymentMethod]
    FROM [Booking].[NightStayBookings] n
    LEFT JOIN [Booking].[BookingPayments] pay
        ON pay.[BookingType] = N'NightStay'
       AND pay.[BookingId] = n.[NightStayBookingId]
    CROSS APPLY
    (
        SELECT [Amount] = CAST(COALESCE(
            pay.[Amount],
            CASE
                WHEN n.[PricePerNight] IS NULL THEN NULL
                -- Stayed nights = [CheckInDate, CheckOutDate); checkout day isn't billed.
                ELSE ROUND(n.[PricePerNight] * DATEDIFF(DAY, n.[CheckInDate], n.[CheckOutDate]), 2)
            END) AS DECIMAL(12, 2))
    ) amt
    WHERE (@ProviderId IS NULL OR n.[ProviderId] = @ProviderId)
      AND (@PetParentId IS NULL OR n.[PetParentId] = @PetParentId)
);
