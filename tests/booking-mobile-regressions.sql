-- Run only in a disposable database after DeployAll.sql. All identities are synthetic.
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Parent UNIQUEIDENTIFIER=NEWID(), @ParentAuth UNIQUEIDENTIFIER=NEWID(),
        @Provider UNIQUEIDENTIFIER=NEWID(), @ProviderAuth UNIQUEIDENTIFIER=NEWID(),
        @DayService UNIQUEIDENTIFIER=NEWID(), @NightService UNIQUEIDENTIFIER=NEWID(), @Pet UNIQUEIDENTIFIER=NEWID();
DECLARE @Today DATE=CAST(SYSUTCDATETIME() AS DATE), @Tomorrow DATE=DATEADD(DAY,1,CAST(SYSUTCDATETIME() AS DATE)),
        @Later DATE=DATEADD(DAY,3,CAST(SYSUTCDATETIME() AS DATE));
INSERT [Parent].[ParentAuthIdentities](ParentAuthIdentityId,FirebaseUserId,AuthProvider,Email)
VALUES(@ParentAuth,CONVERT(nvarchar(36),@Parent),N'EmailPassword',CONCAT(@Parent,N'@test.invalid'));
INSERT [Parent].[PetParents](PetParentId,ParentAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth,AddressLine,Latitude,Longitude,ZipCode,City,Description)
VALUES(@Parent,@ParentAuth,N'Test',N'Parent',N'Other',N'+41',LEFT(CONVERT(nvarchar(36),@Parent),16),'1990-01-01',N'Address',47,8,N'8000',N'Zurich',N'Test');
INSERT [Provider].[ProviderAuthIdentities](ProviderAuthIdentityId,FirebaseUserId,AuthProvider,Email)
VALUES(@ProviderAuth,CONVERT(nvarchar(36),@Provider),N'EmailPassword',CONCAT(@Provider,N'@test.invalid'));
INSERT [Provider].[Providers](ProviderId,ProviderAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth)
VALUES(@Provider,@ProviderAuth,N'Test',N'Provider',N'Other',N'+41',LEFT(CONVERT(nvarchar(36),@Provider),16),'1990-01-01');
INSERT [Provider].[ProviderServices](ServiceId,ProviderId,ServiceCategory,SubCategory,ServiceType)
VALUES(@DayService,@Provider,N'PetSitter',N'Freelance',N'DayCare'),(@NightService,@Provider,N'PetSitter',N'PetHotel',N'NightStay');
INSERT [Parent].[Pets](PetId,PetParentId,PetType,PetName,Breed,Gender,DateOfBirth,Weight)
VALUES(@Pet,@Parent,N'Dog',N'Milo',N'Labrador',N'Male','2020-01-01',20);

-- A same-day stay with midnight drop-off can be created and accepted after drop-off.
EXEC [Booking].[CreateNightStayBooking] @ProviderId=@Provider,@PetParentId=@Parent,@PetId=@Pet,
    @ServiceId=@NightService,@ServiceCategory=N'PetSitter',@SubCategory=N'PetHotel',@CheckInDate=@Today,
    @CheckOutDate=@Later,@DropOffTime='00:00',@PickUpTime='10:00',@PricePerNight=10,@Capacity=10;
DECLARE @Stay UNIQUEIDENTIFIER=(SELECT TOP(1) NightStayBookingId FROM [Booking].[NightStayBookings] WHERE PetId=@Pet);
EXEC [Booking].[ExpireStaleCreatedBookings];
IF NOT EXISTS(SELECT 1 FROM [Booking].[NightStayBookings] WHERE NightStayBookingId=@Stay AND Status=N'CREATED')
    THROW 50001,'Same-day stay incorrectly expired after drop-off.',1;
EXEC [Booking].[UpdateNightStayBookingStatus] @Stay,N'CONFIRMED',N'Provider',@Provider;
EXEC [Booking].[RequestNightStayBookingModification] @Stay,N'Provider',@Provider,@Today,@Tomorrow;
EXEC [Booking].[RevertExpiredModificationRequests];
IF NOT EXISTS(SELECT 1 FROM [Booking].[NightStayBookingModifications] WHERE NightStayBookingId=@Stay)
    THROW 50001,'Same-day proposal incorrectly expired after drop-off.',1;
