using Google.Api.Gax.Grpc;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;
using WriteResult = Google.Cloud.Firestore.V1.WriteResult;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Pawfront.Application.Notifications;
using Pawfront.Infrastructure.Firebase;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    checks++;
}

// The SQL request stages the deadline; rendering must preserve that instant and
// quote its exact review window, including when an earlier proposal governs.
foreach (var (deadline, duration) in new[]
{
    ("2026-10-18T15:30:00Z", "2 hours"),
    ("2026-10-18T16:00:00Z", "2 hours 30 minutes"),
    ("2026-10-19T13:30:00Z", "24 hours")
})
{
    var data = new Dictionary<string, string>
    {
        [NotificationDataKeys.RequestedAtUtc] = "2026-10-18T13:30:00Z",
        [NotificationDataKeys.ReviewByUtc] = deadline
    };
    var rendered = NotificationRenderer.Render(NotificationTypes.BookingModificationRequestedByParent,
        NotificationAudience.Provider, data)!;
    Check(rendered.Body.Contains($"Review it within {duration}"), "Modification push quotes the supplied deadline");
    Check(rendered.Data[NotificationDataKeys.ReviewByUtc] == deadline,
        "Modification app payload preserves the review deadline");
}

foreach (var audience in new[] { NotificationAudience.PetParent, NotificationAudience.Provider })
{
    var message = new PushMessage(audience, ["device-a", "device-b"], "A title", "A body",
        new Dictionary<string, string>
        {
            ["notificationId"] = Guid.NewGuid().ToString(), ["type"] = "MESSAGE_RECEIVED",
            ["conversationId"] = "conversation-1", ["bookingId"] = "", ["route"] = "/chat"
        }, null, "firebase-uid");
    var client = new RecordingFirestoreClient();
    var db = FirestoreDb.Create(audience.ToString().ToLowerInvariant(), client);
    await FirestoreNotificationWriter.CreateAsync(db, message, message.Data["notificationId"],
        message.Data["type"], default);
    var write = client.Request!.Writes.Single();
    Check(write.Update.Name == $"projects/{db.ProjectId}/databases/(default)/documents/users/firebase-uid/notifications/{message.Data["notificationId"]}", "Project and document identity");
    Check(write.CurrentDocument is { Exists: false }, "Create-only precondition protects read state on retries");
    Check(write.Update.Fields["title"].StringValue == message.Title && write.Update.Fields["body"].StringValue == message.Body, "Rendered copy preserved");
    Check(write.Update.Fields["type"].StringValue == message.Data["type"] && !write.Update.Fields["isRead"].BooleanValue, "Type and initial read state");
    var data = write.Update.Fields["data"].MapValue.Fields;
    Check(data.Count == message.Data.Count && message.Data.All(pair => data[pair.Key].StringValue == pair.Value), "Exact push payload preserved, including empty strings");
    Check(write.UpdateTransforms.Single().FieldPath == "createdAt" &&
          write.UpdateTransforms.Single().SetToServerValue == DocumentTransform.Types.FieldTransform.Types.ServerValue.RequestTime,
          "Server-side creation timestamp");

    client.Failure = new RpcException(new Status(StatusCode.AlreadyExists, "duplicate"));
    await FirestoreNotificationWriter.CreateAsync(db, message, message.Data["notificationId"], message.Data["type"], default);
    Check(client.Calls == 2, "Duplicate is accepted without an update");
    client.Failure = new RpcException(new Status(StatusCode.PermissionDenied, "denied"));
    try
    {
        await FirestoreNotificationWriter.CreateAsync(db, message, message.Data["notificationId"], message.Data["type"], default);
        throw new Exception("Permissions failure was swallowed");
    }
    catch (RpcException e) when (e.StatusCode == StatusCode.PermissionDenied) { checks++; }

    var inbox = new RecordingInboxWriter();
    var push = new RecordingPushSender();
    var sender = new InboxPushSender(push, inbox, NullLogger<InboxPushSender>.Instance);
    var pending = sender.SendAsync(message, default);
    Check(push.Calls == 1 && inbox.Calls == 1 && !pending.IsCompleted, "Push starts while Firestore is pending and writer is tracked");
    inbox.Completion.SetResult();
    Check(await pending == push.Result && inbox.Message == message, "Both audiences share exact message and FCM result");
    inbox = new RecordingInboxWriter();
    sender = new InboxPushSender(push, inbox, NullLogger<InboxPushSender>.Instance);
    inbox.Completion.SetException(new IOException("Firestore unavailable"));
    Check(await sender.SendAsync(message, default) == push.Result, "Firestore failure cannot fail a successful push");
    push.Result = PushSendResult.Failed("FCM unavailable");
    inbox = new RecordingInboxWriter();
    inbox.Completion.SetResult();
    sender = new InboxPushSender(push, inbox, NullLogger<InboxPushSender>.Instance);
    Check((await sender.SendAsync(message, default)).IsFailure && inbox.Calls == 1, "FCM failure still attempts inbox write");
}
checks += await NotificationApiChecks.RunAsync();
checks += await FirestoreReadChecks.RunAsync();
try { checks += await AccountCleanupChecks.RunAsync(); }
catch (Exception exception) { Console.Error.WriteLine(exception); Environment.Exit(1); }
checks += await CashNotificationChecks.RunAsync();
checks += EventNotificationCopyChecks.Run();

