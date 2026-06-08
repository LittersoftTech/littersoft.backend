CREATE OR ALTER PROCEDURE [Parent].[GetPetParentByFirebaseUid]
    @FirebaseUserId NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    -- Resolves a Firebase user id (sub/user_id claim) to the persisted pet-parent
    -- identity, so the mobile app can re-hydrate state after a reinstall (which
    -- wipes local storage). LEFT JOIN to PetParents: the auth identity may exist
    -- without a profile row (mid-onboarding state), in which case PetParentId and
    -- the profile columns come back NULL. Empty result set means no auth identity
    -- exists for this Firebase user.
    SELECT ai.[ParentAuthIdentityId],
           ai.[PetParentId],
           ai.[FirebaseUserId],
           ai.[Email],
           ai.[IsEmailVerified],
           ai.[DisplayName],
           ai.[SignUpStatus],
           CAST(CASE WHEN p.[PetParentId] IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS [HasProfile],
           p.[MobileVerifiedAtUtc]
    FROM [Parent].[ParentAuthIdentities] AS ai
    LEFT JOIN [Parent].[PetParents] AS p
        ON p.[PetParentId] = ai.[PetParentId]
    WHERE ai.[FirebaseUserId] = @FirebaseUserId;
END;
