using Google.Cloud.Firestore;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Firebase;

public sealed class FirestoreNotificationDeleter(FirebaseAppRegistry registry) : INotificationInboxDeleter
{
    public async Task DeleteAsync(AccountNotificationCleanupJob job, CancellationToken cancellationToken)
    {
        var db = await registry.GetFirestoreAsync(job.Audience, cancellationToken);
        await DeleteAsync(db, job, cancellationToken);
    }

    internal static async Task DeleteAsync(FirestoreDb db, AccountNotificationCleanupJob job, CancellationToken ct)
    {
        FirestoreNotificationReader.ValidateRequest(job.FirebaseUserId, 1);
        var user = db.Collection("users").Document(job.FirebaseUserId);
        // A retained marker prevents an already-claimed old notification from recreating the inbox.
        // It is scoped to the SQL account, so signing up again with the same Firebase UID works.
        await user.Collection("deletedAccounts").Document(job.AccountId.ToString()).SetAsync(
            new Dictionary<string, object> { ["deletedAt"] = Timestamp.FromDateTimeOffset(job.DeletedAtUtc) }, cancellationToken: ct);

        DocumentSnapshot? last = null;
        while (true)
        {
            var query = user.Collection("notifications").OrderBy(FieldPath.DocumentId).Limit(400);
            if (last is not null) query = query.StartAfter(last);
            var page = await query.GetSnapshotAsync(ct);
            if (page.Count == 0) return;
            var batch = db.StartBatch();
            var count = 0;
            foreach (var document in page.Documents)
            {
                var recipientAccount = Guid.Empty;
                var hasOwner = document.TryGetValue<Dictionary<string, string>>("data", out var data)
                    && data.TryGetValue("recipientId", out var recipient) && Guid.TryParse(recipient, out recipientAccount);
                var createdAt = document.TryGetValue<Timestamp>("createdAt", out var created)
                    ? created.ToDateTimeOffset() : document.CreateTime?.ToDateTimeOffset();
                var belongsToAccount = hasOwner ? recipientAccount == job.AccountId
                    : createdAt is null || createdAt <= job.DeletedAtUtc;
                if (!belongsToAccount) continue;
                batch.Delete(document.Reference);
                count++;
            }
            if (count > 0) await batch.CommitAsync(ct);
            if (page.Count < 400) return;
            last = page.Documents[^1];
        }
    }
}
