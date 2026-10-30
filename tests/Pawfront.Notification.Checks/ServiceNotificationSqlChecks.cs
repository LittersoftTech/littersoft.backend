using System.Text.Json;
using Microsoft.Data.SqlClient;

internal static class ServiceNotificationSqlChecks
{
    // Dedicated disposable-data database; never reads an application connection string.
    private const string ConnectionString = @"Server=(localdb)\MSSQLLocalDB;Database=PawfrontNotificationV4Checks;Integrated Security=true;Encrypt=false";

    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception(name);
            checks++;
        }
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        async Task<object?> Query(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            return await command.ExecuteScalarAsync();
        }
        async Task<int> Count(Guid booking, string type) => Convert.ToInt32(await Query(
            "SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE EntityId=@Id AND NotificationType=@Type",
            ("@Id", booking), ("@Type", type)));
        async Task<string?> Code(Guid booking, string type) => (string?)await Query(
            "SELECT TOP(1) JSON_VALUE(DataJson,'$.notificationCode') FROM Notification.NotificationOutbox WHERE EntityId=@Id AND NotificationType=@Type ORDER BY CreatedAtUtc DESC",
            ("@Id", booking), ("@Type", type));

        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "notifications-comparison.json")));
        foreach (var card in fixture.RootElement.EnumerateArray().Where(c => c.GetProperty("code").GetString()!.StartsWith("P-") || c.GetProperty("code").GetString()!.StartsWith("V-")))
        {
            var parameters = card.GetProperty("parameters");
            string? Value(string key) => parameters.TryGetProperty(key, out var value) ? value.GetString() : null;
            var code = card.GetProperty("code").GetString()!;
            Check((string?)await Query("SELECT Notification.ServiceNotificationCode(@T,@A,@Absent,@Reason,@Scenario)",
                ("@T", card.GetProperty("type").GetString()), ("@A", card.GetProperty("audience").GetString()),
                ("@Absent", Value("absentParty")), ("@Reason", Value("noShowReason")), ("@Scenario", Value("completionScenario"))) == code,
                "SQL and renderer select the same card: " + code);
            Check(Convert.ToInt32(await Query("SELECT DelaySeconds FROM Notification.ServiceNotificationDelivery WHERE Code=@Code", ("@Code", code))) == card.GetProperty("delay").GetInt32(),
                "Configured delivery delay: " + code);
        }

        var provider = Guid.NewGuid(); var parent = Guid.NewGuid(); var service = Guid.NewGuid(); var nightService = Guid.NewGuid();
        await Query("""
            DECLARE @A uniqueidentifier=NEWID(),@P uniqueidentifier=NEWID();
            INSERT Provider.ProviderAuthIdentities(ProviderAuthIdentityId,FirebaseUserId,AuthProvider,Email)
            VALUES(@A,CONVERT(nvarchar(36),@Provider),N'EmailPassword',CONCAT(@Provider,N'@test.invalid'));
            INSERT Provider.Providers(ProviderId,ProviderAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth)
            VALUES(@Provider,@A,N'Alex',N'Provider',N'Other',N'+41',LEFT(CONVERT(nvarchar(36),@Provider),32),'1990-01-01');
            UPDATE Provider.ProviderAuthIdentities SET ProviderId=@Provider WHERE ProviderAuthIdentityId=@A;
            INSERT Parent.ParentAuthIdentities(ParentAuthIdentityId,FirebaseUserId,AuthProvider,Email)
            VALUES(@P,CONVERT(nvarchar(36),@Parent),N'EmailPassword',CONCAT(@Parent,N'@test.invalid'));
            INSERT Parent.PetParents(PetParentId,ParentAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth,AddressLine,Latitude,Longitude,ZipCode,City,Description)
            VALUES(@Parent,@P,N'Sam',N'Parent',N'Other',N'+41',LEFT(CONVERT(nvarchar(36),@Parent),32),'1990-01-01',N'Address',47,8,N'8000',N'Zurich',N'Test');
            INSERT Provider.ProviderServices(ServiceId,ProviderId,ServiceCategory,SubCategory,ServiceType)
            VALUES(@Service,@Provider,N'PetSitter',N'PetHotel',N'DayCare'),(@NightService,@Provider,N'PetSitter',N'PetHotel',N'NightStay');
            """, ("@Provider", provider), ("@Parent", parent), ("@Service", service), ("@NightService", nightService));
        async Task<Guid> Seed(bool night = false, string status = "IN_PROGRESS", bool reminder = false)
        {
            var id = Guid.NewGuid();
            await Query(night ? """
                INSERT Booking.NightStayBookings(NightStayBookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,CheckInDate,CheckOutDate,DropOffTime,PickUpTime,Status,PricePerNight)
                VALUES(@Id,@Provider,@Parent,@Service,N'PetSitter',N'PetHotel',CAST(DATEADD(day,-1,SYSUTCDATETIME()) AS date),CAST(SYSUTCDATETIME() AS date),'10:00','12:30',@Status,20);
                """ : """
                INSERT Booking.Bookings(BookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,BookingDate,StartTime,EndTime,Status,PricePerHour,WorkingHoursClosesAtUtc)
                VALUES(@Id,@Provider,@Parent,@Service,N'PetSitter',N'PetHotel',CASE WHEN @Reminder=1 THEN CAST('20300110' AS date) ELSE CAST(SYSUTCDATETIME() AS date) END,'12:00','12:30',@Status,40,
                    CASE WHEN @Reminder=1 THEN CAST('2030-01-10T13:00:00' AS datetime2) ELSE NULL END);
                """, ("@Id", id), ("@Provider", provider), ("@Parent", parent), ("@Service", night ? nightService : service), ("@Status", status), ("@Reminder", reminder));
            return id;
        }
        async Task Complete(Guid id, bool night, bool handOver) => await Query(night
            ? "EXEC Booking.CompleteNightStayBooking @Id,@Provider,@HandOver"
            : "EXEC Booking.CompleteBooking @Id,@Provider,@HandOver", ("@Id", id), ("@Provider", provider), ("@HandOver", handOver));

        // Timer boundaries, dedupe, and suppression if the job completes before dispatch.
        var reminder = await Seed(reminder: true);
        await Query("INSERT Booking.BookingStartOtps(BookingId,OtpCode,ExpiresAtUtc,Status,ConsumedAtUtc) VALUES(@Id,N'123456',DATEADD(day,1,SYSUTCDATETIME()),N'Consumed',SYSUTCDATETIME())", ("@Id", reminder));
        await Query("EXEC Booking.SendBookingReminders @AsOfUtc='2030-01-10T12:39:59'");
        Check(await Count(reminder, "BOOKING_COMPLETION_OVERDUE") == 0, "V-S15 excluded before end + ten minutes");
        await Query("EXEC Booking.SendBookingReminders @AsOfUtc='2030-01-10T12:40:00'");
        Check(await Count(reminder, "BOOKING_COMPLETION_OVERDUE") == 1 && await Code(reminder, "BOOKING_COMPLETION_OVERDUE") == "V-S15", "V-S15 at exact due boundary");
        await Query("EXEC Booking.SendBookingReminders @AsOfUtc='2030-01-10T12:49:59'");
        Check(await Count(reminder, "BOOKING_NOT_MARKED_COMPLETE") == 0, "V-S12 excluded before closure minus ten minutes");
        await Query("EXEC Booking.SendBookingReminders @AsOfUtc='2030-01-10T12:50:00'");
        await Query("EXEC Booking.SendBookingReminders @AsOfUtc='2030-01-10T12:50:01'");
        Check(await Count(reminder, "BOOKING_NOT_MARKED_COMPLETE") == 1 && await Count(reminder, "BOOKING_COMPLETION_OVERDUE") == 1, "Distinct reminders are idempotent");
        await Complete(reminder, false, false);
        await Query("EXEC Notification.ClaimPendingNotifications");
        Check(Convert.ToInt32(await Query("SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE EntityId=@Id AND NotificationType IN(N'BOOKING_NOT_MARKED_COMPLETE',N'BOOKING_COMPLETION_OVERDUE') AND Status=N'Failed'", ("@Id", reminder))) == 2,
            "Completed job retires both pending reminders before delivery");

        foreach (var night in new[] { false, true })
        {
            foreach (var (fromStatus, waiting, explicitReason, expected) in new (string, bool, string?, string)[]
            {
                ("CONFIRMED", false, null, "P-U9"), ("START_JOB", false, null, "P-U10"), ("START_JOB", true, null, "P-U9"),
                ("START_JOB", true, "OtpNotShared", "P-U10"), ("START_JOB", false, "NotArrived", "P-U9")
            })
            {
                var noShow = await Seed(night, fromStatus);
                await Query(night
                    ? "UPDATE Booking.NightStayBookings SET CheckInDate=CAST(DATEADD(day,-2,SYSUTCDATETIME()) AS date),CheckOutDate=CAST(DATEADD(day,-1,SYSUTCDATETIME()) AS date) WHERE NightStayBookingId=@Id"
                    : "UPDATE Booking.Bookings SET BookingDate=CAST(DATEADD(day,-1,SYSUTCDATETIME()) AS date) WHERE BookingId=@Id", ("@Id", noShow));
                if (waiting)
                    await Query(night
                        ? "INSERT Booking.NightStayBookingStatusHistory(NightStayBookingId,FromStatus,ToStatus,ChangedByActor,ChangedByActorId,Note) VALUES(@Id,N'START_JOB',N'START_JOB',N'Provider',@Provider,N'Waiting for customer')"
                        : "INSERT Booking.BookingStatusHistory(BookingId,FromStatus,ToStatus,ChangedByActor,ChangedByActorId,Note) VALUES(@Id,N'START_JOB',N'START_JOB',N'Provider',@Provider,N'Waiting for customer')", ("@Id", noShow), ("@Provider", provider));
                await Query(night
                    ? "EXEC Booking.UpdateNightStayBookingStatus @NightStayBookingId=@Id,@NewStatus=N'PARENT_NO_SHOW',@Actor=N'Provider',@ActorId=@Provider,@Latitude=47,@Longitude=8,@NoShowReason=@Reason"
                    : "EXEC Booking.UpdateBookingStatus @BookingId=@Id,@NewStatus=N'PARENT_NO_SHOW',@Actor=N'Provider',@ActorId=@Provider,@Latitude=47,@Longitude=8,@NoShowReason=@Reason", ("@Id", noShow), ("@Provider", provider), ("@Reason", explicitReason));
                Check(await Code(noShow, "BOOKING_NO_SHOW_REPORTED") == expected, "Manual no-show cause selects " + expected + ": " + night);
                Check(Convert.ToInt32(await Query("SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE EntityId=@Id AND NotificationType=N'BOOKING_NO_SHOW_REPORTED' AND Audience=N'PetParent' AND RecipientId=@Parent", ("@Id", noShow), ("@Parent", parent))) == 1,
                    "Manual no-show goes only to the parent counterparty");
            }
            var id = await Seed(night);
            await Complete(id, night, false);
            Check(await Code(id, "BOOKING_COMPLETED") == "P-U11", "Completion without collection: " + night);
            var handOver = await Seed(night);
            try { await Complete(handOver, night, true); throw new Exception("Missing evidence accepted"); }
            catch (SqlException exception) when (exception.Number == (night ? 51253 : 51133))
            {
                await Query("IF @@TRANCOUNT>0 ROLLBACK TRANSACTION;");
                Check(await Count(handOver, "BOOKING_COMPLETED") == 0, "No hand-over message without photo");
            }
            await Query(night
                ? "INSERT Booking.NightStayBookingEvidence(NightStayBookingId,PhotoUrl) VALUES(@Id,N'https://test.invalid/photo.jpg')"
                : "INSERT Booking.BookingEvidence(BookingId,PhotoUrl) VALUES(@Id,N'https://test.invalid/photo.jpg')", ("@Id", handOver));
            await Complete(handOver, night, true);
            Check(await Code(handOver, "BOOKING_COMPLETED") == "P-U12", "Photo-evidenced hand-over: " + night);

            // Provider and parent tickets each notify only their own raiser, once.
            foreach (var audience in new[] { "Provider", "PetParent" })
            {
                var ticket = Guid.NewGuid();
                await Query("""
                    INSERT Support.Tickets(TicketId,TicketType,ProviderId,PetParentId,RaisedByType,BookingType,BookingId)
                    VALUES(@Ticket,N'BookingIncident',@Provider,@Parent,@Audience,@Kind,@Id);
                    EXEC Support.CloseTicket @Ticket;
                    EXEC Support.CloseTicket @Ticket;
                    """, ("@Ticket", ticket), ("@Id", id), ("@Provider", provider), ("@Parent", parent), ("@Audience", audience), ("@Kind", night ? "NightStay" : "SingleDay"));
                Check(Convert.ToInt32(await Query("SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE NotificationType=N'DISPUTE_RESOLVED' AND JSON_VALUE(DataJson,'$.ticketId')=CONVERT(nvarchar(36),@Ticket) AND Audience=@Audience AND RecipientId=@Recipient",
                    ("@Ticket", ticket), ("@Audience", audience), ("@Recipient", audience == "Provider" ? provider : parent))) == 1, "Resolution goes once to ticket raiser");
            }

            // A real support report triggers the unpaid notice; retries share the ticket dedupe key.
            var unpaid = await Seed(night, "COMPLETED");
            await Query("EXEC Notification.EnqueueBookingNotification @Id,@Night,N'PetParent',N'BOOKING_CASH_NOT_RECEIVED'", ("@Id", unpaid), ("@Night", night));
            Check(await Count(unpaid, "BOOKING_CASH_NOT_RECEIVED") == 0, "Standalone unpaid state waits for its ticket");
            await Query("EXEC Support.CreateTicket @TicketType=N'BookingIncident',@RaisedByType=N'Provider',@ActorId=@Provider,@BookingType=@Kind,@BookingId=@Id,@Category=N'Cash Not Received'",
                ("@Provider", provider), ("@Kind", night ? "NightStay" : "SingleDay"), ("@Id", unpaid));
            await Query("EXEC Notification.EnqueueBookingNotification @Id,@Night,N'PetParent',N'BOOKING_CASH_NOT_RECEIVED'", ("@Id", unpaid), ("@Night", night));
            Check(await Count(unpaid, "BOOKING_CASH_NOT_RECEIVED") == 1 && await Code(unpaid, "BOOKING_CASH_NOT_RECEIVED") == "P-U15", "Ticket and unpaid retries produce one P-U15");

            var conflicting = await Seed(night, "COMPLETED");
            await Query("EXEC Support.CreateTicket @TicketType=N'BookingIncident',@RaisedByType=N'Provider',@ActorId=@Provider,@BookingType=@Kind,@BookingId=@Id,@Category=N'Other'",
                ("@Provider", provider), ("@Kind", night ? "NightStay" : "SingleDay"), ("@Id", conflicting));
            await Query("EXEC Support.CreateTicket @TicketType=N'BookingIncident',@RaisedByType=N'Provider',@ActorId=@Provider,@BookingType=@Kind,@BookingId=@Id,@Category=N'Cash Not Received'",
                ("@Provider", provider), ("@Kind", night ? "NightStay" : "SingleDay"), ("@Id", conflicting));
            Check(await Count(conflicting, "BOOKING_CASH_NOT_RECEIVED") == 0, "Rejected cash report cannot reuse an unrelated open ticket");

            // Both invoice recipients are notified only when their own file exists.
            foreach (var audience in new[] { "Provider", "PetParent" })
            {
                var invoice = Guid.NewGuid();
                await Query("""
                    INSERT Billing.Invoices(InvoiceId,InvoiceNumber,BookingType,BookingId,Recipient,ProviderId,PetParentId,Amount)
                    VALUES(@Invoice,CONVERT(nvarchar(36),@Invoice),@Kind,@Id,@Audience,@Provider,@Parent,20);
                    """, ("@Invoice", invoice), ("@Kind", night ? "NightStay" : "SingleDay"), ("@Id", handOver), ("@Audience", audience), ("@Provider", provider), ("@Parent", parent));
                Check(Convert.ToInt32(await Query("SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE EntityId=@Id AND NotificationType=N'INVOICE_ISSUED' AND Audience=@Audience", ("@Id", handOver), ("@Audience", audience))) == 0, "Pending PDF has no invoice-ready notice");
                await Query("EXEC Billing.CompleteInvoiceGeneration @Invoice,1,N'https://test.invalid/invoice.pdf'; EXEC Billing.CompleteInvoiceGeneration @Invoice,1,N'https://test.invalid/invoice.pdf';", ("@Invoice", invoice));
                Check(Convert.ToInt32(await Query("SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE EntityId=@Id AND NotificationType=N'INVOICE_ISSUED' AND Audience=@Audience AND JSON_VALUE(DataJson,'$.notificationCode')=@Code AND DATEDIFF(second,CreatedAtUtc,NextAttemptAtUtc) BETWEEN 59 AND 61",
                    ("@Id", handOver), ("@Audience", audience), ("@Code", audience == "Provider" ? "V-S14" : "P-S16"))) == 1, "Generated invoice announced once with sixty-second delay");
            }
        }

        var stayReminder = await Seed(true);
        await Query("""
            UPDATE Booking.NightStayBookings SET CheckInDate='20300109',CheckOutDate='20300110' WHERE NightStayBookingId=@Id;
            INSERT Booking.NightStayBookingStartOtps(NightStayBookingId,OtpCode,ExpiresAtUtc,Status,ConsumedAtUtc)
            VALUES(@Id,N'123456',DATEADD(day,1,SYSUTCDATETIME()),N'Consumed',SYSUTCDATETIME());
            EXEC Booking.SendBookingReminders @AsOfUtc='2030-01-10T12:39:59';
            """, ("@Id", stayReminder));
        Check(await Count(stayReminder, "BOOKING_COMPLETION_OVERDUE") == 0, "Night stay excluded before pickup plus ten minutes");
        await Query("EXEC Booking.SendBookingReminders @AsOfUtc='2030-01-10T12:40:00'");
        Check(await Count(stayReminder, "BOOKING_COMPLETION_OVERDUE") == 1, "Night stay receives V-S15 after scheduled pickup");

        // Exercise real intentional delivery times, including audience-specific no-show delays.
        var timing = await Seed();
        foreach (var (audience, absent, expected, delay) in new[]
        {
            ("PetParent", "the provider", "P-S8", 15), ("Provider", "the provider", "V-S9", 60),
            ("PetParent", "the customer", "P-S12", 15), ("Provider", "the customer", "V-S11", 15)
        })
        {
            await Query("EXEC Notification.EnqueueBookingNotification @Id,0,@Audience,N'BOOKING_NO_SHOW_AUTO_SETTLED',@AbsentParty=@Absent,@DedupeSuffix=@Suffix",
                ("@Id", timing), ("@Audience", audience), ("@Absent", absent), ("@Suffix", expected));
            Check(Convert.ToInt32(await Query("SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE EntityId=@Id AND JSON_VALUE(DataJson,'$.notificationCode')=@Code AND DATEDIFF(second,CreatedAtUtc,NextAttemptAtUtc) BETWEEN @Delay-1 AND @Delay+1",
                ("@Id", timing), ("@Code", expected), ("@Delay", delay))) == 1, "Automatic no-show recipient and delay: " + expected);
        }
        foreach (var type in new[] { "BOOKING_PRESCRIPTION_RECORDED", "EVENT_BOOKING_CONFIRMED", "EVENT_BOOKING_CANCELLED", "EVENT_BOOKING_PAYMENT_SUCCEEDED", "EVENT_BOOKING_PAYMENT_FAILED", "EVENT_TICKET_SOLD", "EVENT_TICKET_CANCELLED" })
        {
            await Query("EXEC Notification.EnqueueNotification @Audience=N'PetParent',@RecipientId=@Parent,@NotificationType=@Type,@EntityId=@Id,@SuppressResultSet=1", ("@Parent", parent), ("@Type", type), ("@Id", timing));
        }
        await Query("EXEC Notification.ClaimPendingNotifications");
        Check(Convert.ToInt32(await Query("SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE EntityId=@Id AND NotificationType=N'BOOKING_PRESCRIPTION_RECORDED'", ("@Id", timing))) == 0, "Retired prescription never enqueues");
        Check(Convert.ToInt32(await Query("SELECT COUNT(*) FROM Notification.NotificationOutbox WHERE EntityId=@Id AND NotificationType IN(N'EVENT_BOOKING_CONFIRMED',N'EVENT_BOOKING_CANCELLED',N'EVENT_BOOKING_PAYMENT_SUCCEEDED',N'EVENT_BOOKING_PAYMENT_FAILED',N'EVENT_TICKET_SOLD',N'EVENT_TICKET_CANCELLED') AND Status=N'Failed'", ("@Id", timing))) == 6, "All six retired event types suppressed");
        Console.WriteLine($"Passed {checks} Services V4 SQL checks.");
        return checks;
    }
}
