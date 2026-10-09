-- Run only in an isolated database after DeployAll.sql, with sqlcmd -I -b.
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Provider UNIQUEIDENTIFIER = NEWID(), @Auth UNIQUEIDENTIFIER = NEWID(),
        @Service UNIQUEIDENTIFIER = NEWID(), @Booking UNIQUEIDENTIFIER = NEWID(),
        @Cancelled UNIQUEIDENTIFIER = NEWID(), @Other UNIQUEIDENTIFIER = NEWID();
INSERT [Provider].[ProviderAuthIdentities]
    (ProviderAuthIdentityId, FirebaseUserId, AuthProvider, Email)
VALUES (@Auth, CONVERT(NVARCHAR(36), @Auth), N'EmailPassword', N'checks@example.invalid');
INSERT [Provider].[Providers]
    (ProviderId, ProviderAuthIdentityId, FirstName, LastName, Gender, MobileCountryCode, MobileNumber, DateOfBirth)
VALUES (@Provider, @Auth, N'Availability', N'Checks', N'Other', N'+91', LEFT(CONVERT(NVARCHAR(36), @Auth), 32), '19900101');
INSERT [Provider].[ProviderServices] (ServiceId, ProviderId, ServiceCategory, SubCategory, ServiceType)
VALUES (@Service, @Provider, N'PetSitter', N'PetHotel', N'DayCare');

DECLARE @Days NVARCHAR(MAX) = N'[
 {"dayOfWeek":0,"isOpen":false},
 {"dayOfWeek":1,"isOpen":true,"startTime":"00:00:00","endTime":"08:00:00"},
 {"dayOfWeek":2,"isOpen":false},{"dayOfWeek":3,"isOpen":false},
 {"dayOfWeek":4,"isOpen":false},{"dayOfWeek":5,"isOpen":false},{"dayOfWeek":6,"isOpen":false}]';
EXEC [Provider].[SaveProviderWeeklyAvailability] @Provider, @Days, N'Asia/Kolkata', N'India Standard Time';
IF NOT EXISTS (SELECT 1 FROM [Provider].[WorkingHoursAtUtc](@Provider, '20300106 19:00')
               WHERE IsOpen = 1 AND OpensAtUtc = '20300106 18:30' AND ClosesAtUtc = '20300107 02:30')
    THROW 52000, 'India Monday midnight did not project onto Sunday UTC.', 1;
IF NOT EXISTS (SELECT 1 FROM [Provider].[ProviderWeeklyAvailability]
               WHERE ProviderId = @Provider AND DayOfWeek = 1 AND StartTime = '00:00' AND TimeZone = N'Asia/Kolkata')
    THROW 52000, 'Wall-clock hours did not round trip.', 1;
EXEC [Provider].[GetProviderWeeklyAvailability] @Provider;

EXEC [Provider].[SaveProviderWeeklyAvailability] @Provider, @Days, N'Europe/Zurich', N'W. Europe Standard Time';
IF NOT EXISTS (SELECT 1 FROM [Provider].[WorkingHoursAtUtc](@Provider, '20300106 23:30')
               WHERE IsOpen = 1 AND OpensAtUtc = '20300106 23:00' AND ClosesAtUtc = '20300107 07:00')
    THROW 52000, 'Zurich winter conversion failed.', 1;
IF NOT EXISTS (SELECT 1 FROM [Provider].[WorkingHoursAtUtc](@Provider, '20300707 22:30')
               WHERE IsOpen = 1 AND OpensAtUtc = '20300707 22:00' AND ClosesAtUtc = '20300708 06:00')
    THROW 52000, 'Zurich summer conversion failed.', 1;
