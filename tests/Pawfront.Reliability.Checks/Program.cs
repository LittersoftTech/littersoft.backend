using System.Data;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Reliability;
using Pawfront.Contracts.Reliability;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
    Console.WriteLine($"PASS {name}");
}
var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
ServiceReliabilityMark[] History(params string[] outcomes) => outcomes.Select((outcome, i) =>
    new ServiceReliabilityMark("SingleDay", Guid.NewGuid(), outcome, now.AddHours(-i))).ToArray();
ServiceReliabilityResponse Calculate(params string[] outcomes) => ServiceReliabilityCalculator.Calculate(History(outcomes), now);
Check(Calculate().Score is null && !Calculate().IsRedFlagged, "empty history is unscored");
Check(Calculate("Unpaid", "Unpaid", "Unpaid", "Unpaid").Score is null, "four negative bookings cannot pass the gate");
var threshold = Calculate("NoShow", "Paid", "Paid", "Paid", "Paid");
Check(threshold.Score == 50 && threshold.MeetsVolumeGate && !threshold.IsRedFlagged, "four paid plus one no-show is scored at exactly 50");
var below = Calculate("Unpaid", "Paid", "Paid", "Paid", "Paid");
Check(below.Score == 40 && below.IsRedFlagged && below.RedFlagExpiresAtUtc == now.AddDays(30), "unpaid weight and flag clock");
Check(Calculate("LateCancellation", "Paid", "Paid", "Paid", "Paid").Score == 66.67m, "late cancellation weight is two");
Check(Calculate("Paid", "Paid", "Paid", "Paid", "Paid").Score == 100, "all paid history is 100");
Check(Calculate("NoShow", "NoShow", "NoShow", "NoShow", "NoShow").Score == 0, "all negative history is zero");
var bands = Calculate(Enumerable.Repeat("Paid", 11).ToArray());
Check(bands.WeightedPointsInFavour == 7.75m, "newest five full, next five half, older quarter");
var recentNegative = Calculate("NoShow", "Paid", "Paid", "Paid", "Paid", "Paid");
var oldNegative = Calculate("Paid", "Paid", "Paid", "Paid", "Paid", "NoShow");
Check(recentNegative.Score == 52.94m && oldNegative.Score == 71.43m, "outcome order changes recency weighting");
var old = History("Unpaid", "Paid", "Paid", "Paid", "Paid").Select(m => m with { OccurredAtUtc = m.OccurredAtUtc.AddDays(-30) }).ToArray();
var expired = ServiceReliabilityCalculator.Calculate(old, now);
Check(expired.Score == 40 && !expired.IsRedFlagged && expired.RedFlagExpiresAtUtc is null, "flag lifts at 30 days despite a low score");
Check(ServiceReliabilityCalculator.Calculate(old, now.AddTicks(-1)).IsRedFlagged, "flag remains immediately before expiry");
var rolling = History("Unpaid", "Paid", "Paid", "Paid", "Paid");
rolling[0] = rolling[0] with { OccurredAtUtc = now.AddDays(-180).AddTicks(-1) };
Check(ServiceReliabilityCalculator.Calculate(rolling, now).Score is null, "180-day ageing can roll the score below the volume gate");
rolling[0] = rolling[0] with { OccurredAtUtc = now.AddDays(-180) };
Check(ServiceReliabilityCalculator.Calculate(rolling, now).CountedBookings == 5, "180-day boundary is inclusive");
var recovery = Calculate("Paid", "NoShow", "NoShow", "NoShow", "NoShow");
Check(recovery.IsRedFlagged && recovery.AdditionalPaidBookingsToReachThreshold == 7, "recovery count reweights history after each positive outcome");
var future = History("Paid", "Paid", "Paid", "Paid", "Paid");
future[0] = future[0] with { OccurredAtUtc = now.AddSeconds(1) };
Check(ServiceReliabilityCalculator.Calculate(future, now).CountedBookings == 4, "future outcomes are not counted");
Check(!ServiceReliabilityCalculator.NotApplicable().IsApplicable && ServiceReliabilityCalculator.NotApplicable().Score is null, "provider service score is not applicable");
var json = JsonSerializer.Serialize(new ReliabilityResponse(below, new(false, null, 0, 0, 0, true, 5, 0, 5), new(false, 0, 0, now.AddMonths(-12)), now), new JsonSerializerOptions(JsonSerializerDefaults.Web));
Check(json.Contains("\"services\"") && json.Contains("\"community\"") && json.Contains("\"hosting\"") && json.Contains("\"score\":40"), "mobile response exposes independent sections and numeric score");

