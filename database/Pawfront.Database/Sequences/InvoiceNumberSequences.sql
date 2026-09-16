-- Mints the two invoice references. SEQUENCEs rather than per-table IDENTITYs
-- for the same reason [Booking].[PayoutNumberSequence] is one: single-day and
-- night-stay bookings live in separate tables but share ONE invoice namespace,
-- exactly as they share the [Booking].[BookingPayments] ledger.
--
-- TWO sequences, not one, because the two documents are issued by two different
-- legal entities and each keeps its own book:
--   [ParentInvoiceNumberSequence]   -> 'PF-INV-<year>-<n>', the provider's
--                                      invoice to the pet parent
--   [ProviderInvoiceNumberSequence] -> 'LS-INV-<year>-<n>', Littersoft GmbH's
--                                      fee invoice to the provider
--
-- Neither resets annually. The year in the formatted reference is stamped from
-- the issue date, so a number is unique for the life of the system and a January
-- rollover needs no locking, no per-year counter table and no midnight-UTC edge
-- case. Accounting can still group by the year segment.
IF NOT EXISTS (SELECT 1 FROM sys.sequences
               WHERE [name] = N'ParentInvoiceNumberSequence' AND [schema_id] = SCHEMA_ID(N'Billing'))
BEGIN
    CREATE SEQUENCE [Billing].[ParentInvoiceNumberSequence]
        AS BIGINT START WITH 1 INCREMENT BY 1 NO CYCLE CACHE 50;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.sequences
               WHERE [name] = N'ProviderInvoiceNumberSequence' AND [schema_id] = SCHEMA_ID(N'Billing'))
BEGIN
    CREATE SEQUENCE [Billing].[ProviderInvoiceNumberSequence]
        AS BIGINT START WITH 1 INCREMENT BY 1 NO CYCLE CACHE 50;
END
GO
