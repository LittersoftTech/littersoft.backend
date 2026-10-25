CREATE OR ALTER PROCEDURE [Provider].[SaveProviderAuthIdentity]
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

    DECLARE @ProviderAuthIdentityId UNIQUEIDENTIFIER;
    DECLARE @ProviderId UNIQUEIDENTIFIER;

    BEGIN TRANSACTION;

    -- Serialize provider token registration with account deletion, including sign-in.
    DECLARE @TokenLockResult INT;
    EXEC @TokenLockResult = sys.sp_getapplock @Resource = N'Provider.DeviceTokenOwnership',
        @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 15000;
    IF @TokenLockResult < 0 THROW 51006, 'Device-token ownership is busy. Please retry.', 1;


    SELECT @ProviderAuthIdentityId = [ProviderAuthIdentityId]
    FROM [Provider].[ProviderAuthIdentities] WITH (UPDLOCK, HOLDLOCK)
    WHERE [FirebaseUserId] = @FirebaseUserId;

    -- Contact uniqueness is scoped to this app's Firebase account type.
    -- Range locks on the email index also serialize concurrent registrations.
    IF EXISTS (
        SELECT 1
        FROM [Provider].[ProviderAuthIdentities] AS a WITH (UPDLOCK, HOLDLOCK)
        WHERE a.[Email] = @Email AND a.[FirebaseUserId] <> @FirebaseUserId
          AND NOT EXISTS (
              SELECT 1 FROM [Provider].[Providers] AS p
              WHERE p.[ProviderAuthIdentityId] = a.[ProviderAuthIdentityId]
                AND p.[IsDeleted] = 1
          )
    )
        THROW 51980, 'Email is already registered to another provider account.', 1;

    IF @ProviderAuthIdentityId IS NULL
    BEGIN
        INSERT INTO [Provider].[ProviderAuthIdentities]
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

        SELECT @ProviderAuthIdentityId = [ProviderAuthIdentityId],
               @ProviderId = [ProviderId]
        FROM [Provider].[ProviderAuthIdentities]
        WHERE [FirebaseUserId] = @FirebaseUserId;
    END
    ELSE
    BEGIN
        UPDATE [Provider].[ProviderAuthIdentities]
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
        WHERE [ProviderAuthIdentityId] = @ProviderAuthIdentityId;

        SELECT @ProviderId = [ProviderId]
        FROM [Provider].[ProviderAuthIdentities]
        WHERE [ProviderAuthIdentityId] = @ProviderAuthIdentityId;
    END

    IF @FcmToken IS NOT NULL AND LEN(LTRIM(RTRIM(@FcmToken))) > 0
    BEGIN
        -- Sign-in and explicit refresh must use exactly the same ownership rules.
        EXEC [Provider].[SaveProviderDeviceToken]
            @FirebaseUserId = @FirebaseUserId, @FcmToken = @FcmToken,
            @DeviceId = @DeviceId, @DevicePlatform = @DevicePlatform, @ReturnResult = 0;
    END

    SELECT [ProviderAuthIdentityId],
           [ProviderId],
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
    FROM [Provider].[ProviderAuthIdentities]
    WHERE [FirebaseUserId] = @FirebaseUserId;

    COMMIT TRANSACTION;
END;