SET @Days = REPLACE(@Days, '08:00:00', '23:59:00');
EXEC [Provider].[SaveProviderWeeklyAvailability] @Provider, @Days, N'Asia/Kolkata', N'India Standard Time';
IF NOT EXISTS (SELECT 1 FROM [Provider].[ProviderWeeklyAvailability]
               WHERE ProviderId = @Provider AND DayOfWeek = 1 AND StartTime = '00:00' AND EndTime = '23:59')
    THROW 52000, 'All-day hours could not be saved.', 1;
-- Old callers retain UTC semantics when the new parameters are omitted.
EXEC [Provider].[SaveProviderWeeklyAvailability] @Provider, @Days;
IF EXISTS (SELECT 1 FROM [Provider].[ProviderWeeklyAvailability] WHERE ProviderId = @Provider AND TimeZone <> N'UTC')
    THROW 52000, 'Legacy UTC default failed.', 1;

INSERT [Booking].[Bookings]
    (BookingId, ProviderId, ServiceId, ServiceCategory, SubCategory, BookingDate, StartTime, EndTime,
     Status, Source, CustomerName, CustomerMobileCountryCode, CustomerMobile, AnimalType, PetName, ServiceLocation, PricePerHour)
SELECT id, @Provider, @Service, N'PetSitter', N'PetHotel', '20200101', '09:00', '10:00',
       N'CONFIRMED', N'Custom', N'Test', N'+91', N'123456789', N'Dog', N'Pet', N'MyLocation', 10
FROM (VALUES (@Booking), (@Cancelled)) ids(id);
DECLARE @Payments INT = (SELECT COUNT(*) FROM [Booking].[BookingPayments]);
BEGIN TRY
    EXEC [Booking].[UpdateBookingStatus] @Booking, N'COMPLETED', N'Provider', @Other;
    THROW 52000, 'Wrong provider was allowed to complete a Custom booking.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF ERROR_NUMBER() <> 51121 THROW;
END CATCH;
BEGIN TRY
    EXEC [Booking].[UpdateBookingStatus] @Booking, N'CONFIRMED', N'Provider', @Provider;
    THROW 52000, 'Unsupported Custom transition was accepted.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF ERROR_NUMBER() <> 51122 THROW;
END CATCH;
EXEC [Booking].[UpdateBookingStatus] @Booking, N'COMPLETED', N'Provider', @Provider;
EXEC [Booking].[UpdateBookingStatus] @Cancelled, N'PROVIDER_CANCELLED', N'Provider', @Provider;
IF NOT EXISTS (SELECT 1 FROM [Booking].[Bookings] WHERE BookingId = @Booking AND Status = N'COMPLETED')
    THROW 52000, 'Custom completion was not persisted.', 1;
IF NOT EXISTS (SELECT 1 FROM [Booking].[Bookings] WHERE BookingId = @Cancelled AND Status = N'PROVIDER_CANCELLED' AND CancelledAtUtc IS NOT NULL)
    THROW 52000, 'Custom cancellation was not persisted.', 1;
IF EXISTS (SELECT 1 FROM [Booking].[Bookings] WHERE BookingId IN (@Booking, @Cancelled) AND (PayoutStatus <> N'Pending' OR PayoutId IS NOT NULL))
    THROW 52000, 'Custom lifecycle changed payout fields.', 1;
IF @Payments <> (SELECT COUNT(*) FROM [Booking].[BookingPayments])
    THROW 52000, 'Custom lifecycle created a payment.', 1;
IF (SELECT COUNT(*) FROM [Booking].[BookingStatusHistory] WHERE BookingId IN (@Booking, @Cancelled)) <> 2
    THROW 52000, 'Custom lifecycle audit entries are missing.', 1;
BEGIN TRY
    EXEC [Booking].[UpdateBookingStatus] @Booking, N'PROVIDER_CANCELLED', N'Provider', @Provider;
    THROW 52000, 'Terminal Custom booking was changed.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    IF ERROR_NUMBER() <> 51123 THROW;
END CATCH;
PRINT 'Passed SQL timezone, legacy compatibility, Custom lifecycle, ownership, audit and payment checks.';
