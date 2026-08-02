-- Creates the [Parent].[PetParents] profile row behind
-- POST /parent-onboarding/profile, linking it to the caller's auth identity.
-- Idempotent: an identity that already has a profile gets its existing row back.
--
-- A mobile number can back only ONE account. That is enforced twice: an explicit
-- pre-check here (THROW 51222 -> 409 MobileNumberAlreadyExists), which is what
-- gives the caller a clean, deterministic error, and the UNIQUE index
-- UX_PetParents_MobileNumber, which is the race-safe backstop for two
-- registrations landing at once. The pre-check reads under UPDLOCK + HOLDLOCK so
-- concurrent completions on the same number serialise rather than both passing.
--
-- THROW 51200 = parent auth identity not found; 51222 = mobile already registered.
CREATE OR ALTER PROCEDURE [Parent].[CompletePetParentProfile]
    -- The auth identity is resolved server-side from the caller's Firebase
    -- user id (sub/user_id claim) rather than trusting the client. The
    -- endpoint never accepts a ParentAuthIdentityId in the body — this
    -- closes the gap where a malicious caller could complete a different
    -- user's profile by guessing the id.
    @FirebaseUserId NVARCHAR(128),
    @FirstName NVARCHAR(100),
    @LastName NVARCHAR(100),
    @Gender NVARCHAR(32),
    @MobileCountryCode NVARCHAR(8),
    @MobileNumber NVARCHAR(32),
    @DateOfBirth DATE,
    @AddressLine NVARCHAR(500),
    @Latitude DECIMAL(9, 6),
    @Longitude DECIMAL(9, 6),
    @ZipCode NVARCHAR(16),
    @City NVARCHAR(100),
    @Description NVARCHAR(2000)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ParentAuthIdentityId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @ParentAuthIdentityId = [ParentAuthIdentityId],
           @PetParentId = [PetParentId]
    FROM [Parent].[ParentAuthIdentities] WITH (UPDLOCK, HOLDLOCK)
    WHERE [FirebaseUserId] = @FirebaseUserId;

    IF @ParentAuthIdentityId IS NULL
    BEGIN
        THROW 51200, 'Parent auth identity was not found.', 1;
    END

    IF @PetParentId IS NULL
    BEGIN
        -- The number is only free if no OTHER parent already holds it. UPDLOCK +
        -- HOLDLOCK takes a range lock on the (MobileCountryCode, MobileNumber)
        -- key so a concurrent completion of the same number waits here instead of
        -- reading "free" at the same instant.
        IF EXISTS (
            SELECT 1
            FROM [Parent].[PetParents] WITH (UPDLOCK, HOLDLOCK)
            WHERE [MobileCountryCode] = @MobileCountryCode
              AND [MobileNumber] = @MobileNumber)
        BEGIN
            THROW 51222, 'Mobile number is already registered to another account.', 1;
        END

        INSERT INTO [Parent].[PetParents]
        (
            [ParentAuthIdentityId],
            [FirstName],
            [LastName],
            [Gender],
            [MobileCountryCode],
            [MobileNumber],
            [DateOfBirth],
            [AddressLine],
            [Latitude],
            [Longitude],
            [ZipCode],
            [City],
            [Description]
        )
        VALUES
        (
            @ParentAuthIdentityId,
            @FirstName,
            @LastName,
            @Gender,
            @MobileCountryCode,
            @MobileNumber,
            @DateOfBirth,
            @AddressLine,
            @Latitude,
            @Longitude,
            @ZipCode,
            @City,
            @Description
        );

        SELECT @PetParentId = [PetParentId]
        FROM [Parent].[PetParents]
        WHERE [ParentAuthIdentityId] = @ParentAuthIdentityId;

        UPDATE [Parent].[ParentAuthIdentities]
        SET [PetParentId] = @PetParentId,
            [SignUpStatus] = N'ParentProfileCompleted',
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ParentAuthIdentityId] = @ParentAuthIdentityId;

        UPDATE [Parent].[ParentDeviceTokens]
        SET [PetParentId] = @PetParentId,
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ParentAuthIdentityId] = @ParentAuthIdentityId;
    END

    SELECT [PetParentId],
           [ParentAuthIdentityId],
           [FirstName],
           [LastName],
           [Gender],
           [MobileCountryCode],
           [MobileNumber],
           [DateOfBirth],
           [AddressLine],
           [Latitude],
           [Longitude],
           [ZipCode],
           [City],
           [Description],
           [ProfilePhotoUrl],
           [MobileVerifiedAtUtc],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[PetParents]
    WHERE [PetParentId] = @PetParentId;

    COMMIT TRANSACTION;
END;