EXEC [Booking].[RespondNightStayBookingModification] @Stay,N'Parent',@Parent,1,10;
IF NOT EXISTS(SELECT 1 FROM [Booking].[NightStayBookings] WHERE NightStayBookingId=@Stay AND CheckInDate=@Today AND CheckOutDate=@Tomorrow)
    THROW 50001,'Same-day modification acceptance failed.',1;
PRINT 'PASS same-day night create, acceptance, modification and both expiry sweeps';

-- Repeat cash reports update the timestamp, then payment clears it for both types.
DECLARE @Day UNIQUEIDENTIFIER=NEWID();
INSERT [Booking].[Bookings](BookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,BookingDate,StartTime,EndTime,Status,Source,PricePerHour,PetId)
VALUES(@Day,@Provider,@Parent,@DayService,N'PetSitter',N'Freelance',@Today,'00:00','01:00',N'COMPLETED',N'App',10,@Pet);
UPDATE [Booking].[NightStayBookings] SET Status=N'COMPLETED' WHERE NightStayBookingId=@Stay;
EXEC [Booking].[UpdateBookingStatus] @Day,N'PAYMENT_NOT_RECEIVED',N'Provider',@Provider,@Latitude=47,@Longitude=8;
EXEC [Booking].[UpdateNightStayBookingStatus] @Stay,N'PAYMENT_NOT_RECEIVED',N'Provider',@Provider,@Latitude=47,@Longitude=8;
DECLARE @FirstDay DATETIME2=(SELECT CashNotReceivedAtUtc FROM [Booking].[Bookings] WHERE BookingId=@Day),
        @FirstStay DATETIME2=(SELECT CashNotReceivedAtUtc FROM [Booking].[NightStayBookings] WHERE NightStayBookingId=@Stay);
IF @FirstDay IS NULL OR @FirstStay IS NULL THROW 50001,'Report timestamp not stored.',1;
EXEC [Booking].[UpdateBookingStatus] @Day,N'PAYMENT_NOT_RECEIVED',N'Provider',@Provider,@Latitude=47,@Longitude=8;
EXEC [Booking].[UpdateNightStayBookingStatus] @Stay,N'PAYMENT_NOT_RECEIVED',N'Provider',@Provider,@Latitude=47,@Longitude=8;
IF EXISTS(SELECT 1 FROM [Booking].[Bookings] WHERE BookingId=@Day AND CashNotReceivedAtUtc<@FirstDay)
 OR EXISTS(SELECT 1 FROM [Booking].[NightStayBookings] WHERE NightStayBookingId=@Stay AND CashNotReceivedAtUtc<@FirstStay)
    THROW 50001,'Latest cash report did not win.',1;
EXEC [Booking].[GetBookingDetail] @Day;
EXEC [Booking].[GetNightStayBookingDetail] @Stay;
EXEC [Booking].[ListBookingsByPetParent] @Parent;
EXEC [Booking].[ListNightStayBookingsByPetParent] @Parent;
EXEC [Booking].[ListBookingsByProvider] @Provider;
EXEC [Booking].[ListNightStayBookingsByProvider] @Provider;
EXEC [Booking].[ListProviderJobs] @Provider;
EXEC [Booking].[ListProviderEarningsBookings] @Provider;
EXEC [Booking].[ListPetParentBookingHistory] @Parent;
EXEC [Booking].[ListBookingsForParentProvider] @Provider,@Parent;
EXEC [Booking].[MarkBookingPaid] @Day,@Provider,10,1,N'Cash',47,8;
EXEC [Booking].[MarkNightStayBookingPaid] @Stay,@Provider,10,1,N'Cash',47,8;
IF EXISTS(SELECT 1 FROM [Booking].[Bookings] WHERE BookingId=@Day AND CashNotReceivedAtUtc IS NOT NULL)
 OR EXISTS(SELECT 1 FROM [Booking].[NightStayBookings] WHERE NightStayBookingId=@Stay AND CashNotReceivedAtUtc IS NOT NULL)
    THROW 50001,'Paid booking still has a cash report.',1;
EXEC [Booking].[RecordBookingLocationEvent] @Day,N'CashNotReceived',N'Provider',@Provider,47,8;
IF EXISTS(SELECT 1 FROM [Booking].[Bookings] WHERE BookingId=@Day AND CashNotReceivedAtUtc IS NOT NULL)
    THROW 50001,'Late location report overwrote paid booking.',1;
