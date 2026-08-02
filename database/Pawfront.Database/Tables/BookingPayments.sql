-- One payment record per paid booking — written when the provider marks a
-- COMPLETED booking as PAID (the parent has paid the provider). This is a
-- financial ledger: rows are keyed by (BookingType, BookingId) and deliberately
-- have NO FK to the booking tables, so the payment history survives even if a
-- booking row is later removed. It is the source for "total payment received by
-- a provider" reports (SUM([Amount]) / SUM([Amount] - [PawfrontFee]) filtered by
-- [ProviderId]).
--
-- [BookingType] discriminates which booking table [BookingId] points at:
-- 'SingleDay' -> [Booking].[Bookings], 'NightStay' -> [Booking].[NightStayBookings].
-- [Amount] is the price-locked total the parent paid (snapshot rate x quantity,
-- computed server-side at PAID time); [PawfrontFee] is the platform commission on
-- that amount at the time of payment (provider net = Amount - PawfrontFee).
-- Only App bookings can be paid (Custom walk-ins are off-platform), so
-- [PetParentId] is always set.
CREATE TABLE [Booking].[BookingPayments]
(
    [BookingPaymentId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_BookingPayments_Id] DEFAULT NEWSEQUENTIALID(),
    [BookingType] NVARCHAR(16) NOT NULL,
    [BookingId] UNIQUEIDENTIFIER NOT NULL,
    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
    [PetParentId] UNIQUEIDENTIFIER NOT NULL,
    [Amount] DECIMAL(10, 2) NOT NULL,
    [PawfrontFee] DECIMAL(10, 2) NOT NULL
        CONSTRAINT [DF_BookingPayments_PawfrontFee] DEFAULT 0,
    -- How the parent paid the provider: 'Cash' or 'Digital' (same vocabulary as
    -- the provider's payout methods).
    [PaymentMethod] NVARCHAR(16) NOT NULL,
    [PaidAtUtc] DATETIME2(7) NOT NULL
        CONSTRAINT [DF_BookingPayments_PaidAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_BookingPayments] PRIMARY KEY CLUSTERED ([BookingPaymentId] ASC),
    -- A booking is paid at most once.
    CONSTRAINT [UQ_BookingPayments_Booking] UNIQUE ([BookingType], [BookingId]),
    CONSTRAINT [CK_BookingPayments_BookingType]
        CHECK ([BookingType] IN (N'SingleDay', N'NightStay')),
    CONSTRAINT [CK_BookingPayments_PaymentMethod]
        CHECK ([PaymentMethod] IN (N'Cash', N'Digital')),
    CONSTRAINT [CK_BookingPayments_Amount] CHECK ([Amount] >= 0),
    CONSTRAINT [CK_BookingPayments_PawfrontFee] CHECK ([PawfrontFee] >= 0)
);

GO

-- Drives the per-provider "total received" aggregate.
CREATE INDEX [IX_BookingPayments_Provider]
    ON [Booking].[BookingPayments] ([ProviderId])
    INCLUDE ([Amount], [PawfrontFee], [PaidAtUtc]);
