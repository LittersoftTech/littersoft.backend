using Google.Cloud.Firestore;
using Grpc.Core;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Firebase;

public interface INotificationInboxWriter
{
    Task WriteAsync(PushMessage message, CancellationToken cancellationToken);
}

/// <summary>Creates one immutable inbox entry; retries never reset read state.</summary>
public sealed class FirestoreNotificationWriter(FirebaseAppRegistry registry) : INotificationInboxWriter
{
    public async Task WriteAsync(PushMessage message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.FirebaseUserId) || message.FirebaseUserId.Contains('/'))
            throw new InvalidOperationException("Notification recipient has no valid Firebase UID.");
        if (!message.Data.TryGetValue("notificationId", out var id)
            || string.IsNullOrWhiteSpace(id) || id.Contains('/'))
            throw new InvalidOperationException("Notification payload has no valid notificationId.");
        if (!message.Data.TryGetValue("type", out var type) || string.IsNullOrWhiteSpace(type))
            throw new InvalidOperationException("Notification payload has no type.");

        var db = await registry.GetFirestoreAsync(message.Audience, cancellationToken);
        await CreateAsync(db, message, id, type, cancellationToken);
    }

    internal static async Task CreateAsync(FirestoreDb db, PushMessage message, string id,
        string type, CancellationToken cancellationToken)
    {
        var document = db.Collection("users").Document(message.FirebaseUserId)
            .Collection("notifications").Document(id);
        try
        {
            await document.CreateAsync(new Dictionary<string, object>
            {
                ["title"] = message.Title,
                ["body"] = message.Body,
                ["type"] = type,
                ["data"] = new Dictionary<string, string>(message.Data),
                ["createdAt"] = FieldValue.ServerTimestamp,
                ["isRead"] = false
            }, cancellationToken);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.AlreadyExists)
        {
            // Another device, retry, or overlapping worker already created this ID.
        }
    }
}
