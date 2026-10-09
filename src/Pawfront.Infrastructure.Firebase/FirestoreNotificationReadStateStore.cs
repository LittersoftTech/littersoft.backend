using Google.Cloud.Firestore;
using Grpc.Core;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Firebase;

public sealed class FirestoreNotificationReadStateStore(FirebaseAppRegistry registry) : INotificationReadStateStore
{
    public async Task<bool> MarkReadAsync(NotificationAudience audience, string firebaseUserId,
        string notificationId, CancellationToken cancellationToken)
    {
        FirestoreNotificationReader.ValidateRequest(firebaseUserId, 1);
        if (string.IsNullOrWhiteSpace(notificationId) || notificationId.Contains('/')
            || notificationId is "." or ".." || System.Text.Encoding.UTF8.GetByteCount(notificationId) > 1500)
            throw new ArgumentException("A valid notification ID is required.");
        var db = await registry.GetFirestoreAsync(audience, cancellationToken);
        return await MarkReadAsync(db, firebaseUserId, notificationId, cancellationToken);
    }

    internal static async Task<bool> MarkReadAsync(FirestoreDb db, string uid, string id, CancellationToken cancellationToken)
    {
        try
        {
            await db.Collection("users").Document(uid).Collection("notifications").Document(id)
                .UpdateAsync(new Dictionary<string, object> { ["isRead"] = true }, cancellationToken: cancellationToken);
            return true;
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound)
        {
            return false; // Never create an incomplete document or disclose another user's inbox.
        }
    }

    public async Task MarkAllReadAsync(NotificationAudience audience, string firebaseUserId, CancellationToken cancellationToken)
    {
        FirestoreNotificationReader.ValidateRequest(firebaseUserId, 1);
        var db = await registry.GetFirestoreAsync(audience, cancellationToken);
        await MarkAllReadAsync(db, firebaseUserId, cancellationToken);
    }

    internal static async Task MarkAllReadAsync(FirestoreDb db, string uid, CancellationToken cancellationToken)
    {
        var collection = db.Collection("users").Document(uid).Collection("notifications");
        // Fixed cutoff excludes newly arriving notifications. Page without filtering
        // on isRead, so updates cannot move documents across pagination boundaries.
        var cutoff = Timestamp.GetCurrentTimestamp();
        DocumentSnapshot? last = null;
        while (true)
        {
            var query = collection.WhereLessThanOrEqualTo("createdAt", cutoff)
                .OrderBy("createdAt").OrderBy(FieldPath.DocumentId).Limit(400);
            if (last is not null) query = query.StartAfter(last);
            var page = await query.GetSnapshotAsync(cancellationToken);
            if (page.Count == 0) return;
            var batch = db.StartBatch();
            var count = 0;
            foreach (var document in page.Documents)
            {
                if (document.TryGetValue<bool>("isRead", out var read) && read) continue;
                batch.Update(document.Reference, new Dictionary<string, object> { ["isRead"] = true });
                count++;
            }
            if (count > 0) await batch.CommitAsync(cancellationToken);
            if (page.Count < 400) return;
            last = page.Documents[^1];
        }
    }
}
