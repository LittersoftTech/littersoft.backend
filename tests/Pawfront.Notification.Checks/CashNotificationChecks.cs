using Google.Cloud.Firestore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pawfront.Application.Notifications;
using Pawfront.Functions.Notifications;
using Pawfront.Infrastructure.Firebase;

internal static class CashNotificationChecks
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++;
        }

        foreach (var night in new[] { false, true })
        foreach (var hasDevice in new[] { false, true })
        {
            var bookingId = Guid.NewGuid();
            var parentId = Guid.NewGuid();
            var data = new Dictionary<string, string>
            {
                ["bookingId"] = bookingId.ToString(), ["parentId"] = parentId.ToString(),
                ["providerId"] = Guid.NewGuid().ToString(), ["petId"] = Guid.NewGuid().ToString(),
                ["bookingType"] = night ? "NightStay" : "SingleDay", ["payoutId"] = "PF-000001",
                ["providerName"] = "Provider", ["petName"] = "Pet", ["serviceName"] = "Boarding",
                ["amount"] = "CHF 20"
            };
            ClaimedNotification Notification(string type) => new(Guid.NewGuid(), NotificationAudience.PetParent,
                parentId, type, night ? "NightStayBooking" : "Booking", bookingId,
                NotificationPayloadBuilder.SerializeData(data), null, 1, DateTimeOffset.UtcNow, "parent-uid");
            var cash = Notification(NotificationTypes.BookingCashNotReceived);
            var paid = Notification(NotificationTypes.BookingPaid);
            var store = new RecordingCashOutbox(new([cash, paid], hasDevice
                ? [new(NotificationAudience.PetParent, parentId, "device", "Android")] : []));
            var inbox = new RecordingCashInbox();
            var push = new RecordingPushSender { Result = new(hasDevice ? 1 : 0, [], null) };
            var sender = new InboxPushSender(push, inbox, NullLogger<InboxPushSender>.Instance);
            var dispatcher = new NotificationDispatcher(store, sender, Options.Create(new NotificationOptions()),
                NullLogger<NotificationDispatcher>.Instance);
            var dispatched = await dispatcher.RunAsync(default);
            Check(dispatched.Claimed == 2 && (hasDevice ? dispatched.Sent == 2 : dispatched.NoDevice == 2),
                "Cash and paid events complete with and without devices");
            Check(inbox.Messages.Count == 2 && push.Calls == 2, "Both notifications reach push and Firestore transports");

            var message = inbox.Messages[0];
            var payload = message.Data;
            var paidData = inbox.Messages[1].Data;
            Check(payload.Keys.Order().SequenceEqual(paidData.Keys.Order()), "Same envelope keys as BOOKING_PAID");
            Check(payload["type"] == "BOOKING_CASH_NOT_RECEIVED" && payload["entityId"] == bookingId.ToString()
                && payload["bookingId"] == bookingId.ToString() && payload["category"] == "BOOKING",
                "Cash sheet event type and booking identity");
            Check(payload["entityType"] == (night ? "NightStayBooking" : "Booking")
                && payload["isNightStay"] == (night ? "true" : "false")
                && payload["route"] == paidData["route"], "Booking kind and routing match paid event");
            Check(message.Audience == NotificationAudience.PetParent && message.FirebaseUserId == "parent-uid"
                && message.Tokens.Count == (hasDevice ? 1 : 0), "Parent recipient and device routing");
            var client = new RecordingFirestoreClient();
            var db = FirestoreDb.Create("parent-app", client);
            await FirestoreNotificationWriter.CreateAsync(db, message, payload["notificationId"], payload["type"], default);
            var write = client.Request!.Writes.Single().Update;
            Check(write.Name.EndsWith($"/users/parent-uid/notifications/{cash.NotificationId}"), "Parent inbox path");
            Check(write.Fields["type"].StringValue == "BOOKING_CASH_NOT_RECEIVED"
                && write.Fields["data"].MapValue.Fields["entityId"].StringValue == bookingId.ToString(),
                "Firestore event carries the cash sheet contract");
            Check(payload.All(pair => write.Fields["data"].MapValue.Fields[pair.Key].StringValue == pair.Value),
                "Firestore data is identical to push data");
        }
        return checks;
    }
}

internal sealed class RecordingCashInbox : INotificationInboxWriter
{
    public List<PushMessage> Messages { get; } = [];
    public Task WriteAsync(PushMessage message, CancellationToken cancellationToken)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingCashOutbox(NotificationClaimBatch batch) : INotificationOutboxStore
{
    public Task<NotificationClaimBatch> ClaimAsync(int batchSize, int maxAttempts, int leaseMinutes,
        CancellationToken cancellationToken) => Task.FromResult(batch);
    public Task CompleteAsync(IReadOnlyList<NotificationDeliveryResult> results, int maxAttempts,
        CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DeactivateTokensAsync(IReadOnlyList<DeadDeviceToken> tokens,
        CancellationToken cancellationToken) => Task.CompletedTask;
}
