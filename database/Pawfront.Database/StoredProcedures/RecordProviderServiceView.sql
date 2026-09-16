-- Records one pet parent looking at one provider. Called from the PARENT host's
-- POST /providers/{providerId}/views; there is no provider-side equivalent,
-- because a provider opening their own profile is not a view.
--
-- Why the write path is validated at all, when it only appends to an analytics
-- log: a [ServiceId] belonging to somebody else would put a stranger's views in
-- this provider's breakdown, and a [PetId] belonging to somebody else would put
-- another parent's pet's breed on this provider's viewer card. Both are wrong in
-- a way a provider would act on, so both are refused rather than silently
-- dropped.
--
-- NOT idempotent and takes no client-supplied id: two views ARE two views (see
-- the table header). No UPDLOCK anywhere — nothing here reads a row it then
-- depends on, and an INSERT into an append-only log cannot race with itself.
-- The service check is a plain EXISTS rather than the UPDLOCK + HOLDLOCK the
-- booking creates use: a service deactivated in the same instant makes this view
-- no less real, so serialising against that would buy contention on the busiest
-- write in the product for nothing.
--
-- @ServiceId / @PetParentId / @PetId are all optional; see the table header for
-- what each NULL legitimately means.
CREATE OR ALTER PROCEDURE [Provider].[RecordProviderServiceView]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceId UNIQUEIDENTIFIER = NULL,
    @PetParentId UNIQUEIDENTIFIER = NULL,
    @PetId UNIQUEIDENTIFIER = NULL,
    @Source NVARCHAR(32) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- A view of a provider who does not exist is a client bug, not data.
    IF NOT EXISTS (SELECT 1 FROM [Provider].[Providers] WHERE [ProviderId] = @ProviderId)
    BEGIN
        THROW 51410, 'Provider profile was not found.', 1;
    END;

    -- An INACTIVE or deleted provider is deliberately still recordable. Inactive
    -- means "not taking bookings" — they are out of discovery but their profile is
    -- still reachable by deep link, and a view of it is still a view they should
    -- see when they switch back on. Same carve-out chat makes.
    IF @ServiceId IS NOT NULL AND NOT EXISTS (
        SELECT 1
        FROM [Provider].[ProviderServices]
        WHERE [ServiceId] = @ServiceId
          AND [ProviderId] = @ProviderId
    )
    BEGIN
        THROW 51411, 'Service is not valid for this provider.', 1;
    END;

    -- The pet must be one the caller actually has. A soft-deleted pet is refused
    -- for the same reason a booking refuses one: the row survives to keep existing
    -- bookings readable, it is not a pet they still own.
    IF @PetId IS NOT NULL AND NOT EXISTS (
        SELECT 1
        FROM [Parent].[Pets]
        WHERE [PetId] = @PetId
          AND [PetParentId] = @PetParentId
          AND [IsDeleted] = 0
    )
    BEGIN
        THROW 51412, 'Pet was not found or does not belong to the pet parent.', 1;
    END;

    DECLARE @Inserted TABLE ([ProviderServiceViewId] UNIQUEIDENTIFIER);

    INSERT INTO [Provider].[ProviderServiceViews]
        ([ProviderId], [ServiceId], [PetParentId], [PetId], [Source])
    OUTPUT inserted.[ProviderServiceViewId] INTO @Inserted
    VALUES
        (@ProviderId, @ServiceId, @PetParentId, @PetId, NULLIF(LTRIM(RTRIM(@Source)), N''));

    SELECT v.[ProviderServiceViewId],
           v.[ProviderId],
           v.[ServiceId],
           v.[PetParentId],
           v.[PetId],
           v.[Source],
           v.[ViewedAtUtc]
    FROM [Provider].[ProviderServiceViews] v
    WHERE v.[ProviderServiceViewId] IN (SELECT [ProviderServiceViewId] FROM @Inserted);
END;