PRINT 'PASS cash report persistence, repeated reports, list/detail queries and payment clearing';

-- Booking tickets remain free-form for both parties; provider cash tickets persist the marker.
UPDATE [Booking].[Bookings] SET Status=N'COMPLETED' WHERE BookingId=@Day;
EXEC [Support].[CreateTicket] @TicketType=N'BookingIncident',@RaisedByType=N'PetParent',@ActorId=@Parent,
    @BookingType=N'SingleDay',@BookingId=@Day,@Category=N'Payment issue',@Reason=N'This is my description';
EXEC [Support].[CreateTicket] @TicketType=N'BookingIncident',@RaisedByType=N'Provider',@ActorId=@Provider,
    @BookingType=N'SingleDay',@BookingId=@Day,@Category=N'Cash Not Received',@Reason=N'My description';
IF NOT EXISTS(SELECT 1 FROM [Booking].[Bookings] WHERE BookingId=@Day AND CashNotReceivedAtUtc IS NOT NULL)
    THROW 50001,'Provider cash ticket did not persist report timestamp.',1;
PRINT 'PASS free-form booking tickets and provider cash ticket timestamp';

-- Provider-caused cancellations use one call and produce no reliability penalty.
DECLARE @CancelDay UNIQUEIDENTIFIER=NEWID(), @CancelStay UNIQUEIDENTIFIER=NEWID();
INSERT [Booking].[Bookings](BookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,BookingDate,StartTime,EndTime,Status,Source,PricePerHour,CancellationPolicyHours)
VALUES(@CancelDay,@Provider,@Parent,@DayService,N'PetSitter',N'Freelance',@Tomorrow,'10:00','11:00',N'CONFIRMED',N'App',10,96);
INSERT [Booking].[NightStayBookings](NightStayBookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,CheckInDate,CheckOutDate,DropOffTime,PickUpTime,Status,PricePerNight,CancellationPolicyHours)
VALUES(@CancelStay,@Provider,@Parent,@NightService,N'PetSitter',N'PetHotel',@Today,@Later,'00:00','10:00',N'CONFIRMED',10,96);
EXEC [Booking].[RequestBookingModification] @CancelDay,N'Provider',@Provider,@Later,'10:00','11:00';
EXEC [Booking].[RequestNightStayBookingModification] @CancelStay,N'Provider',@Provider,@Today,@Tomorrow;
EXEC [Booking].[RespondBookingModification] @CancelDay,N'Parent',@Parent,0,10,@CancelBooking=1;
EXEC [Booking].[RespondNightStayBookingModification] @CancelStay,N'Parent',@Parent,0,10,@CancelBooking=1;
IF NOT EXISTS(SELECT 1 FROM [Booking].[Bookings] WHERE BookingId=@CancelDay AND Status=N'PARENT_CANCELLED' AND CancellationReason=N'CancelledAfterModificationDeclined' AND CancelledAtUtc IS NOT NULL)
 OR NOT EXISTS(SELECT 1 FROM [Booking].[NightStayBookings] WHERE NightStayBookingId=@CancelStay AND Status=N'PARENT_CANCELLED' AND CancellationReason=N'CancelledAfterModificationDeclined' AND CancelledAtUtc IS NOT NULL)
    THROW 50001,'Atomic decline-and-cancel did not record its reason.',1;
IF EXISTS(SELECT 1 FROM [Booking].[BookingModifications] WHERE BookingId=@CancelDay)
 OR EXISTS(SELECT 1 FROM [Booking].[NightStayBookingModifications] WHERE NightStayBookingId=@CancelStay)
    THROW 50001,'Cancellation left a pending modification.',1;
IF EXISTS(SELECT 1 FROM [Booking].[ServiceReliabilityMarks] WHERE BookingId IN (@CancelDay,@CancelStay))
    THROW 50001,'Provider-caused cancellation penalized the parent.',1;
