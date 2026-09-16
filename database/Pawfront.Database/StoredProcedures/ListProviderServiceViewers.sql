-- Level THREE of the provider's PawPrints "Views" card: WHO looked. One row per
-- pet parent — not per view — because the question the screen asks is "which
-- customers are interested", and a parent who opened the profile six times is
-- one interested customer, not six.
--
-- Returns TWO result sets:
--   1. [TotalCount] — matching parents before paging.
--   2. The page.
--
-- @ServiceId narrows to one service, which is how the drill-down from
-- [Provider].[GetProviderViewSummary]'s second result set works. Omit it for the
-- provider-wide list.
--
-- VIEWS WITH NO [PetParentId] ARE EXCLUDED. A caller browsing before their
-- profile is finished has no identity to show, and a nameless card on a screen
-- whose entire purpose is naming customers is worse than a shorter list. The
-- summary reports those as [AnonymousViews] so the shortfall is explained rather
-- than looking like a bug.
--
-- THE PET COMES FROM THE PARENT'S MOST RECENT VIEW IN RANGE, and is null when
-- none of their views named one. It is deliberately NOT filled in from whatever
-- pets the parent happens to own: a parent with three animals gives no honest
-- answer, and a wrong breed on a card the provider is about to act on is worse
-- than a blank one. When the parent viewed with pet A on Monday and pet B on
-- Friday, Friday's wins — "what they last looked at you for" is both
-- well-defined and the more useful of the two.
--
-- A soft-deleted pet still resolves, so a provider's older analytics keep their
-- meaning; it reads "Deleted Pet" because the anonymisation happens in the row
-- itself. Parent names and photos are likewise joined LIVE, so a parent who
-- deletes their account reads "Deleted User" here rather than leaving their real
-- name behind — the same invariant every other read in this codebase holds.
--
-- Ordered most-recently-viewed first: a provider following up on interest wants
-- the freshest lead at the top. [PetParentId] breaks ties, without which OFFSET
-- paging can repeat or skip parents who share a timestamp.
CREATE OR ALTER PROCEDURE [Provider].[ListProviderServiceViewers]
    @ProviderId UNIQUEIDENTIFIER,
    @ServiceId UNIQUEIDENTIFIER = NULL,
    @FromDate DATE = NULL,
    @ToDate DATE = NULL,
    @Skip INT = 0,
    @Take INT = 20
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ToExclusive DATETIME2(3) =
        CASE WHEN @ToDate IS NULL THEN NULL ELSE DATEADD(DAY, 1, CAST(@ToDate AS DATETIME2(3))) END;
    DECLARE @FromInclusive DATETIME2(3) =
        CASE WHEN @FromDate IS NULL THEN NULL ELSE CAST(@FromDate AS DATETIME2(3)) END;

    -- The matching view rows, once, so the count and the page cannot disagree
    -- about which views are in range.
    WITH [InRange] AS
    (
        SELECT v.[PetParentId],
               v.[PetId],
               v.[ViewedAtUtc]
        FROM [Provider].[ProviderServiceViews] v
        WHERE v.[ProviderId] = @ProviderId
          AND v.[PetParentId] IS NOT NULL
          AND (@ServiceId IS NULL OR v.[ServiceId] = @ServiceId)
          AND (@FromInclusive IS NULL OR v.[ViewedAtUtc] >= @FromInclusive)
          AND (@ToExclusive IS NULL OR v.[ViewedAtUtc] < @ToExclusive)
    )
    SELECT [TotalCount] = COUNT(DISTINCT [PetParentId]) FROM [InRange];

    WITH [InRange] AS
    (
        SELECT v.[PetParentId],
               v.[PetId],
               v.[ViewedAtUtc]
        FROM [Provider].[ProviderServiceViews] v
        WHERE v.[ProviderId] = @ProviderId
          AND v.[PetParentId] IS NOT NULL
          AND (@ServiceId IS NULL OR v.[ServiceId] = @ServiceId)
          AND (@FromInclusive IS NULL OR v.[ViewedAtUtc] >= @FromInclusive)
          AND (@ToExclusive IS NULL OR v.[ViewedAtUtc] < @ToExclusive)
    ),
    [PerParent] AS
    (
        SELECT [PetParentId],
               [ViewCount]         = COUNT(*),
               [FirstViewedAtUtc]  = MIN([ViewedAtUtc]),
               [LastViewedAtUtc]   = MAX([ViewedAtUtc])
        FROM [InRange]
        GROUP BY [PetParentId]
    )
    SELECT p.[PetParentId],
           -- Live-joined, so a deleted account reads its anonymised placeholder.
           [ParentName]       = pp.[FirstName] + N' ' + pp.[LastName],
           [ParentPhotoUrl]   = pp.[ProfilePhotoUrl],
           [PetId]            = lastPet.[PetId],
           [PetName]          = pet.[PetName],
           [PetType]          = pet.[PetType],
           [Breed]            = pet.[Breed],
           [PetGender]        = pet.[Gender],
           p.[ViewCount],
           p.[FirstViewedAtUtc],
           p.[LastViewedAtUtc]
    FROM [PerParent] p
    LEFT JOIN [Parent].[PetParents] pp
        ON pp.[PetParentId] = p.[PetParentId]
    -- The pet named by this parent's most recent view that named one. TOP (1) over
    -- the range rather than a join on [LastViewedAtUtc]: their latest view may have
    -- carried no pet while an earlier one did, and reporting the pet they last
    -- shopped with beats reporting none.
    OUTER APPLY
    (
        SELECT TOP (1) r.[PetId]
        FROM [InRange] r
        WHERE r.[PetParentId] = p.[PetParentId]
          AND r.[PetId] IS NOT NULL
        ORDER BY r.[ViewedAtUtc] DESC
    ) lastPet
    LEFT JOIN [Parent].[Pets] pet
        ON pet.[PetId] = lastPet.[PetId]
    ORDER BY p.[LastViewedAtUtc] DESC, p.[PetParentId]
    OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY;
END;
