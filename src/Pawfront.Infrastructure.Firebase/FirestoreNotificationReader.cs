using System.Text.Json;
using Google.Cloud.Firestore;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Firebase;

public sealed class FirestoreNotificationReader(FirebaseAppRegistry registry) : INotificationInboxReader
{
    public async Task<NotificationInboxPage> ListAsync(NotificationAudience audience, string firebaseUserId,
        int take, string? cursor, CancellationToken cancellationToken)
    {
        ValidateRequest(firebaseUserId, take);
        var position = DecodeCursor(cursor, audience, firebaseUserId);
        var db = await registry.GetFirestoreAsync(audience, cancellationToken);
        return await ReadPageAsync(db, audience, firebaseUserId, take, position, cancellationToken);
    }

    internal static async Task<NotificationInboxPage> ReadPageAsync(FirestoreDb db,
        NotificationAudience audience, string firebaseUserId, int take, InboxCursor? position,
        CancellationToken cancellationToken)
    {
        var collection = db.Collection("users").Document(firebaseUserId).Collection("notifications");
        var query = collection.OrderByDescending("createdAt").OrderByDescending(FieldPath.DocumentId)
            .Limit(take + 1);
        if (position is not null)
        {
            query = query.StartAfter(
                Timestamp.FromDateTime(new DateTime(position.Ticks, DateTimeKind.Utc)),
                collection.Document(position.Id));
        }

        var snapshot = await query.GetSnapshotAsync(cancellationToken);
        return BuildPage(snapshot.Documents, take, audience, firebaseUserId);
    }

    internal static NotificationInboxPage BuildPage(IReadOnlyList<DocumentSnapshot> documents, int take,
        NotificationAudience audience, string firebaseUserId)
    {
        var items = documents.Take(take).Select(document => new NotificationInboxItem(
            document.Id, document.GetValue<string>("title"), document.GetValue<string>("body"),
            document.GetValue<string>("type"), document.GetValue<Dictionary<string, string>>("data"),
            document.GetValue<Timestamp>("createdAt").ToDateTimeOffset(), document.GetValue<bool>("isRead")))
            .ToArray();
        string? nextCursor = null;
        if (documents.Count > take)
        {
            var last = documents[take - 1];
            nextCursor = EncodeCursor(new InboxCursor(audience, firebaseUserId,
                last.GetValue<Timestamp>("createdAt").ToDateTime().Ticks, last.Id));
        }
        return new NotificationInboxPage(items, nextCursor);
    }

    internal static void ValidateRequest(string firebaseUserId, int take)
    {
        if (string.IsNullOrWhiteSpace(firebaseUserId) || firebaseUserId.Contains('/'))
            throw new ArgumentException("A valid Firebase user ID is required.");
        if (take is < 1 or > 100)
            throw new ArgumentException("take must be between 1 and 100.");
    }

    internal sealed record InboxCursor(NotificationAudience Audience, string Uid, long Ticks, string Id);

    internal static string EncodeCursor(InboxCursor cursor) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static InboxCursor? DecodeCursor(string? value, NotificationAudience audience, string uid)
    {
        if (value is null) return null;
        try
        {
            if (value.Length is 0 or > 2048) throw new FormatException();
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            var cursor = JsonSerializer.Deserialize<InboxCursor>(Convert.FromBase64String(base64));
            if (cursor is null || cursor.Audience != audience || cursor.Uid != uid
                || cursor.Ticks < DateTime.MinValue.Ticks || cursor.Ticks > DateTime.MaxValue.Ticks
                || string.IsNullOrWhiteSpace(cursor.Id) || cursor.Id.Length > 1500 || cursor.Id.Contains('/'))
                throw new FormatException();
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("Invalid notification cursor. Use nextCursor from the previous page.", nameof(value));
        }
    }
}
