using System.Text.Json;

namespace Pawfront.Application.Notifications;

/// <summary>
/// Assembles the FCM <c>data</c> payload — the deep-linking contract the mobile
/// apps code against.
///
/// It lives in Application rather than in the Firebase adapter because it IS the
/// wire contract: it should be reviewable and testable without a Firebase
/// dependency, and it must not drift if the transport is ever swapped.
///
/// Every value is a string. FCM has no way to carry a nested object, so the
/// booking's parameters are flattened alongside the envelope keys rather than
/// nested under one.
/// </summary>
public static class NotificationPayloadBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds the payload for a claimed notification: the envelope keys
    /// (<c>v</c>, <c>type</c>, <c>route</c>, <c>entityType</c>, <c>entityId</c>,
    /// <c>notificationId</c>, <c>sentAtUtc</c>) plus the row's own template
    /// parameters.
    ///
    /// Envelope keys are written LAST so a stray parameter of the same name in
    /// <c>DataJson</c> can never overwrite the routing contract.
    /// </summary>
    /// <summary>
    /// The id block the mobile apps read on every notification to decide what to
    /// open. Always emitted, with an empty string where the field does not apply
    /// — see <see cref="ApplyCanonicalFields"/> for why absence is not used.
    /// </summary>
    private static readonly string[] CanonicalFields =
    [
        NotificationDataKeys.BookingId,
        NotificationDataKeys.EventId,
        NotificationDataKeys.ParentId,
        NotificationDataKeys.ProviderId,
        NotificationDataKeys.PetId,
        NotificationDataKeys.IsNightStay,
        NotificationDataKeys.PayoutId,
        // Added with chat (2026-08-09). A messaging notification is useless
        // without it, and the always-present rule is what lets a tap handler read
        // any id without first checking whether this type carries one.
        NotificationDataKeys.ConversationId
    ];

    public static IReadOnlyDictionary<string, string> Build(
        ClaimedNotification notification,
        RenderedNotification rendered,
        DateTimeOffset sentAtUtc)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal);

        // rendered.Data, NOT a re-parse of DataJson: it carries the parameters the
        // copy was actually rendered from, including any the renderer derived
        // (serviceName). Re-parsing here would have sent the app a payload missing
        // the very service name shown in the body it is displaying — and only for
        // SQL-enqueued notifications, since the C# create path sets it explicitly.
        foreach (var (key, value) in rendered.Data)
        {
            payload[key] = value;
        }

        payload[NotificationDataKeys.Version] = NotificationDataKeys.CurrentVersion;
        payload[NotificationDataKeys.Type] = notification.NotificationType;
        payload[NotificationDataKeys.Route] = rendered.Route;
        payload[NotificationDataKeys.NotificationId] = notification.NotificationId.ToString();
        payload[NotificationDataKeys.SentAtUtc] = sentAtUtc.ToString("O");

        if (!string.IsNullOrWhiteSpace(notification.EntityType))
        {
            payload[NotificationDataKeys.EntityType] = notification.EntityType;
        }

        if (notification.EntityId is { } entityId)
        {
            payload[NotificationDataKeys.EntityId] = entityId.ToString();
        }

        ApplyCanonicalFields(payload, rendered.Category);

        return payload;
    }

    /// <summary>
    /// Guarantees the canonical id block: <c>category</c> from the template, and
    /// the seven id fields present on every payload.
    ///
    /// Missing fields are filled with an EMPTY STRING rather than left out. A
    /// client cannot distinguish "this type has no pet" from "the server forgot to
    /// set the pet", so omission would push that ambiguity onto every tap handler;
    /// an always-present key makes the contract self-describing and lets the app
    /// treat a blank as a definite "not applicable".
    ///
    /// <c>category</c> is written from the template rather than the row so it can
    /// never disagree with the type — a row whose DataJson carried a stale or
    /// hand-written category is corrected here.
    /// </summary>
    private static void ApplyCanonicalFields(Dictionary<string, string> payload, string category)
    {
        payload[NotificationDataKeys.Category] = category;

        // A booking notification always knows which table its id points at, even
        // if the enqueuer only set the older bookingType key.
        if (category == NotificationCategories.Booking
            && !payload.ContainsKey(NotificationDataKeys.IsNightStay)
            && payload.TryGetValue(NotificationDataKeys.BookingType, out var bookingType))
        {
            payload[NotificationDataKeys.IsNightStay] =
                string.Equals(bookingType, "NightStay", StringComparison.OrdinalIgnoreCase)
                    ? "true"
                    : "false";
        }

        foreach (var field in CanonicalFields)
        {
            if (!payload.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
            {
                payload[field] = string.Empty;
            }
        }
    }

    /// <summary>
    /// Reads the outbox row's <c>DataJson</c> into template parameters.
    ///
    /// Tolerant by design: a malformed or non-object payload yields no parameters
    /// rather than throwing. The template renderer substitutes neutral fallbacks
    /// for anything missing, so a bad row still produces a readable notification
    /// instead of blocking the whole batch.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseData(string? dataJson)
    {
        if (string.IsNullOrWhiteSpace(dataJson))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(dataJson, SerializerOptions);
            if (parsed is null)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }

            var data = new Dictionary<string, string>(parsed.Count, StringComparer.Ordinal);
            foreach (var (key, element) in parsed)
            {
                // Numbers and booleans are coerced rather than dropped: a T-SQL
                // enqueue building JSON by hand can easily emit an unquoted value.
                var value = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => element.GetRawText()
                };

                if (!string.IsNullOrWhiteSpace(value))
                {
                    data[key] = value;
                }
            }

            return data;
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Serialises template parameters for the outbox's <c>DataJson</c> column.</summary>
    public static string? SerializeData(IReadOnlyDictionary<string, string>? data) =>
        data is null || data.Count == 0 ? null : JsonSerializer.Serialize(data, SerializerOptions);
}
