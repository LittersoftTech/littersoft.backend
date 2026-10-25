-- Disposable deployed database only. Run using sqlcmd -I -b from repository root.
SET NOCOUNT ON;
SET XACT_ABORT ON;
CREATE TABLE #ReturningProvider (OldId uniqueidentifier, OldAuth uniqueidentifier,
    NewId uniqueidentifier, NewAuth uniqueidentifier, Email nvarchar(320),
    Uid nvarchar(128), Phone nvarchar(32), DeletedAt datetime2(7), RepairUpdatedAt datetime2(7));
DECLARE @Suffix nvarchar(36)=CONVERT(nvarchar(36), NEWID()),
        @OldAuth uniqueidentifier, @OldId uniqueidentifier, @NewAuth uniqueidentifier, @NewId uniqueidentifier,
        @Parent uniqueidentifier, @Service uniqueidentifier=NEWID(), @NightService uniqueidentifier=NEWID(),
        @Booking uniqueidentifier=NEWID(), @Night uniqueidentifier=NEWID(), @Event uniqueidentifier=NEWID();
DECLARE @Email nvarchar(320)=CONCAT(@Suffix,N'@test.invalid'), @Uid nvarchar(128)=CONCAT(N'old-',@Suffix),
        @NewUid nvarchar(128)=CONCAT(N'new-',@Suffix), @Phone nvarchar(32)=LEFT(@Suffix,32);
EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@Uid, @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
SELECT @OldAuth=ProviderAuthIdentityId FROM [Provider].[ProviderAuthIdentities] WHERE FirebaseUserId=@Uid;
EXEC [Provider].[CompleteProviderProfile] @OldAuth,N'Test',N'Provider',N'Other',N'+41',@Phone,'1990-01-01';
SELECT @OldId=ProviderId FROM [Provider].[ProviderAuthIdentities] WHERE ProviderAuthIdentityId=@OldAuth;
EXEC [Parent].[SaveParentAuthIdentity] @FirebaseUserId=@Uid, @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
EXEC [Parent].[CompletePetParentProfile] @Uid,N'Test',N'Parent',N'Other',N'+41',@Phone,'1990-01-01',N'Address',47,8,N'8000',N'Zurich',N'Test';
SELECT @Parent=PetParentId FROM [Parent].[ParentAuthIdentities] WHERE FirebaseUserId=@Uid;
INSERT [Provider].[ProviderServices](ServiceId,ProviderId,ServiceCategory,SubCategory,ServiceType)
VALUES(@Service,@OldId,N'PetSitter',N'PetHotel',N'DayCare'),(@NightService,@OldId,N'PetSitter',N'PetHotel',N'NightStay');
INSERT [Booking].[Bookings](BookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,BookingDate,StartTime,EndTime,Status,PayoutStatus,PayoutId)
VALUES(@Booking,@OldId,@Parent,@Service,N'PetSitter',N'PetHotel','2020-01-01','10:00','11:00',N'PAID',N'Paid',N'old-payout');
INSERT [Booking].[NightStayBookings](NightStayBookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,CheckInDate,CheckOutDate,DropOffTime,PickUpTime,Status)
VALUES(@Night,@OldId,@Parent,@NightService,N'PetSitter',N'PetHotel','2020-01-01','2020-01-02','10:00','11:00',N'PAID');
INSERT [Booking].[BookingPayments](BookingType,BookingId,ProviderId,PetParentId,Amount,PaymentMethod)
VALUES(N'SingleDay',@Booking,@OldId,@Parent,100,N'Cash');
INSERT [Event].[Events](EventId,ProviderId,EventCategory,IsChildFriendly,Title,Description,EventType,StartDate,EndDate,StartTime,EndTime)
VALUES(@Event,@OldId,N'MeetupsAndSocials',1,N'Historical event',N'Test',N'Online','2020-01-01','2020-01-01','10:00','11:00');
INSERT [Provider].[ProviderPayoutMethods](ProviderId,PayoutMethod) VALUES(@OldId,N'Cash');

-- Inactive accounts keep their email; deleted accounts release it.
EXEC [Provider].[SetProviderActiveStatus] @OldId,0,1;
BEGIN TRY
    EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@NewUid, @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
    THROW 50000, 'Inactive account released email.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF ERROR_NUMBER() <> 51980 THROW;
END CATCH;
EXEC [Provider].[DeleteProvider] @OldId;
IF NOT EXISTS (SELECT 1 FROM [Provider].[Providers] WHERE ProviderId=@OldId AND IsDeleted=1 AND IsActive=0 AND MobileNumber<>@Phone)
    THROW 50000, 'Deletion did not release mobile.', 1;
IF EXISTS (SELECT 1 FROM [Provider].[ProviderAuthIdentities] WHERE ProviderAuthIdentityId=@OldAuth AND (Email=@Email OR FirebaseUserId=@Uid))
    THROW 50000, 'Deletion did not release email and Firebase UID.', 1;