DECLARE @Manual UNIQUEIDENTIFIER=NEWID();
INSERT [Booking].[Bookings](BookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,BookingDate,StartTime,EndTime,Status,Source,PricePerHour,CancellationPolicyHours)
VALUES(@Manual,@Provider,@Parent,@DayService,N'PetSitter',N'Freelance',@Tomorrow,'10:00','11:00',N'CONFIRMED',N'App',10,96);
EXEC [Booking].[UpdateBookingStatus] @Manual,N'PARENT_CANCELLED',N'Parent',@Parent;
IF NOT EXISTS(SELECT 1 FROM [Booking].[ServiceReliabilityMarks] WHERE BookingId=@Manual AND Outcome=N'LateCancellation')
    THROW 50001,'Manual late cancellation lost its reliability penalty.',1;
PRINT 'PASS atomic decline-and-cancel, proposal cleanup, penalty exclusion and manual cancellation';

-- A thirteenth gallery photo is refused; deleting one restores capacity.
DECLARE @Count INT=0;
WHILE @Count<12
BEGIN
    EXEC [Parent].[AddPetPhoto] @Pet,N'https://test.invalid/photo.jpg';
    SET @Count+=1;
END;
BEGIN TRY
    EXEC [Parent].[AddPetPhoto] @Pet,N'https://test.invalid/13.jpg';
    THROW 50001,'Thirteenth pet photo was accepted.',1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK;
    IF ERROR_NUMBER()<>51420 THROW;
END CATCH;
IF (SELECT COUNT(*) FROM [Parent].[PetPhotos] WHERE PetId=@Pet)<>12
    THROW 50001,'Photo limit changed existing gallery.',1;
DELETE TOP(1) FROM [Parent].[PetPhotos] WHERE PetId=@Pet;
EXEC [Parent].[AddPetPhoto] @Pet,N'https://test.invalid/replacement.jpg';
PRINT 'PASS photo limit and replacement after deletion';

-- Leave one reported night stay to exercise non-null night-stay C# read mappings.
DECLARE @ReportedStay UNIQUEIDENTIFIER=NEWID();
INSERT [Booking].[NightStayBookings](NightStayBookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,CheckInDate,CheckOutDate,DropOffTime,PickUpTime,Status,PricePerNight)
VALUES(@ReportedStay,@Provider,@Parent,@NightService,N'PetSitter',N'PetHotel',@Today,@Later,'00:00','10:00',N'COMPLETED',10);
EXEC [Booking].[UpdateNightStayBookingStatus] @ReportedStay,N'PAYMENT_NOT_RECEIVED',N'Provider',@Provider,@Latitude=47,@Longitude=8;

-- Date rejection remains enforced for direct callers.
DECLARE @Yesterday DATE=DATEADD(DAY,-1,@Today);
BEGIN TRY
    EXEC [Booking].[CreateNightStayBooking] @ProviderId=@Provider,@PetParentId=@Parent,
        @ServiceId=@NightService,@ServiceCategory=N'PetSitter',@SubCategory=N'PetHotel',@CheckInDate=@Yesterday,
        @CheckOutDate=@Today,@DropOffTime='00:00',@PickUpTime='10:00',@Capacity=10;
    THROW 50001,'Past check-in was accepted.',1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK;
    IF ERROR_NUMBER()<>51421 THROW;
END CATCH;
BEGIN TRY
    EXEC [Booking].[CreateNightStayBooking] @ProviderId=@Provider,@PetParentId=@Parent,
        @ServiceId=@NightService,@ServiceCategory=N'PetSitter',@SubCategory=N'PetHotel',@CheckInDate=@Today,
        @CheckOutDate=@Today,@DropOffTime='00:00',@PickUpTime='10:00',@Capacity=10;
    THROW 50001,'Checkout on check-in was accepted.',1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT>0 ROLLBACK;
    IF ERROR_NUMBER()<>51421 THROW;
END CATCH;
PRINT 'PASS past check-in and invalid checkout rejection';

