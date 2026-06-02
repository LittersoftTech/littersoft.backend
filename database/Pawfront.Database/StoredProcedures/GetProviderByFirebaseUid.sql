CREATE OR ALTER PROCEDURE [Provider].[GetProviderByFirebaseUid]
    @FirebaseUserId NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    -- Resolves a Firebase user id (sub/user_id claim) to the persisted provider
    -- identity, so the mobile app can re-hydrate state after a reinstall (which
    -- wipes local storage). LEFT JOIN to Providers: the auth identity may exist
    -- without a profile row (mid-onboarding state), in which case ProviderId and
    -- the profile columns come back NULL. Empty result set means no auth identity
    -- exists for this Firebase user.
    SELECT ai.[ProviderAuthIdentityId],
           ai.[ProviderId],
           ai.[FirebaseUserId],
           ai.[Email],
           ai.[IsEmailVerified],
           ai.[DisplayName],
           ai.[SignUpStatus],
           CAST(CASE WHEN p.[ProviderId] IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS [HasProfile],
           p.[OnboardingStatus],
           p.[MobileVerifiedAtUtc],
           p.[IsActive]
    FROM [Provider].[ProviderAuthIdentities] AS ai
    LEFT JOIN [Provider].[Providers] AS p
        ON p.[ProviderId] = ai.[ProviderId]
    WHERE ai.[FirebaseUserId] = @FirebaseUserId;
END;
