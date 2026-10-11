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
checks += await CashNotificationChecks.RunAsync();
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