foreach (var type in new[] { NotificationTypes.EventBookingCashReceived, NotificationTypes.EventBookingPaymentPending })
{
    var bookingId=Guid.NewGuid(); var eventId=Guid.NewGuid();
    var data=new Dictionary<string,string> { ["type"]=type,["category"]="EVENT",["bookingId"]=bookingId.ToString(),
        ["entityId"]=bookingId.ToString(),["eventId"]=eventId.ToString(),["amount"]="100",["eventTitle"]="Play day",["hostName"]="Alex" };
    var rendered=NotificationRenderer.Render(type,NotificationAudience.PetParent,data)!;
    Check(rendered is not null && rendered.Category=="EVENT","Event cash type is registered");
    var notification=new ClaimedNotification(Guid.NewGuid(),NotificationAudience.PetParent,Guid.NewGuid(),type,
        "EventBooking",bookingId,System.Text.Json.JsonSerializer.Serialize(data),null,1,DateTimeOffset.UtcNow);
    var payload=NotificationPayloadBuilder.Build(notification,rendered!,DateTimeOffset.UtcNow);
    Check(data.All(pair=>payload[pair.Key]==pair.Value),"Cash payload retains exact app envelope and amount");
    var cashClient=new RecordingFirestoreClient();
    var cashDb=FirestoreDb.Create("parent",cashClient);
    var cashMessage=new PushMessage(NotificationAudience.PetParent,["device"],rendered!.Title,rendered.Body,payload,null,"attendee-uid");
    await FirestoreNotificationWriter.CreateAsync(cashDb,cashMessage,payload["notificationId"],type,default);
    var cashWrite=cashClient.Request!.Writes.Single().Update;
    Check(cashWrite.Name.Contains("/users/attendee-uid/notifications/") &&
        cashWrite.Fields["data"].MapValue.Fields["bookingId"].StringValue==bookingId.ToString() &&
        cashWrite.Fields["data"].MapValue.Fields["amount"].StringValue=="100","Attendee Firestore inbox receives cash payload");
}

// Schedules are stored as Swiss wall-clock readings; creation is a separate instant.
foreach (var (date, expectedTime) in new[] { ("2026-07-18", "16:30"), ("2026-12-18", "15:30") })
{
    var scheduled = new Dictionary<string, string>
    {
        [NotificationDataKeys.ServiceStartUtc] = date + "T" + expectedTime + ":00Z", // stored Swiss schedule for 18:00 IST
        [NotificationDataKeys.CreatedAtUtc] = date + "T10:30:00Z", // 16:00 IST
        [NotificationDataKeys.AcceptByUtc] = date + "T11:30:00Z"
    };
    foreach (var type in new[] { NotificationTypes.BookingRequested, NotificationTypes.NightStayBookingRequested })
    {
        var rendered = NotificationRenderer.Render(type, NotificationAudience.Provider, scheduled)!;
        Check(rendered.Body.Contains("at " + expectedTime), "Booking push quotes scheduled Swiss time: " + date);
        Check(rendered.Data[NotificationDataKeys.StartTime] == expectedTime, "Push body and app scheduled time agree");
    }
}

Console.WriteLine($"Passed {checks} notification checks.");

sealed class RecordingFirestoreClient : FirestoreClient
{
    public CommitRequest? Request;
    public Exception? Failure;
    public int Calls;
    public override Task<CommitResponse> CommitAsync(CommitRequest request, CallSettings? callSettings = null)
    {
        Calls++;
        Request = request;
        if (Failure is not null) return Task.FromException<CommitResponse>(Failure);
        var timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        var response = new CommitResponse { CommitTime = timestamp };
        response.WriteResults.Add(new WriteResult { UpdateTime = timestamp });
        return Task.FromResult(response);
    }
}

sealed class RecordingInboxWriter : INotificationInboxWriter
{
    public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public PushMessage? Message;
    public int Calls;
    public Task WriteAsync(PushMessage message, CancellationToken cancellationToken)
    {
        Calls++;
        Message = message;
        return Completion.Task.WaitAsync(cancellationToken);
    }
}

sealed class RecordingPushSender : IPushSender
{
    public int Calls;
    public PushSendResult Result = new(2, [], null);
    public Task<PushSendResult> SendAsync(PushMessage message, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}
