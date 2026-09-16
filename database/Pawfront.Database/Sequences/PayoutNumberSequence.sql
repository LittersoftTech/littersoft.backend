-- Mints the human-facing payout reference stamped onto a booking when its job is
-- completed ([Booking].[Bookings].[PayoutId] / [Booking].[NightStayBookings].[PayoutId],
-- formatted 'PO-000123').
--
-- A SEQUENCE rather than a per-table IDENTITY because a payout reference has to be
-- unique across BOTH booking kinds: single-day and night-stay bookings live in
-- separate tables but share one payout namespace, exactly as they share the
-- [Booking].[BookingPayments] ledger. Two IDENTITY columns would collide.
--
-- Distinct from [JobNumber] ('PF-000123'), which numbers the JOB. A job that is
-- never completed never earns a payout reference, so the two sequences drift apart
-- by design and must not be conflated.
CREATE SEQUENCE [Booking].[PayoutNumberSequence]
    AS BIGINT
    START WITH 1
    INCREMENT BY 1
    NO CYCLE
    CACHE 50;
