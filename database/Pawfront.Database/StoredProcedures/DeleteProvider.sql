-- Backs the provider app's "Delete account" action as an ANONYMISE + DISABLE,
-- not a row delete. The ProviderId is deliberately kept so everything that
-- references it keeps its meaning: bookings, night-stay bookings, their audit /
-- evidence / OTP / modification / prescription children, the events the provider
-- organised with their ticket bookings, and the [Booking].[BookingPayments]
-- ledger are ALL retained untouched. Deleting them would destroy both parties'
-- history — a pet parent's past bookings reference this provider too.
--
-- What the sproc does, in one transaction:
--   1. Scrubs the personal fields on [Provider].[Providers] — name becomes
--      'Deleted Provider', gender/DOB/mobile/banner are replaced or cleared —
--      and sets IsActive = 0 + IsDeleted = 1 + DeletedAtUtc. IsActive = 0 is
--      what stops new bookings ([Booking].[CreateBooking] THROWs 51067 ->
--      409 ProviderInactive); IsDeleted = 1 is permanent and additionally blocks
--      reactivation and profile edits (THROW 51115).
--   2. Scrubs the Firebase link on [Provider].[ProviderAuthIdentities] so the
--      account can never be signed into again. FirebaseUserId is UNIQUE, so
--      replacing it also FREES the real Firebase uid — signing up again creates a
--      brand-new identity and a brand-new ProviderId. The mobile number is
--      replaced for the same reason: (MobileCountryCode, MobileNumber) is UNIQUE,
--      so the real number becomes available for re-registration.
--   3. Deactivates the provider's [Provider].[ProviderServices] rows. The rows
--      themselves must stay (bookings FK to ServiceId), but IsActive = 0 removes
--      them from the catalog and therefore from all five parent-facing booking
--      searches, which require an ACTIVE service row.
--   4. Deletes only what is operational or pure PII and carries no history:
--      device tokens (stop push), mobile OTPs, gallery photos, per-service
--      banners, weekly availability, closures, cancellation policy, payout
--      methods, and the service registration (the "I offer this" declaration,
--      and the row that makes the provider readable at
--      GET /providers/{providerId}). The booking-level cancellation policy is
--      unaffected — it was snapshotted onto each booking at creation.
--
-- All the placeholder values are derived from @ProviderId, so they are stable:
-- re-running on an already-deleted provider is a no-op that returns the original
-- DeletedAtUtc (@WasAlreadyDeleted = 1) instead of churning new placeholders.
--
-- Returns three result sets, since SQL cannot reach Cosmos or Blob Storage:
--   1. summary — ProviderId, DeletedAtUtc, WasAlreadyDeleted + deactivated /
--                retained counts (the retained counts document, in the response,
--                that history survived)
--   2. service categories — partition key(s) of the Cosmos [ProviderServices]
--      offering doc to remove. That document is the provider's public service
--      LISTING (business name, prices, photos, address) and is what makes them
--      appear in Cosmos-backed discovery, so it must not outlive the account.
--      The Cosmos [Events] docs are NOT returned — the events are retained, so
--      their venue/capacity extension docs stay.
--   3. blob URLs — provider banner, gallery photos, per-service banners. Event
--      banners and booking evidence are NOT returned: those belong to records
--      that are being kept.
--
-- THROW 51114 = provider profile not found (account delete).
CREATE OR ALTER PROCEDURE [Provider].[DeleteProvider]
    @ProviderId UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();
    DECLARE @ProviderAuthIdentityId UNIQUEIDENTIFIER;
    DECLARE @IsDeleted BIT;
    DECLARE @DeletedAtUtc DATETIME2(7);
    DECLARE @WasAlreadyDeleted BIT = 0;

    -- Captured before the scrub so the caller can clean the other stores.
    DECLARE @ServiceCategories TABLE ([ServiceCategory] NVARCHAR(64) NOT NULL);
    DECLARE @BlobUrls TABLE
    (
        [BlobUrl] NVARCHAR(1000) NOT NULL,
        [Kind] NVARCHAR(32) NOT NULL
    );

    DECLARE @DeactivatedServiceCount INT = 0;

    BEGIN TRANSACTION;

    -- UPDLOCK + HOLDLOCK: serialise against a concurrent booking create or
    -- active-status toggle on this provider.
    SELECT @ProviderAuthIdentityId = [ProviderAuthIdentityId],
           @IsDeleted = [IsDeleted],
           @DeletedAtUtc = [DeletedAtUtc]
    FROM [Provider].[Providers] WITH (UPDLOCK, HOLDLOCK)
    WHERE [ProviderId] = @ProviderId;

    -- XACT_ABORT ON rolls the transaction back on THROW.
    IF @ProviderAuthIdentityId IS NULL
    BEGIN
        THROW 51114, 'Provider profile was not found.', 1;
    END

    IF @IsDeleted = 1
    BEGIN
        -- Idempotent: already anonymised. Report the original timestamp and skip
        -- straight to the result sets (nothing left to clean up out of SQL).
        SET @WasAlreadyDeleted = 1;
        SET @Now = ISNULL(@DeletedAtUtc, @Now);
    END
    ELSE
    BEGIN
        INSERT INTO @ServiceCategories ([ServiceCategory])
        SELECT [ServiceCategory]
        FROM [Provider].[ProviderServiceRegistrations]
        WHERE [ProviderId] = @ProviderId;

        INSERT INTO @BlobUrls ([BlobUrl], [Kind])
        SELECT [BannerImageUrl], N'ProviderBanner'
        FROM [Provider].[Providers]
        WHERE [ProviderId] = @ProviderId AND [BannerImageUrl] IS NOT NULL
        UNION ALL
        SELECT [PhotoUrl], N'ProviderPhoto'
        FROM [Provider].[ProviderPhotos]
        WHERE [ProviderId] = @ProviderId
        UNION ALL
        SELECT [BannerImageUrl], N'ServiceBanner'
        FROM [Provider].[ProviderServiceBanners]
        WHERE [ProviderId] = @ProviderId;

        ------------------------------------------------------------------
        -- 1. Anonymise the profile row and disable the account.
        --    The mobile placeholder is derived from the ProviderId so it stays
        --    unique under UX_Providers_MobileNumber, and frees the real number.
        ------------------------------------------------------------------
        UPDATE [Provider].[Providers]
        SET [FirstName] = N'Deleted',
            [LastName] = N'Provider',
            [Gender] = N'PreferNotToSay',
            [DateOfBirth] = '1900-01-01',
            [MobileCountryCode] = N'+00',
            [MobileNumber] = N'DEL' + LEFT(REPLACE(CONVERT(NVARCHAR(36), @ProviderId), N'-', N''), 29),
            [MobileVerifiedAtUtc] = NULL,
            [BannerImageUrl] = NULL,
            [IsActive] = 0,
            [IsDeleted] = 1,
            [DeletedAtUtc] = @Now,
            [UpdatedAtUtc] = @Now
        WHERE [ProviderId] = @ProviderId;

        ------------------------------------------------------------------
        -- 2. Sever the Firebase login. FirebaseUserId is UNIQUE, so replacing it
        --    frees the real uid for a fresh sign-up. The ProviderId link is kept
        --    for audit — the row is unreachable by any real Firebase user now.
        ------------------------------------------------------------------
        UPDATE [Provider].[ProviderAuthIdentities]
        SET [FirebaseUserId] = N'deleted:' + CONVERT(NVARCHAR(36), @ProviderId),
            [FirebaseTenantId] = NULL,
            [Email] = N'deleted+' + CONVERT(NVARCHAR(36), @ProviderId) + N'@deleted.invalid',
            [IsEmailVerified] = 0,
            [DisplayName] = NULL,
            [FirebasePhoneNumber] = NULL,
            [PhotoUrl] = NULL,
            [UpdatedAtUtc] = @Now
        WHERE [ProviderAuthIdentityId] = @ProviderAuthIdentityId;

        ------------------------------------------------------------------
        -- 3. Deactivate the bookable services. The rows stay — bookings FK to
        --    ServiceId — but an inactive service is out of the catalog and out
        --    of all five parent-facing searches.
        ------------------------------------------------------------------
        UPDATE [Provider].[ProviderServices]
        SET [IsActive] = 0,
            [UpdatedAtUtc] = @Now
        WHERE [ProviderId] = @ProviderId
          AND [IsActive] = 1;
        SET @DeactivatedServiceCount = @@ROWCOUNT;

        ------------------------------------------------------------------
        -- 4. Remove operational config + media only. Nothing here carries
        --    historical meaning: each booking already froze the cancellation
        --    policy it was created under.
        ------------------------------------------------------------------
        DELETE FROM [Provider].[ProviderDeviceTokens]
        WHERE [ProviderId] = @ProviderId
           OR [ProviderAuthIdentityId] = @ProviderAuthIdentityId;

        DELETE FROM [Provider].[ProviderMobileOtps] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderPhotos] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderServiceBanners] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderWeeklyAvailability] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderClosures] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderCancellationPolicies] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderPayoutMethods] WHERE [ProviderId] = @ProviderId;
        DELETE FROM [Provider].[ProviderServiceRegistrations] WHERE [ProviderId] = @ProviderId;
    END

    -- Result set 1: summary. The retained counts are reported so the caller can
    -- see that history survived the delete.
    SELECT @ProviderId AS [ProviderId],
           @Now AS [DeletedAtUtc],
           @WasAlreadyDeleted AS [WasAlreadyDeleted],
           @DeactivatedServiceCount AS [DeactivatedServiceCount],
           (SELECT COUNT(*) FROM [Booking].[Bookings] WHERE [ProviderId] = @ProviderId)
               AS [RetainedBookingCount],
           (SELECT COUNT(*) FROM [Booking].[NightStayBookings] WHERE [ProviderId] = @ProviderId)
               AS [RetainedNightStayBookingCount],
           (SELECT COUNT(*) FROM [Event].[Events] WHERE [ProviderId] = @ProviderId)
               AS [RetainedEventCount],
           (SELECT COUNT(*) FROM [Booking].[BookingPayments] WHERE [ProviderId] = @ProviderId)
               AS [RetainedPaymentCount];

    -- Result set 2: Cosmos [ProviderServices] partition keys (the public listing).
    SELECT [ServiceCategory] FROM @ServiceCategories;

    -- Result set 3: blob URLs to delete best-effort.
    SELECT [BlobUrl], [Kind] FROM @BlobUrls;

    COMMIT TRANSACTION;
END;
