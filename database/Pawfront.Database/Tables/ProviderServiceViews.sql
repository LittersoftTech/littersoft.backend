-- One row per time a pet parent looked at a provider — the raw log behind the
-- provider's PawPrints "Views" card, its per-service breakdown, and the list of
-- WHO viewed.
--
-- WHY A LOG AND NOT A COUNTER. [Event].[Events] tracks views as three bare
-- integer columns bumped by [Event].[IncrementEventCounter], and that is all an
-- event needs. It cannot answer the third level of this feature — "which parents
-- viewed my day-care service this month" — because a counter keeps no identity
-- and no timestamp, so it can be neither attributed nor bucketed into a period.
-- Both of those are the whole ask here, so views are stored as rows.
--
-- APPEND-ONLY, and deliberately NOT unique on (provider, service, parent): a
-- parent who looks twice has viewed twice, and refusing the second write would
-- make [ViewCount] and [LastViewedAtUtc] impossible. De-duplication is a
-- reporting concern instead — every read reports raw [TotalViews] alongside
-- DISTINCT [UniqueViewers], so the noisy figure and the robust one travel
-- together and the caller picks. Nothing here rate-limits a client that fires on
-- every re-render; that is the app calling once per screen open.
--
-- [ServiceId] IS NULLABLE, and that is a real case rather than laxity. A parent
-- arriving from the generic discovery card (GET /providers) has picked a provider,
-- not a service — only the five /providers/search/* cards carry a ServiceId. Such
-- a view still happened, so it is counted in the total and reported separately as
-- [UnattributedViews]; the alternative (dropping it) would make the dashboard
-- undercount, and inventing a service for it would make the breakdown lie. This
-- is the same posture [UnpricedBookings] takes in the earnings summary: a total
-- that cannot be fully broken down is visibly incomplete rather than quietly
-- wrong.
--
-- [PetParentId] IS NULLABLE for the same kind of reason. The parent host admits a
-- caller whose profile is not finished yet (/providers/* is not ownership
-- filtered — browsing before onboarding completes is legitimate), and they have
-- no PetParentId to record. Their view counts toward the total; the viewer list
-- skips them, because an unidentifiable viewer is of no use on a screen whose
-- whole purpose is naming customers.
--
-- [PetId] is the pet the parent was SHOPPING FOR — the ?petId= filter the five
-- searches already take — not a guess at which animal they own. That is what
-- lets the viewer list carry the pet's name, breed and gender beside the parent.
-- When the app sends no petId those three read null: a parent with several pets
-- gives no honest answer, and picking one would put a wrong breed on a card the
-- provider is about to act on.
--
-- NO FK on [PetParentId] / [PetId] — the same posture as
-- [Booking].[BookingPayments]. Both parent and pet rows survive a "delete"
-- (deleting a pet parent anonymises in place; deleting a pet soft-deletes), so
-- an FK would never actually be violated, but this is an append-only analytics
-- log rather than a relationship and it must never be able to block a write on
-- the browse path. Viewer names are joined LIVE at read time, so a parent who
-- deletes their account reads "Deleted User" rather than leaving their real name
-- frozen in a provider's analytics — the same invariant the booking reads hold.
CREATE TABLE [Provider].[ProviderServiceViews]
(
    [ProviderServiceViewId] UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT [DF_ProviderServiceViews_ProviderServiceViewId] DEFAULT NEWSEQUENTIALID(),
    [ProviderId] UNIQUEIDENTIFIER NOT NULL,
    -- Which of the provider's bookable services was being looked at.
    -- NULL = the provider's profile, with no service named (see header).
    [ServiceId] UNIQUEIDENTIFIER NULL,
    -- NULL = the viewer had no completed parent profile (see header).
    [PetParentId] UNIQUEIDENTIFIER NULL,
    -- The pet the parent was searching for, when the app named one.
    [PetId] UNIQUEIDENTIFIER NULL,
    -- Where the view came from, so a provider can tell a search impression from a
    -- shared link. Free text on purpose: the vocabulary is the app's, exactly as
    -- [Support].[Tickets].[Category] is, so adding a surface is a mobile release
    -- rather than a migration and an unrecognised value is stored verbatim.
    [Source] NVARCHAR(32) NULL,
    [ViewedAtUtc] DATETIME2(3) NOT NULL
        CONSTRAINT [DF_ProviderServiceViews_ViewedAtUtc] DEFAULT SYSUTCDATETIME(),

    CONSTRAINT [PK_ProviderServiceViews] PRIMARY KEY CLUSTERED ([ProviderServiceViewId] ASC),
    CONSTRAINT [FK_ProviderServiceViews_Providers_ProviderId]
        FOREIGN KEY ([ProviderId]) REFERENCES [Provider].[Providers] ([ProviderId]) ON DELETE CASCADE,
    -- No cascade here: [Provider].[ProviderServices] already cascades from
    -- [Providers], and a second cascade path into this table is rejected outright
    -- by SQL Server. Same shape as [Provider].[ProviderClosures].
    CONSTRAINT [FK_ProviderServiceViews_ProviderServices_ServiceId]
        FOREIGN KEY ([ServiceId]) REFERENCES [Provider].[ProviderServices] ([ServiceId])
);

GO

-- Serves the summary and the provider-wide viewer list: every read is
-- "this provider, this date range".
CREATE INDEX [IX_ProviderServiceViews_Provider_ViewedAt]
    ON [Provider].[ProviderServiceViews] ([ProviderId], [ViewedAtUtc])
    INCLUDE ([ServiceId], [PetParentId], [PetId]);

GO

-- Serves the drill-down: one service's viewers over a date range.
CREATE INDEX [IX_ProviderServiceViews_Service_ViewedAt]
    ON [Provider].[ProviderServiceViews] ([ServiceId], [ViewedAtUtc])
    INCLUDE ([PetParentId], [PetId])
    WHERE [ServiceId] IS NOT NULL;
