CREATE OR ALTER PROCEDURE [Parent].[SaveParentAuthIdentity]
    @FirebaseUserId NVARCHAR(128),
    @FirebaseTenantId NVARCHAR(128) = NULL,
    @AuthProvider NVARCHAR(32),
    @FirebaseProviderId NVARCHAR(64) = NULL,
    @Email NVARCHAR(320),
    @IsEmailVerified BIT,
    @DisplayName NVARCHAR(200) = NULL,
    @FirebasePhoneNumber NVARCHAR(32) = NULL,
    @PhotoUrl NVARCHAR(1000) = NULL,
    @FcmToken NVARCHAR(2048) = NULL,
    @DeviceId NVARCHAR(200) = NULL,
    @DevicePlatform NVARCHAR(32) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @ParentAuthIdentityId UNIQUEIDENTIFIER;
    DECLARE @PetParentId UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    SELECT @ParentAuthIdentityId = [ParentAuthIdentityId]
    FROM [Parent].[ParentAuthIdentities] WITH (UPDLOCK, HOLDLOCK)
    WHERE [FirebaseUserId] = @FirebaseUserId;

    IF @ParentAuthIdentityId IS NULL
    BEGIN
        INSERT INTO [Parent].[ParentAuthIdentities]
        (
            [FirebaseUserId],
            [FirebaseTenantId],
            [AuthProvider],
            [FirebaseProviderId],
            [Email],
            [IsEmailVerified],
            [DisplayName],
            [FirebasePhoneNumber],
            [PhotoUrl]
        )
        VALUES
        (
            @FirebaseUserId,
            @FirebaseTenantId,
            @AuthProvider,
            @FirebaseProviderId,
            @Email,
            @IsEmailVerified,
            @DisplayName,
            @FirebasePhoneNumber,
            @PhotoUrl
        );

        SELECT @ParentAuthIdentityId = [ParentAuthIdentityId],
               @PetParentId = [PetParentId]
        FROM [Parent].[ParentAuthIdentities]
        WHERE [FirebaseUserId] = @FirebaseUserId;
    END
    ELSE
    BEGIN
        UPDATE [Parent].[ParentAuthIdentities]
        SET [FirebaseTenantId] = @FirebaseTenantId,
            [AuthProvider] = @AuthProvider,
            [FirebaseProviderId] = @FirebaseProviderId,
            [Email] = @Email,
            [IsEmailVerified] = @IsEmailVerified,
            [DisplayName] = @DisplayName,
            [FirebasePhoneNumber] = @FirebasePhoneNumber,
            [PhotoUrl] = @PhotoUrl,
            [LastSignedInAtUtc] = SYSUTCDATETIME(),
            [UpdatedAtUtc] = SYSUTCDATETIME()
        WHERE [ParentAuthIdentityId] = @ParentAuthIdentityId;

        SELECT @PetParentId = [PetParentId]
        FROM [Parent].[ParentAuthIdentities]
        WHERE [ParentAuthIdentityId] = @ParentAuthIdentityId;
    END

    IF @FcmToken IS NOT NULL AND LEN(LTRIM(RTRIM(@FcmToken))) > 0
    BEGIN
        IF EXISTS (
            SELECT 1
            FROM [Parent].[ParentDeviceTokens] WITH (UPDLOCK, HOLDLOCK)
            WHERE [FcmToken] = @FcmToken
        )
        BEGIN
            UPDATE [Parent].[ParentDeviceTokens]
            SET [ParentAuthIdentityId] = @ParentAuthIdentityId,
                [PetParentId] = @PetParentId,
                [DeviceId] = @DeviceId,
                [DevicePlatform] = @DevicePlatform,
                [IsActive] = 1,
                [LastSeenAtUtc] = SYSUTCDATETIME(),
                [UpdatedAtUtc] = SYSUTCDATETIME()
            WHERE [FcmToken] = @FcmToken;
        END
        ELSE
        BEGIN
            INSERT INTO [Parent].[ParentDeviceTokens]
            (
                [ParentAuthIdentityId],
                [PetParentId],
                [FcmToken],
                [DeviceId],
                [DevicePlatform]
            )
            VALUES
            (
                @ParentAuthIdentityId,
                @PetParentId,
                @FcmToken,
                @DeviceId,
                @DevicePlatform
            );
        END
    END

    SELECT [ParentAuthIdentityId],
           [PetParentId],
           [FirebaseUserId],
           [AuthProvider],
           [FirebaseProviderId],
           [Email],
           [IsEmailVerified],
           [DisplayName],
           [FirebasePhoneNumber],
           [PhotoUrl],
           [FirebaseTenantId],
           [SignUpStatus],
           [LastSignedInAtUtc],
           [CreatedAtUtc],
           [UpdatedAtUtc]
    FROM [Parent].[ParentAuthIdentities]
    WHERE [FirebaseUserId] = @FirebaseUserId;

    COMMIT TRANSACTION;
END;
