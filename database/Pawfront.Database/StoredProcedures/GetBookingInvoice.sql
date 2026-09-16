-- Resolves ONE party's invoice for a booking, for the download endpoints.
--
-- The caller's id is supplied by the host from the JWT (or an ownership-verified
-- route), never from a body, and @Recipient is pinned by the host rather than
-- chosen by the client: the provider host always asks for the 'Provider' invoice
-- and the parent host always for the 'PetParent' one. That is what stops a
-- provider pulling their customer's invoice, or the reverse — the two documents
-- name different parties and carry different money.
--
-- The party test is part of the WHERE clause rather than a check afterwards, so
-- an invoice belonging to somebody else is INDISTINGUISHABLE from one that does
-- not exist. Both return no row and the endpoint answers 404. Same posture as
-- [Provider].[DeactivateProviderDeviceToken] (THROW 51005) and
-- [Support].[GetTicket]: an id must not be probeable.
--
-- Returns at most one row. [Status] travels with it so the endpoint can tell
-- "not paid / never raised" (no row -> 404) apart from "raised, still rendering"
-- (a row with no URL -> 409), which are different things to a client that has
-- just tapped Download.
CREATE OR ALTER PROCEDURE [Billing].[GetBookingInvoice]
    @BookingType NVARCHAR(16),
    @BookingId UNIQUEIDENTIFIER,
    @Recipient NVARCHAR(16),
    @ProviderId UNIQUEIDENTIFIER = NULL,
    @PetParentId UNIQUEIDENTIFIER = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Defensive: with neither party supplied the predicates below would collapse
    -- and return the row unscoped, which is the one failure this procedure exists
    -- to prevent. Both hosts always pass their own side, so this can only fire on
    -- a coding error — and it must fail loudly rather than leak.
    IF @ProviderId IS NULL AND @PetParentId IS NULL
    BEGIN
        THROW 51401, 'An invoice lookup must be scoped to a provider or a pet parent.', 1;
    END

    SELECT [InvoiceId],
           [InvoiceNumber],
           [BookingType],
           [BookingId],
           [Recipient],
           [ProviderId],
           [PetParentId],
           [Amount],
           [PawfrontFee],
           [Status],
           [InvoiceUrl],
           [IssuedAtUtc],
           [GeneratedAtUtc]
    FROM [Billing].[Invoices]
    WHERE [BookingType] = @BookingType
      AND [BookingId] = @BookingId
      AND [Recipient] = @Recipient
      -- Exactly one of the two is supplied, by the calling host. A NULL here
      -- means "this host does not scope on that party", not "match anything":
      -- the other predicate is always present, so the row is still scoped.
      AND (@ProviderId IS NULL OR [ProviderId] = @ProviderId)
      AND (@PetParentId IS NULL OR [PetParentId] = @PetParentId);
END;