if (args.Contains("--sql"))
{
    const string connectionString = @"Server=(localdb)\MSSQLLocalDB;Database=PawfrontReliabilityChecks;Integrated Security=true;Encrypt=false;TrustServerCertificate=true";
    var readerType = Assembly.Load("Pawfront.Infrastructure.Sql").GetType("Pawfront.Infrastructure.Sql.Reliability.SqlReliabilityReader")!;
    var reader = (IReliabilityReader)Activator.CreateInstance(readerType, connectionString, null)!;
    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
    var parent = Guid.NewGuid(); var provider = Guid.NewGuid(); var service = Guid.NewGuid();
    async Task Execute(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
        await command.ExecuteNonQueryAsync();
    }
    await Execute("""
        DECLARE @ParentAuth UNIQUEIDENTIFIER=NEWID(), @ProviderAuth UNIQUEIDENTIFIER=NEWID();
        INSERT [Parent].[ParentAuthIdentities](ParentAuthIdentityId,FirebaseUserId,AuthProvider,Email)
        VALUES(@ParentAuth,CONVERT(nvarchar(36),@Parent),N'EmailPassword',CONCAT(@Parent,N'@test.invalid'));
        INSERT [Parent].[PetParents](PetParentId,ParentAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth,AddressLine,Latitude,Longitude,ZipCode,City,Description)
        VALUES(@Parent,@ParentAuth,N'Test',N'Parent',N'Other',N'+41',LEFT(CONVERT(nvarchar(36),@Parent),16),'1990-01-01',N'Address',47,8,N'8000',N'Zurich',N'Test');
        INSERT [Provider].[ProviderAuthIdentities](ProviderAuthIdentityId,FirebaseUserId,AuthProvider,Email)
        VALUES(@ProviderAuth,CONVERT(nvarchar(36),@Provider),N'EmailPassword',CONCAT(@Provider,N'@test.invalid'));
        INSERT [Provider].[Providers](ProviderId,ProviderAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth)
        VALUES(@Provider,@ProviderAuth,N'Test',N'Provider',N'Other',N'+41',LEFT(CONVERT(nvarchar(36),@Provider),16),'1990-01-01');
        INSERT [Provider].[ProviderServices](ServiceId,ProviderId,ServiceCategory,SubCategory,ServiceType)
        VALUES(@Service,@Provider,N'PetSitter',N'Freelance',N'DayCare');
        """, ("@Parent", parent), ("@Provider", provider), ("@Service", service));
    // All integration data is synthetic and rolled back. The reader joins this transaction's
    // connection via the stored procedure directly so uncommitted fixtures stay private.
    async Task<int> CountMarks()
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM [Booking].[ServiceReliabilityMarks] WHERE PetParentId=@Parent", connection, transaction);
        command.Parameters.AddWithValue("@Parent", parent);
        return (int)(await command.ExecuteScalarAsync())!;
    }
    async Task<Guid> Booking(string outcome, decimal price = 10, int? policy = 24, string source = "App")
    {
        var booking = Guid.NewGuid();
        await Execute("""
            INSERT [Booking].[Bookings](BookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,BookingDate,StartTime,EndTime,Status,Source,PricePerHour,CancellationPolicyHours,
                CustomerName,CustomerMobileCountryCode,CustomerMobile,AnimalType,PetName,ServiceLocation,CancelledAtUtc)
            VALUES(@Booking,@Provider,CASE WHEN @Source=N'App' THEN @Parent END,@Service,N'PetSitter',N'Freelance',CAST(SYSUTCDATETIME() AS DATE),'23:00','23:59',@Status,@Source,@Price,@Policy,
                CASE WHEN @Source=N'Custom' THEN N'Walkin' END,CASE WHEN @Source=N'Custom' THEN N'+41' END,CASE WHEN @Source=N'Custom' THEN N'1234567' END,
                CASE WHEN @Source=N'Custom' THEN N'Dog' END,CASE WHEN @Source=N'Custom' THEN N'Milo' END,CASE WHEN @Source=N'Custom' THEN N'MyLocation' END,
                CASE WHEN @Status IN (N'PARENT_CANCELLED',N'PROVIDER_CANCELLED') THEN SYSUTCDATETIME() END);
            INSERT [Booking].[BookingStatusHistory](BookingId,FromStatus,ToStatus,ChangedByActor)
            VALUES(@Booking,N'CONFIRMED',@Status,N'Provider');
            """, ("@Booking", booking), ("@Provider", provider), ("@Parent", parent), ("@Service", service),
            ("@Status", outcome), ("@Price", price), ("@Policy", policy is null ? DBNull.Value : policy.Value), ("@Source", source));
        return booking;
    }
    var unpaid = await Booking("PAYMENT_NOT_RECEIVED");
    Check(await CountMarks() == 1, "SQL captures unpaid service");
    await Execute("""
        UPDATE [Booking].[Bookings] SET Status=N'PAID' WHERE BookingId=@Booking;
        INSERT [Booking].[BookingStatusHistory](BookingId,FromStatus,ToStatus,ChangedByActor) VALUES(@Booking,N'PAYMENT_NOT_RECEIVED',N'PAID',N'Provider');
        IF NOT EXISTS (SELECT 1 FROM [Booking].[ServiceReliabilityMarks] WHERE BookingId=@Booking AND Outcome=N'Paid') THROW 50001,'Unpaid not replaced',1;
        """, ("@Booking", unpaid));
    Check(await CountMarks() == 1, "later payment replaces one unpaid mark");
    await Booking("PARENT_NO_SHOW"); await Booking("PARENT_CANCELLED");
    Check(await CountMarks() == 3, "SQL captures no-show and late cancellation");
    foreach (var outcome in new[] { "COMPLETED", "PROVIDER_CANCELLED", "PROVIDER_NO_SHOW", "PROVIDER_DECLINED", "EXPIRED", "OTP_MAX_ATTEMPTS_EXCEEDED" }) await Booking(outcome);
    await Booking("PARENT_CANCELLED", policy: null); await Booking("PAID", price: 0); await Booking("COMPLETED", source: "Custom");
    Check(await CountMarks() == 3, "unpaid-undeclared, provider outcomes, OTP closures, no-policy cancellation, free and private jobs do not count");
    await Execute("""
        DECLARE @Stay UNIQUEIDENTIFIER=NEWID();
        INSERT [Booking].[NightStayBookings](NightStayBookingId,ProviderId,PetParentId,ServiceId,ServiceCategory,SubCategory,CheckInDate,CheckOutDate,DropOffTime,PickUpTime,Status,CancellationPolicyHours)
        VALUES(@Stay,@Provider,@Parent,@Service,N'PetSitter',N'Freelance',CAST(SYSUTCDATETIME() AS DATE),DATEADD(day,1,CAST(SYSUTCDATETIME() AS DATE)),'23:00','10:00',N'PARENT_NO_SHOW',24);
        INSERT [Booking].[NightStayBookingStatusHistory](NightStayBookingId,FromStatus,ToStatus,ChangedByActor) VALUES(@Stay,N'CONFIRMED',N'PARENT_NO_SHOW',N'System');
        """, ("@Parent", parent), ("@Provider", provider), ("@Service", service));
    Check(await CountMarks() == 4, "night stays share the parent's service history");
    // Test actual multi-result shape inside the fixture transaction.
    await using (var command = new SqlCommand("Booking.GetAccountReliability", connection, transaction) { CommandType = CommandType.StoredProcedure })
    {
        command.Parameters.AddWithValue("@AccountId", parent); command.Parameters.AddWithValue("@AccountType", "PetParent");
        await using var result = await command.ExecuteReaderAsync();
        Check(await result.ReadAsync() && !result.GetBoolean(1), "host history defaults to no published events");
        Check(await result.NextResultAsync() && await result.ReadAsync() && !result.GetBoolean(0), "community state is independent of service marks");
        await result.NextResultAsync(); var count = 0; while (await result.ReadAsync()) count++;
        Check(count == 4, "SQL profile reliability returns service marks as its third result");
    }
    // Free events close without a mark, including when none of their tickets was paid.
    await Execute("""
        DECLARE @Event UNIQUEIDENTIFIER=NEWID(), @Booking UNIQUEIDENTIFIER=NEWID();
        INSERT [Event].[Events](EventId,PetParentId,EventCategory,IsChildFriendly,Title,Description,EventType,StartDate,EndDate,StartTime,EndTime,IsPaid)
        VALUES(@Event,@Parent,N'MeetupsAndSocials',1,N'Free',N'Test',N'Physical',DATEADD(day,-1,CAST(SYSUTCDATETIME() AS DATE)),DATEADD(day,-1,CAST(SYSUTCDATETIME() AS DATE)),'09:00','10:00',0);
        INSERT [Event].[EventBookings](BookingId,EventId,BookerName,BookerEmail,TicketCount,PaymentMethod,BookerAccountId,BookerType)
        VALUES(@Booking,@Event,N'Test',N'test@example.invalid',1,N'Cash',@Parent,N'PetParent');
        INSERT [Event].[EventBookingTickets](BookingId,EventId,TicketNumber,AttendeeName) VALUES(@Booking,@Event,1,N'Test');
        INSERT [Event].[Reliability](AccountId,LastNegativeAtUtc,ConsecutiveNegativeMarks,RedFlagExpiresAt,ConsecutivePaidEventsAttended)
        VALUES(@Parent,SYSUTCDATETIME(),1,DATEADD(day,30,SYSUTCDATETIME()),1);
        EXEC [Event].[SettleEventAttendance];
        IF EXISTS (SELECT 1 FROM [Event].[ReliabilityOutcomes] WHERE BookingId=@Booking) THROW 50001,'Free event generated a mark',1;
        IF NOT EXISTS (SELECT 1 FROM [Event].[EventBookings] WHERE BookingId=@Booking AND ReliabilitySettled=1 AND Status=N'Attended') THROW 50001,'Free event did not close',1;
        IF NOT EXISTS (SELECT 1 FROM [Event].[Reliability] WHERE AccountId=@Parent AND ConsecutiveNegativeMarks=1 AND ConsecutivePaidEventsAttended=1) THROW 50001,'Free event changed sequence',1;
        """, ("@Parent", parent));
    Check(true, "free events close without negative marks or sequence resets");
    await Execute("""
        DECLARE @Recent UNIQUEIDENTIFIER=NEWID(), @Old UNIQUEIDENTIFIER=NEWID(), @Unbooked UNIQUEIDENTIFIER=NEWID();
        INSERT [Event].[Events](EventId,PetParentId,EventCategory,IsChildFriendly,Title,Description,EventType,StartDate,EndDate,StartTime,EndTime,EventStatus,CancelledWithBookings,CancelledAtUtc,IsPaid)
        VALUES(@Recent,@Parent,N'MeetupsAndSocials',1,N'Recent',N'Test',N'Physical',CAST(SYSUTCDATETIME() AS DATE),CAST(SYSUTCDATETIME() AS DATE),'09:00','10:00',N'Cancelled',1,DATEADD(day,-1,SYSUTCDATETIME()),0),
              (@Old,@Parent,N'MeetupsAndSocials',1,N'Old',N'Test',N'Physical',CAST(SYSUTCDATETIME() AS DATE),CAST(SYSUTCDATETIME() AS DATE),'09:00','10:00',N'Cancelled',1,DATEADD(month,-13,SYSUTCDATETIME()),0),
              (@Unbooked,@Parent,N'MeetupsAndSocials',1,N'Unbooked',N'Test',N'Physical',CAST(SYSUTCDATETIME() AS DATE),CAST(SYSUTCDATETIME() AS DATE),'09:00','10:00',N'Cancelled',0,SYSUTCDATETIME(),0);
        EXEC [Event].[RemoveHostedEvent] @Recent,@Parent;
        """, ("@Parent", parent));
    await using (var command = new SqlCommand("Booking.GetAccountReliability", connection, transaction) { CommandType = CommandType.StoredProcedure })
    {
        command.Parameters.AddWithValue("@AccountId", parent); command.Parameters.AddWithValue("@AccountType", "PetParent");
        await using var result = await command.ExecuteReaderAsync(); await result.ReadAsync();
        Check(result.GetBoolean(1) && result.GetInt32(2) == 1 && result.GetInt32(3) == 1, "host history counts a free event run, excludes old/unbooked cancellations and survives removal");
        await result.NextResultAsync(); await result.ReadAsync();
        Check(result.GetBoolean(0) && result.GetInt32(2) == 1, "community flag and recovery progress are included independently");
    }
    await transaction.RollbackAsync();
    var parentResult = await reader.GetParentAsync(parent, default);
    var providerResult = await reader.GetProviderAsync(provider, default);
    Check(parentResult.Services.IsApplicable && parentResult.Services.Score is null && parentResult.Community.MaximumUpcomingBookings == 5, "SQL reader handles a new parent account");
    Check(!providerResult.Services.IsApplicable && providerResult.Hosting.EventsRun == 0, "SQL reader handles a provider without inventing a service score");
}
Console.WriteLine($"{checks} reliability checks passed.");
