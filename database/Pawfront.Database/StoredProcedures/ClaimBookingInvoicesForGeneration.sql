-- Claims a paid booking's unrendered invoices and returns everything the PDF
-- renderer needs from SQL, in one round trip.
--
-- The queue message carries the BOOKING, not an invoice, so one message renders
-- both documents for that job — they describe the same service from two sides and
-- their figures must agree, which is easiest to guarantee when one call assembles
-- them from one read.
--
-- Returns TWO result sets:
--   1. the booking + parties render payload (one row; empty if the booking is
--      unknown or was never paid)
--   2. the invoice rows this call claimed (0-2 — only those still awaiting a
--      render whose lease has lapsed)
--
-- Claiming is lease-based, exactly like [Notification].[ClaimPendingNotifications]:
-- rows flip to 'Generating' and [NextAttemptAtUtc] is pushed forward, so a
-- renderer that dies mid-job releases its work automatically instead of stranding
-- it. An empty second result set means "already rendered, or somebody else holds
-- it" — both of which mean this message has nothing to do, so it should be
-- dequeued rather than retried.
--
-- What is deliberately NOT here: the provider's BUSINESS name and address. Those
-- live in the Cosmos offering document, not SQL, so the renderer reads them
-- separately using [ServiceCategory] as the partition key. Returning the category
-- here is what makes that a point read rather than a scan.
CREATE OR ALTER PROCEDURE [Billing].[ClaimBookingInvoicesForGeneration]
    @BookingType NVARCHAR(16),
    @BookingId UNIQUEIDENTIFIER,
    @MaxAttempts INT = 5,
    @LeaseMinutes INT = 5
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @Claimed TABLE ([InvoiceId] UNIQUEIDENTIFIER PRIMARY KEY);

    BEGIN TRANSACTION;

    -- Claim first, under UPDLOCK, so two renderers racing on a redelivered
    -- message cannot both take the same invoice.
    UPDATE [inv]
    SET [Status] = N'Generating',
        [AttemptCount] = [inv].[AttemptCount] + 1,
        [NextAttemptAtUtc] = DATEADD(MINUTE, @LeaseMinutes, @Now),
        [UpdatedAtUtc] = @Now
    OUTPUT [inserted].[InvoiceId] INTO @Claimed ([InvoiceId])
    FROM [Billing].[Invoices] AS [inv] WITH (UPDLOCK, ROWLOCK)
    WHERE [inv].[BookingType] = @BookingType
      AND [inv].[BookingId] = @BookingId
      AND [inv].[Status] IN (N'Pending', N'Generating')
      AND [inv].[NextAttemptAtUtc] <= @Now
      AND [inv].[AttemptCount] < @MaxAttempts;

    -- Result set 1 — the shared render payload.
    IF @BookingType = N'SingleDay'
    BEGIN
        SELECT [BookingId]            = b.[BookingId],
               [BookingType]          = N'SingleDay',
               [JobId]                = N'PF-' + FORMAT(b.[JobNumber], N'D6'),
               [ProviderId]           = b.[ProviderId],
               [PetParentId]          = b.[PetParentId],
               [ServiceCategory]      = b.[ServiceCategory],
               [SubCategory]          = b.[SubCategory],
               [ServiceItemCode]      = b.[ServiceItemCode],
               [ServiceDate]          = b.[BookingDate],
               [EndDate]              = CAST(NULL AS DATE),
               [StartTime]            = b.[StartTime],
               [EndTime]              = b.[EndTime],
               [UnitPrice]            = b.[PricePerHour],
               -- Provider (personal record only — the business identity is Cosmos)
               [ProviderFirstName]    = prov.[FirstName],
               [ProviderLastName]     = prov.[LastName],
               [ProviderMobileCountryCode] = prov.[MobileCountryCode],
               [ProviderMobileNumber] = prov.[MobileNumber],
               [ProviderEmail]        = pai.[Email],
               -- Pet parent
               [ParentFirstName]      = pp.[FirstName],
               [ParentLastName]       = pp.[LastName],
               [ParentAddressLine]    = pp.[AddressLine],
               [ParentCity]           = pp.[City],
               [ParentZipCode]        = pp.[ZipCode],
               [ParentEmail]          = pai2.[Email],
               -- Pet. COALESCE because a Custom walk-in stores the pet's name on
               -- the booking itself, though one can never reach PAID today.
               [PetName]              = COALESCE(pet.[PetName], b.[PetName]),
               [PetType]              = COALESCE(pet.[PetType], b.[AnimalType]),
               [PetBreed]             = pet.[Breed],
               -- Payment
               [PaidAtUtc]            = pay.[PaidAtUtc],
               [PaymentMethod]        = pay.[PaymentMethod],
               -- The bookable service's type (DayCare / GroomingSession / ...),
               -- which is what names the line item. Appended LAST so the reader's
               -- existing ordinals do not move.
               [ServiceType]          = svc.[ServiceType]
        FROM [Booking].[Bookings] AS b
        LEFT JOIN [Provider].[Providers] AS prov
            ON prov.[ProviderId] = b.[ProviderId]
        LEFT JOIN [Provider].[ProviderAuthIdentities] AS pai
            ON pai.[ProviderAuthIdentityId] = prov.[ProviderAuthIdentityId]
        LEFT JOIN [Parent].[PetParents] AS pp
            ON pp.[PetParentId] = b.[PetParentId]
        LEFT JOIN [Parent].[ParentAuthIdentities] AS pai2
            ON pai2.[ParentAuthIdentityId] = pp.[ParentAuthIdentityId]
        LEFT JOIN [Parent].[Pets] AS pet
            ON pet.[PetId] = b.[PetId]
        LEFT JOIN [Booking].[BookingPayments] AS pay
            ON pay.[BookingId] = b.[BookingId] AND pay.[BookingType] = N'SingleDay'
        LEFT JOIN [Provider].[ProviderServices] AS svc
            ON svc.[ServiceId] = b.[ServiceId]
        WHERE b.[BookingId] = @BookingId;
    END
    ELSE
    BEGIN
        SELECT [BookingId]            = b.[NightStayBookingId],
               [BookingType]          = N'NightStay',
               [JobId]                = N'PF-' + FORMAT(b.[JobNumber], N'D6'),
               [ProviderId]           = b.[ProviderId],
               [PetParentId]          = b.[PetParentId],
               [ServiceCategory]      = b.[ServiceCategory],
               [SubCategory]          = b.[SubCategory],
               [ServiceItemCode]      = CAST(NULL AS NVARCHAR(64)),
               [ServiceDate]          = b.[CheckInDate],
               [EndDate]              = b.[CheckOutDate],
               [StartTime]            = b.[DropOffTime],
               [EndTime]              = b.[PickUpTime],
               [UnitPrice]            = b.[PricePerNight],
               [ProviderFirstName]    = prov.[FirstName],
               [ProviderLastName]     = prov.[LastName],
               [ProviderMobileCountryCode] = prov.[MobileCountryCode],
               [ProviderMobileNumber] = prov.[MobileNumber],
               [ProviderEmail]        = pai.[Email],
               [ParentFirstName]      = pp.[FirstName],
               [ParentLastName]       = pp.[LastName],
               [ParentAddressLine]    = pp.[AddressLine],
               [ParentCity]           = pp.[City],
               [ParentZipCode]        = pp.[ZipCode],
               [ParentEmail]          = pai2.[Email],
               [PetName]              = pet.[PetName],
               [PetType]              = pet.[PetType],
               [PetBreed]             = pet.[Breed],
               [PaidAtUtc]            = pay.[PaidAtUtc],
               [PaymentMethod]        = pay.[PaymentMethod],
               [ServiceType]          = svc.[ServiceType]
        FROM [Booking].[NightStayBookings] AS b
        LEFT JOIN [Provider].[Providers] AS prov
            ON prov.[ProviderId] = b.[ProviderId]
        LEFT JOIN [Provider].[ProviderAuthIdentities] AS pai
            ON pai.[ProviderAuthIdentityId] = prov.[ProviderAuthIdentityId]
        LEFT JOIN [Parent].[PetParents] AS pp
            ON pp.[PetParentId] = b.[PetParentId]
        LEFT JOIN [Parent].[ParentAuthIdentities] AS pai2
            ON pai2.[ParentAuthIdentityId] = pp.[ParentAuthIdentityId]
        LEFT JOIN [Parent].[Pets] AS pet
            ON pet.[PetId] = b.[PetId]
        LEFT JOIN [Booking].[BookingPayments] AS pay
            ON pay.[BookingId] = b.[NightStayBookingId] AND pay.[BookingType] = N'NightStay'
        LEFT JOIN [Provider].[ProviderServices] AS svc
            ON svc.[ServiceId] = b.[ServiceId]
        WHERE b.[NightStayBookingId] = @BookingId;
    END

    -- Result set 2 — what this call actually claimed.
    SELECT [inv].[InvoiceId],
           [inv].[InvoiceNumber],
           [inv].[Recipient],
           [inv].[Amount],
           [inv].[PawfrontFee],
           [inv].[IssuedAtUtc],
           [inv].[AttemptCount]
    FROM [Billing].[Invoices] AS [inv]
    INNER JOIN @Claimed AS c ON c.[InvoiceId] = [inv].[InvoiceId]
    ORDER BY [inv].[Recipient];

    COMMIT TRANSACTION;
END;