-- Earlier/later proposals and the 24-hour cap must quote the same review deadline.
DECLARE @Case INT=0;
WHILE @Case < 6
BEGIN
    -- Exercise both requester directions for each deadline arm.
    DECLARE @Scenario INT=@Case % 3;
    DECLARE @Requester NVARCHAR(16)=CASE WHEN @Case < 3 THEN N'Parent' ELSE N'Provider' END;
    DECLARE @RequesterId UNIQUEIDENTIFIER=CASE WHEN @Case < 3 THEN @Parent ELSE @Provider END;
    DECLARE @ModificationBooking UNIQUEIDENTIFIER=NEWID();
    DECLARE @Original DATETIME2(0)=DATEADD(HOUR,CASE WHEN @Scenario=2 THEN 72 ELSE 5 END,SYSUTCDATETIME());
    DECLARE @Proposed DATETIME2(0)=DATEADD(MINUTE,CASE WHEN @Scenario=0 THEN -30 ELSE 30 END,@Original);
    DECLARE @OriginalDate DATE=CAST(@Original AS DATE), @OriginalTime TIME(0)=CAST(@Original AS TIME(0)),
            @OriginalEnd TIME(0)=CAST(DATEADD(MINUTE,15,@Original) AS TIME(0)),
            @ProposedDate DATE=CAST(@Proposed AS DATE), @ProposedTime TIME(0)=CAST(@Proposed AS TIME(0)),
            @ProposedEnd TIME(0)=CAST(DATEADD(MINUTE,15,@Proposed) AS TIME(0));
    INSERT [Booking].[Bookings](BookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,BookingDate,StartTime,EndTime,Status,Source,PricePerHour)
    VALUES(@ModificationBooking,@Provider,@Parent,@DayService,N'PetSitter',N'Freelance',@OriginalDate,@OriginalTime,@OriginalEnd,N'CONFIRMED',N'App',10);
    EXEC [Booking].[RequestBookingModification] @ModificationBooking,@Requester,@RequesterId,@ProposedDate,@ProposedTime,@ProposedEnd;
    DECLARE @Expected DATETIME2(0)=DATEADD(HOUR,-2,CASE WHEN @Original < @Proposed THEN @Original ELSE @Proposed END);
    IF @Scenario=2 SELECT @Expected=DATEADD(HOUR,24,CreatedAtUtc) FROM [Booking].[BookingModifications] WHERE BookingId=@ModificationBooking;
    IF NOT EXISTS(SELECT 1 FROM [Notification].[NotificationOutbox] WHERE EntityId=@ModificationBooking
        AND TRY_CONVERT(DATETIME2(0),JSON_VALUE(DataJson,'$.reviewByUtc'),127)=@Expected)
        THROW 50001,'Modification push has the wrong review deadline.',1;
    IF @Scenario=0
    BEGIN
        -- Original is still five hours away, but proposed start has crossed T-2h.
        UPDATE [Booking].[BookingModifications]
        SET ProposedBookingDate=CAST(DATEADD(MINUTE,119,SYSUTCDATETIME()) AS DATE),
            ProposedStartTime=CAST(DATEADD(MINUTE,119,SYSUTCDATETIME()) AS TIME(0))
        WHERE BookingId=@ModificationBooking;
        DECLARE @Rejected BIT=0;
        BEGIN TRY
            DECLARE @Responder NVARCHAR(16)=CASE WHEN @Requester=N'Parent' THEN N'Provider' ELSE N'Parent' END;
            DECLARE @ResponderId UNIQUEIDENTIFIER=CASE WHEN @Requester=N'Parent' THEN @Provider ELSE @Parent END;
            EXEC [Booking].[RespondBookingModification] @ModificationBooking,@Responder,@ResponderId,1,10;
        END TRY
        BEGIN CATCH
            IF XACT_STATE()<>0 ROLLBACK;
            IF ERROR_NUMBER()<>51152 THROW;
            SET @Rejected=1;
        END CATCH;
        IF @Rejected=0 THROW 50001,'Expired earlier proposal was accepted.',1;
        EXEC [Booking].[RevertExpiredModificationRequests];
        IF EXISTS(SELECT 1 FROM [Booking].[BookingModifications] WHERE BookingId=@ModificationBooking)
            THROW 50001,'Sweep retained an expired earlier proposal.',1;
        IF NOT EXISTS(SELECT 1 FROM [Booking].[Bookings] WHERE BookingId=@ModificationBooking AND Status=N'CONFIRMED'
                      AND BookingDate=@OriginalDate AND StartTime=@OriginalTime)
            THROW 50001,'Sweep failed to preserve the original schedule.',1;
    END;
    SET @Case+=1;
END;
PRINT 'PASS modification deadlines, late response guard and earlier-start expiry sweep';