IF EXISTS (SELECT 1 FROM [Provider].[ProviderPayoutMethods] WHERE ProviderId=@OldId)
    THROW 50000, 'Deleted payout configuration survived.', 1;

EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@NewUid, @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
SELECT @NewAuth=ProviderAuthIdentityId FROM [Provider].[ProviderAuthIdentities] WHERE FirebaseUserId=@NewUid AND ProviderId IS NULL;
IF @NewAuth IS NULL OR @NewAuth=@OldAuth THROW 50000, 'Returning account reused old identity.', 1;
EXEC [Provider].[CompleteProviderProfile] @NewAuth,N'Returning',N'Provider',N'Other',N'+41',@Phone,'1990-01-01';
SELECT @NewId=ProviderId FROM [Provider].[ProviderAuthIdentities] WHERE ProviderAuthIdentityId=@NewAuth;
IF @NewId IS NULL OR @NewId=@OldId THROW 50000, 'Returning account reused old provider ID.', 1;
INSERT #ReturningProvider(OldId,OldAuth,NewId,NewAuth,Email,Uid,Phone,DeletedAt)
SELECT @OldId,@OldAuth,@NewId,@NewAuth,@Email,@Uid,@Phone,DeletedAtUtc FROM [Provider].[Providers] WHERE ProviderId=@OldId;

EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@NewUid, @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
EXEC [Provider].[DeleteProvider] @OldId;
IF NOT EXISTS (SELECT 1 FROM [Provider].[Providers] p JOIN #ReturningProvider t ON p.ProviderId=t.OldId WHERE p.DeletedAtUtc=t.DeletedAt)
    THROW 50000, 'Repeated deletion changed timestamp.', 1;
IF NOT EXISTS (SELECT 1 FROM [Booking].[Bookings] WHERE BookingId=@Booking AND ProviderId=@OldId AND PayoutId=N'old-payout' AND PayoutStatus=N'Paid')
 OR NOT EXISTS (SELECT 1 FROM [Booking].[NightStayBookings] WHERE NightStayBookingId=@Night AND ProviderId=@OldId)
 OR NOT EXISTS (SELECT 1 FROM [Event].[Events] WHERE EventId=@Event AND ProviderId=@OldId)
 OR NOT EXISTS (SELECT 1 FROM [Booking].[BookingPayments] WHERE BookingId=@Booking AND ProviderId=@OldId AND Amount=100)
    THROW 50000, 'Historical ownership or payout changed.', 1;
IF EXISTS (SELECT 1 FROM [Booking].[Bookings] WHERE ProviderId=@NewId)
 OR EXISTS (SELECT 1 FROM [Booking].[NightStayBookings] WHERE ProviderId=@NewId)
 OR EXISTS (SELECT 1 FROM [Event].[Events] WHERE ProviderId=@NewId)
 OR EXISTS (SELECT 1 FROM [Booking].[BookingPayments] WHERE ProviderId=@NewId)
 OR EXISTS (SELECT 1 FROM [Provider].[ProviderPayoutMethods] WHERE ProviderId=@NewId)
    THROW 50000, 'New account inherited old data.', 1;
PRINT 'PASS deletion releases contacts; new identity/provider has no old bookings, events or payouts; old history retained';

-- A live identity without a profile must still reserve its email.
DECLARE @PendingUid nvarchar(128)=CONCAT(N'pending-',@Suffix), @PendingEmail nvarchar(320)=CONCAT(N'pending-',@Email);
EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@PendingUid, @AuthProvider=N'EmailPassword', @Email=@PendingEmail, @IsEmailVerified=1;
BEGIN TRY
    EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@PendingUid, @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
    THROW 50000, 'Live duplicate email update accepted.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF ERROR_NUMBER() <> 51980 THROW;
END CATCH;
BEGIN TRY
    EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@Uid, @AuthProvider=N'EmailPassword', @Email=@PendingEmail, @IsEmailVerified=1;
    THROW 50000, 'Pending identity did not reserve email.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF ERROR_NUMBER() <> 51980 THROW;
END CATCH;
DECLARE @PendingAuth uniqueidentifier=(SELECT ProviderAuthIdentityId FROM [Provider].[ProviderAuthIdentities] WHERE FirebaseUserId=@PendingUid);
BEGIN TRY
    EXEC [Provider].[CompleteProviderProfile] @PendingAuth,N'Test',N'Provider',N'Other',N'+41',@Phone,'1990-01-01';
    THROW 50000, 'Live duplicate mobile accepted.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF ERROR_NUMBER() NOT IN (2601,2627) THROW;
END CATCH;
PRINT 'PASS live and onboarding identities retain duplicate protection';

-- Simulate an older deployment retaining contacts on a deleted record.
-- Its email/phone may now match a new live account; duplicate checks must ignore it.
UPDATE a SET Email=t.Email, FirebaseUserId=t.Uid
FROM [Provider].[ProviderAuthIdentities] a JOIN #ReturningProvider t ON a.ProviderAuthIdentityId=t.OldAuth;
UPDATE p SET MobileCountryCode=N'+41', MobileNumber=t.Phone
FROM [Provider].[Providers] p JOIN #ReturningProvider t ON p.ProviderId=t.OldId;
EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@NewUid, @AuthProvider=N'EmailPassword', @Email=@Email, @IsEmailVerified=1;
-- Force an actual mobile change so the duplicate check is exercised.
EXEC [Provider].[UpdateProviderMobile] @NewId,N'+49',@Phone;
EXEC [Provider].[UpdateProviderMobile] @NewId,N'+41',@Phone;
PRINT 'PASS legacy deleted contacts do not block email sign-in or mobile updates';
GO
:r database/Pawfront.Database/Deployment/ReleaseDeletedProviderContacts.sql
GO
IF EXISTS (SELECT 1 FROM [Provider].[ProviderAuthIdentities] a JOIN #ReturningProvider t ON a.ProviderAuthIdentityId=t.OldAuth WHERE a.Email=t.Email OR a.FirebaseUserId=t.Uid)
 OR EXISTS (SELECT 1 FROM [Provider].[Providers] p JOIN #ReturningProvider t ON p.ProviderId=t.OldId WHERE p.MobileNumber=t.Phone)
    THROW 50000, 'Legacy contact repair failed.', 1;
IF NOT EXISTS (SELECT 1 FROM [Provider].[ProviderAuthIdentities] a JOIN #ReturningProvider t ON a.ProviderAuthIdentityId=t.NewAuth WHERE a.Email=t.Email AND a.ProviderId=t.NewId)
    THROW 50000, 'Legacy repair changed new account.', 1;
UPDATE t SET RepairUpdatedAt=a.UpdatedAtUtc FROM #ReturningProvider t JOIN [Provider].[ProviderAuthIdentities] a ON a.ProviderAuthIdentityId=t.OldAuth;
-- Exercise upgrading an existing unfiltered index rather than only fresh creation.
DROP INDEX [UX_Providers_MobileNumber] ON [Provider].[Providers];
CREATE UNIQUE INDEX [UX_Providers_MobileNumber] ON [Provider].[Providers](MobileCountryCode,MobileNumber);
GO
:r database/Pawfront.Database/Deployment/ReleaseDeletedProviderContacts.sql
GO
IF EXISTS (SELECT 1 FROM [Provider].[ProviderAuthIdentities] a JOIN #ReturningProvider t ON a.ProviderAuthIdentityId=t.OldAuth WHERE a.UpdatedAtUtc<>t.RepairUpdatedAt)
    THROW 50000, 'Legacy repair is not idempotent.', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'[Provider].[Providers]') AND name=N'UX_Providers_MobileNumber' AND has_filter=1 AND is_unique=1)
    THROW 50000, 'Legacy mobile index was not upgraded.', 1;
IF (SELECT COUNT(*) FROM [Booking].[Bookings] b JOIN #ReturningProvider t ON b.ProviderId=t.OldId) <> 1
 OR (SELECT COUNT(*) FROM [Booking].[NightStayBookings] b JOIN #ReturningProvider t ON b.ProviderId=t.OldId) <> 1
 OR (SELECT COUNT(*) FROM [Event].[Events] e JOIN #ReturningProvider t ON e.ProviderId=t.OldId) <> 1
 OR (SELECT COUNT(*) FROM [Booking].[BookingPayments] b JOIN #ReturningProvider t ON b.ProviderId=t.OldId) <> 1
    THROW 50000, 'Legacy repair changed historical ownership.', 1;
DECLARE @ReuseUid nvarchar(128)=(SELECT Uid FROM #ReturningProvider),
        @ReuseEmail nvarchar(320)=(SELECT CONCAT(N'reuse-',Email) FROM #ReturningProvider);
EXEC [Provider].[SaveProviderAuthIdentity] @FirebaseUserId=@ReuseUid, @AuthProvider=N'EmailPassword', @Email=@ReuseEmail, @IsEmailVerified=1;
IF NOT EXISTS (SELECT 1 FROM [Provider].[ProviderAuthIdentities] a CROSS JOIN #ReturningProvider t WHERE a.FirebaseUserId=@ReuseUid AND a.ProviderAuthIdentityId<>t.OldAuth AND a.ProviderId IS NULL)
    THROW 50000, 'Legacy Firebase UID resolved deleted account.', 1;
PRINT 'PASS legacy repair frees Firebase UID, upgrades old index, preserves accounts/history, and is idempotent';
DROP TABLE #ReturningProvider;
