using Microsoft.Data.SqlClient;
using Pawfront.Application.Notifications;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Application.ProviderOnboarding;

static class AccountDeletionSqlChecks
{
    public static async Task<int> RunAsync()
    {
        const string cs = @"Server=(localdb)\MSSQLLocalDB;Database=PawfrontEmailBookingChecks;Integrated Security=true;Encrypt=false";
        var assembly = typeof(Pawfront.Infrastructure.Sql.SqlServiceRegistration).Assembly;
        T Make<T>(string type, params object?[] args) => (T)Activator.CreateInstance(assembly.GetType(type)!, args)!;
        var providers = Make<IProviderOnboardingService>("Pawfront.Infrastructure.Sql.ProviderOnboarding.SqlProviderOnboardingService", cs, null, null);
        var parents = Make<IParentOnboardingService>("Pawfront.Infrastructure.Sql.ParentOnboarding.SqlParentOnboardingService", cs, null, null);
        var cleanup = Make<IAccountNotificationCleanupStore>("Pawfront.Infrastructure.Sql.Notifications.SqlAccountNotificationCleanupStore", cs, null);
        var provider = Guid.NewGuid(); var parent = Guid.NewGuid(); var providerAuth = Guid.NewGuid(); var parentAuth = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N"); var providerUid = "delete-provider-" + suffix; var parentUid = "delete-parent-" + suffix;
        var service = Guid.NewGuid(); var booking = Guid.NewGuid(); var conversation = Guid.NewGuid();
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        await using var connection = new SqlConnection(cs); await connection.OpenAsync();
        SqlCommand Command(string sql)
        {
            var command = new SqlCommand(sql, connection);
            foreach (var (name, value) in new (string, object)[] { ("@Provider",provider),("@Parent",parent),("@ProviderAuth",providerAuth),("@ParentAuth",parentAuth),
                ("@ProviderUid",providerUid),("@ParentUid",parentUid),("@Service",service),("@Booking",booking),("@Conversation",conversation) })
                command.Parameters.AddWithValue(name,value);
            return command;
        }
        async Task Execute(string sql) { await using var command = Command(sql); await command.ExecuteNonQueryAsync(); }
        async Task<int> Count(string sql) { await using var command = Command(sql); return (int)(await command.ExecuteScalarAsync())!; }
        await Execute("""
            INSERT Provider.ProviderAuthIdentities(ProviderAuthIdentityId,FirebaseUserId,AuthProvider,Email)
            VALUES(@ProviderAuth,@ProviderUid,N'EmailPassword',CONCAT(@ProviderUid,N'@example.invalid'));
            INSERT Provider.Providers(ProviderId,ProviderAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth)
            VALUES(@Provider,@ProviderAuth,N'Delete',N'Provider',N'Other',N'+41',LEFT(CONVERT(NVARCHAR(36),@Provider),16),'1990-01-01');
            UPDATE Provider.ProviderAuthIdentities SET ProviderId=@Provider WHERE ProviderAuthIdentityId=@ProviderAuth;
            INSERT Parent.ParentAuthIdentities(ParentAuthIdentityId,FirebaseUserId,AuthProvider,Email)
            VALUES(@ParentAuth,@ParentUid,N'EmailPassword',CONCAT(@ParentUid,N'@example.invalid'));
            INSERT Parent.PetParents(PetParentId,ParentAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth,AddressLine,Latitude,Longitude,ZipCode,City,Description)
            VALUES(@Parent,@ParentAuth,N'Delete',N'Parent',N'Other',N'+41',LEFT(CONVERT(NVARCHAR(36),@Parent),16),'1990-01-01',N'Address',47,8,N'8000',N'Zurich',N'Test');
            UPDATE Parent.ParentAuthIdentities SET PetParentId=@Parent WHERE ParentAuthIdentityId=@ParentAuth;
            INSERT Provider.ProviderServices(ServiceId,ProviderId,ServiceCategory,SubCategory,ServiceType)
            VALUES(@Service,@Provider,N'PetSitter',N'Freelance',N'DayCare');
            INSERT Provider.ProviderServiceViews(ProviderId,ServiceId,PetParentId) VALUES(@Provider,@Service,@Parent);
            INSERT Chat.ChatConnections(ConnectionId,ParticipantType,ParticipantId)
            VALUES(CONCAT(N'p-',@Provider),N'Provider',@Provider),(CONCAT(N'u-',@Parent),N'PetParent',@Parent);
            EXEC Notification.EnqueueNotification @Audience=N'Provider',@RecipientId=@Provider,@NotificationType=N'BOOKING_REQUESTED';
            EXEC Notification.EnqueueNotification @Audience=N'PetParent',@RecipientId=@Parent,@NotificationType=N'BOOKING_REQUESTED';
            INSERT Booking.Bookings(BookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,BookingDate,StartTime,EndTime,Status,Source,PricePerHour)
            VALUES(@Booking,@Provider,@Parent,@Service,N'PetSitter',N'Freelance','2099-01-05','10:00','11:00',N'CONFIRMED',N'App',20);
            """);
        try { await parents.DeletePetParentAccountAsync(parent, default); throw new Exception("Deletion should be blocked by unfinished job"); }
        catch (PetParentPendingJobsException) { checks++; }
        Check(await cleanup.GetAsync(NotificationAudience.PetParent,parent,default) is null, "Blocked deletion does not queue remote cleanup");
        Check(await Count("SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE Audience=N'PetParent' AND RecipientId=@Parent") == 1,
            "Blocked deletion preserves notifications");
        Check(await Count("SELECT COUNT(*) FROM Chat.ChatConnections WHERE ParticipantId=@Parent") == 1, "Blocked deletion preserves live account data");
        await Execute("UPDATE Booking.Bookings SET Status=N'COMPLETED' WHERE BookingId=@Booking;");
        await parents.DeletePetParentAccountAsync(parent, default);
        var parentJob = await cleanup.GetAsync(NotificationAudience.PetParent,parent,default);
        Check(parentJob?.FirebaseUserId == parentUid, "Parent cleanup captures original UID before anonymisation");
        Check(await Count("SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE Audience=N'PetParent' AND RecipientId=@Parent") == 0, "Parent SQL notifications removed");
        Check(await Count("SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE Audience=N'Provider' AND RecipientId=@Provider") == 1, "Another user's notifications are preserved");
        Check(await Count("SELECT COUNT(*) FROM Provider.ProviderServiceViews WHERE PetParentId=@Parent") == 0, "Parent analytics traces removed");
        Check(await Count("SELECT COUNT(*) FROM Chat.ChatConnections WHERE ParticipantId=@Parent") == 0, "Parent live connections removed");
        await providers.DeleteProviderAccountAsync(provider, default);
        var providerJob = await cleanup.GetAsync(NotificationAudience.Provider,provider,default);
        Check(providerJob?.FirebaseUserId == providerUid, "Provider cleanup captures original UID before anonymisation");
        Check(await Count("SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE Audience=N'Provider' AND RecipientId=@Provider") == 0, "Provider SQL notifications removed");
        Check(await Count("SELECT COUNT(*) FROM Chat.ChatConnections WHERE ParticipantId=@Provider") == 0, "Provider live connections removed");
        Check(await Count("SELECT COUNT(*) FROM Booking.Bookings WHERE BookingId=@Booking") == 1, "Shared completed booking history survives deletion");
        await cleanup.FailAsync(providerJob!,default);
        Check(!(await cleanup.ListDueAsync(default)).Any(j => j.AccountId==provider), "Failed cleanup waits before retry");
        Check((await cleanup.GetAsync(NotificationAudience.Provider,provider,default))?.FirebaseUserId==providerUid, "Failed cleanup retains original inbox key for retry");
        await cleanup.CompleteAsync(providerJob!,default);
        Check(await cleanup.GetAsync(NotificationAudience.Provider,provider,default) is null, "Successful cleanup is no longer pending");
        Check(await Count("SELECT COUNT(*) FROM Notification.AccountNotificationCleanup WHERE AccountId=@Provider AND FirebaseUserId IS NULL AND CompletedAtUtc IS NOT NULL") == 1,
            "Successful cleanup erases retained Firebase UID");
        await providers.DeleteProviderAccountAsync(provider,default);
        Check(await cleanup.GetAsync(NotificationAudience.Provider,provider,default) is null, "Repeated deletion does not requeue completed cleanup");
        await Execute("""
            EXEC Notification.EnqueueNotification @Audience=N'Provider',@RecipientId=@Provider,@NotificationType=N'BOOKING_REQUESTED';
            EXEC Notification.EnqueueInstantNotification @Audience=N'PetParent',@RecipientId=@Parent,@NotificationType=N'MESSAGE_RECEIVED';
            """);
        Check(await Count("SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE RecipientId IN(@Provider,@Parent)") == 0, "Future notifications to deleted accounts are suppressed");
        return checks;
    }
}