-- Provider modification allowance survives the staging row being deleted on response.
DECLARE @LimitCase INT=0;
WHILE @LimitCase<2
BEGIN
    DECLARE @LimitBooking UNIQUEIDENTIFIER=NEWID(), @LimitAccept BIT=CAST(@LimitCase AS BIT), @LimitRejected BIT=0;
    INSERT [Booking].[Bookings](BookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,BookingDate,StartTime,EndTime,Status,Source,PricePerHour)
    VALUES(@LimitBooking,@Provider,@Parent,@DayService,N'PetSitter',N'Freelance',@Later,'10:00','11:00',N'CONFIRMED',N'App',10);
    EXEC [Booking].[RequestBookingModification] @LimitBooking,N'Provider',@Provider,@Later,'11:00','12:00';
    EXEC [Booking].[RespondBookingModification] @LimitBooking,N'Parent',@Parent,@LimitAccept,100;
    IF EXISTS(SELECT 1 FROM [Booking].[BookingModifications] WHERE BookingId=@LimitBooking)
        THROW 50001,'Response failed to remove staging row.',1;
    BEGIN TRY
        EXEC [Booking].[RequestBookingModification] @LimitBooking,N'Provider',@Provider,@Later,'12:00','13:00';
    END TRY
    BEGIN CATCH
        IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
        IF ERROR_NUMBER()<>51990 THROW;
        SET @LimitRejected=1;
    END CATCH;
    IF @LimitRejected<>@LimitAccept THROW 50001,'Only accepted provider changes should consume the allowance.',1;
    IF @LimitAccept=0
        EXEC [Booking].[RespondBookingModification] @LimitBooking,N'Parent',@Parent,0,100;
    -- Parent requests retain their own independent behavior.
    EXEC [Booking].[RequestBookingModification] @LimitBooking,N'Parent',@Parent,@Later,'12:00','13:00';
    SET @LimitCase+=1;
END;
PRINT 'PASS provider modification limit after acceptance only, with parent requests unaffected';

-- Past working days: started private job becomes incomplete; completed work stays completed.
DECLARE @IncompleteJob UNIQUEIDENTIFIER=NEWID(), @FinishedJob UNIQUEIDENTIFIER=NEWID(), @IncompleteDay DATE=DATEADD(DAY,-1,@Today);
INSERT [Booking].[Bookings](BookingId,ProviderId,ServiceId,ServiceCategory,SubCategory,BookingDate,StartTime,EndTime,Status,Source,PricePerHour,CustomerName,CustomerMobileCountryCode,CustomerMobile,AnimalType,PetName,ServiceLocation)
VALUES(@IncompleteJob,@Provider,@DayService,N'PetSitter',N'Freelance',@IncompleteDay,'10:00','11:00',N'IN_PROGRESS',N'Custom',10,N'Test',N'+41',N'123456',N'Dog',N'Milo',N'MyLocation'),
      (@FinishedJob,@Provider,@DayService,N'PetSitter',N'Freelance',@IncompleteDay,'10:00','11:00',N'COMPLETED',N'Custom',10,N'Test',N'+41',N'123456',N'Dog',N'Milo',N'MyLocation');
EXEC [Booking].[SettleUnstartedJobsAsNoShow];
IF NOT EXISTS(SELECT 1 FROM [Booking].[Bookings] WHERE BookingId=@IncompleteJob AND Status=N'JOB_INCOMPLETE')
    THROW 50001,'Started private job did not become incomplete.',1;
IF NOT EXISTS(SELECT 1 FROM [Booking].[BookingStatusHistory] WHERE BookingId=@IncompleteJob AND FromStatus=N'IN_PROGRESS' AND ToStatus=N'JOB_INCOMPLETE')
    THROW 50001,'Incomplete transition missing from history.',1;
IF NOT EXISTS(SELECT 1 FROM [Booking].[Bookings] WHERE BookingId=@FinishedJob AND Status=N'COMPLETED')
    THROW 50001,'Sweep changed completed private job.',1;
EXEC [Booking].[SettleUnstartedJobsAsNoShow];
IF (SELECT COUNT(*) FROM [Booking].[BookingStatusHistory] WHERE BookingId=@IncompleteJob AND ToStatus=N'JOB_INCOMPLETE')<>1
    THROW 50001,'Incomplete sweep is not idempotent.',1;
PRINT 'PASS incomplete private job status, history, completed exclusion and sweep idempotency';
