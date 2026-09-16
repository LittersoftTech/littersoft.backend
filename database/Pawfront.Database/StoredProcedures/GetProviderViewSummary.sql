-- Levels ONE and TWO of the provider's PawPrints "Views" card: the headline
-- figure for a date range, and the per-service breakdown behind it. The third
-- level (who viewed) is [Provider].[ListProviderServiceViewers].
--
-- Returns TWO result sets:
--   1. The totals for the range.
--   2. One row per service the provider offers.
--
-- BOTH IN ONE CALL rather than two endpoints, so the card and the breakdown it
-- expands into are computed from one range and one read — two calls could
-- straddle midnight UTC and show a total that does not match its own breakdown.
-- Same reasoning as [Booking].[GetProviderEarningsSummary] being one sproc behind
-- both the overview and the period endpoint.
--
-- RESULT SET 2 IS DRIVEN FROM [Provider].[ProviderServices], NOT from the view
-- log, so a service with zero views in range still appears with 0. A breakdown
-- that silently omitted an unviewed service would read as "no such service"
-- rather than "nobody looked", which is the more useful fact and the one a
-- provider needs to act on. Inactive services are included and flagged: their
-- historical views are real, and a provider comparing periods needs the service
-- they switched off to still be in the list.
--
-- THE TWO RESULT SETS DO NOT SUM TO EACH OTHER, on purpose:
--   * [TotalViews] counts every row, including views that named no service.
--     Those are reported as [UnattributedViews] so the gap is visible and
--     explained rather than looking like an arithmetic error — the same posture
--     [UnpricedBookings] takes in the earnings summary.
--   * [UniqueViewers] is DISTINCT over the WHOLE range, so it is NOT the sum of
--     the per-service [UniqueViewers] either: one parent who looked at day care
--     and at boarding is one viewer and two service-viewers. Summing distinct
--     counts is never valid; both figures are correct answers to different
--     questions.
--
-- @FromDate / @ToDate are inclusive calendar dates (both NULL = all time),
-- resolved from the API's calendar-aligned period the same way the earnings
-- reads are. The predicate is half-open on the upper bound
-- (< @ToDate + 1 day) rather than CAST(... AS DATE) so the index on
-- ([ProviderId], [ViewedAtUtc]) is still used.
CREATE OR ALTER PROCEDURE [Provider].[GetProviderViewSummary]
    @ProviderId UNIQUEIDENTIFIER,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ToExclusive DATETIME2(3) =
        CASE WHEN @ToDate IS NULL THEN NULL ELSE DATEADD(DAY, 1, CAST(@ToDate AS DATETIME2(3))) END;
    DECLARE @FromInclusive DATETIME2(3) =
        CASE WHEN @FromDate IS NULL THEN NULL ELSE CAST(@FromDate AS DATETIME2(3)) END;

    -- Result set 1: the headline card.
    SELECT [TotalViews]        = COUNT(*),
           [UniqueViewers]     = COUNT(DISTINCT v.[PetParentId]),
           -- Views that named no service — counted in the total, absent from the
           -- breakdown. See header.
           [UnattributedViews] = COUNT(CASE WHEN v.[ServiceId] IS NULL THEN 1 END),
           -- Views by a caller with no completed parent profile. They cannot appear
           -- in the viewer list, so the count is reported to explain why that list
           -- can be shorter than [UniqueViewers] would suggest.
           [AnonymousViews]    = COUNT(CASE WHEN v.[PetParentId] IS NULL THEN 1 END),
           [LastViewedAtUtc]   = MAX(v.[ViewedAtUtc])
    FROM [Provider].[ProviderServiceViews] v
    WHERE v.[ProviderId] = @ProviderId
      AND (@FromInclusive IS NULL OR v.[ViewedAtUtc] >= @FromInclusive)
      AND (@ToExclusive IS NULL OR v.[ViewedAtUtc] < @ToExclusive);

    -- Result set 2: per-service breakdown, every service the provider has.
    SELECT s.[ServiceId],
           s.[ServiceCategory],
           s.[SubCategory],
           s.[ServiceType],
           s.[IsActive],
           [Views]           = ISNULL(agg.[Views], 0),
           [UniqueViewers]   = ISNULL(agg.[UniqueViewers], 0),
           [LastViewedAtUtc] = agg.[LastViewedAtUtc]
    FROM [Provider].[ProviderServices] s
    OUTER APPLY
    (
        SELECT [Views]           = COUNT(*),
               [UniqueViewers]   = COUNT(DISTINCT v.[PetParentId]),
               [LastViewedAtUtc] = MAX(v.[ViewedAtUtc])
        FROM [Provider].[ProviderServiceViews] v
        WHERE v.[ServiceId] = s.[ServiceId]
          AND (@FromInclusive IS NULL OR v.[ViewedAtUtc] >= @FromInclusive)
          AND (@ToExclusive IS NULL OR v.[ViewedAtUtc] < @ToExclusive)
    ) agg
    WHERE s.[ProviderId] = @ProviderId
    -- Active services first (what the provider is selling now), then most-viewed,
    -- then a stable tie-break so paging-free clients still render consistently.
    ORDER BY s.[IsActive] DESC, ISNULL(agg.[Views], 0) DESC, s.[ServiceType];
END;
