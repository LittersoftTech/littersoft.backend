using System.Text;

namespace Pawfront.Application.Notifications;

/// <summary>The rendered result: what the OS shows, and where a tap goes.</summary>
/// <param name="Category">
/// One of <see cref="NotificationCategories"/>, taken from the template so
/// <c>data.category</c> can never disagree with <c>data.type</c>.
/// </param>
/// <param name="Data">
/// The parameters the copy was rendered FROM — the outbox row's own values plus
/// anything derived here (today: <c>serviceName</c>). Carried out so the payload
/// builder sends exactly what was rendered: the app must never be told a
/// different service name from the one in the body it is showing.
/// </param>
public sealed record RenderedNotification(
    string Title,
    string Body,
    string Route,
    string Category,
    IReadOnlyDictionary<string, string> Data);

/// <summary>
/// Fills a <see cref="NotificationTemplate"/>'s <c>{placeholder}</c> tokens from
/// the outbox row's data parameters.
///
/// Used only by the dispatcher, which is deliberately the ONLY component that
/// renders copy — see <see cref="NotificationTemplateCatalog"/> for why.
/// </summary>
public static class NotificationRenderer
{
    // Column widths on Notification.NotificationOutbox.
    private const int MaxTitleLength = 200;
    private const int MaxBodyLength = 1000;

    /// <summary>
    /// Neutral stand-ins for parameters that are legitimately absent rather than
    /// forgotten — a Custom walk-in has no pet, and a deleted account has no name.
    /// Without these, "the booking for  on 5 Aug" would reach a real user.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Fallbacks =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [NotificationDataKeys.PetName] = "your pet",
            [NotificationDataKeys.ProviderName] = "Your provider",
            [NotificationDataKeys.ParentName] = "A customer",
            [NotificationDataKeys.EventTitle] = "your event",
            [NotificationDataKeys.ServiceDate] = "the booked date",
            [NotificationDataKeys.StartTime] = "the booked time",
            [NotificationDataKeys.CheckInDate] = "the check-in date",
            [NotificationDataKeys.DropOffTime] = "the drop-off time",
            [NotificationDataKeys.ServiceName] = "a service",
            [NotificationDataKeys.AcceptBy] = "the deadline shown in the app",
            [NotificationDataKeys.AbsentParty] = "the other party",
            [NotificationDataKeys.Amount] = "the agreed amount",
            [NotificationDataKeys.TicketCount] = "some",
            [NotificationDataKeys.NewServiceDate] = "the new date",
            [NotificationDataKeys.NewStartTime] = "the new time",
            [NotificationDataKeys.Location] = "the agreed location",
            [NotificationDataKeys.ClosingTime] = "closing time",
            [NotificationDataKeys.TicketId] = "your ticket",
            [NotificationDataKeys.InvoiceId] = "your invoice",
            [NotificationDataKeys.IssuedBy] = "your provider",
            [NotificationDataKeys.SenderName] = "Someone",
            // Promotional copy is supplied whole by the caller; if it is missing
            // there is nothing to say, so the fallbacks keep the push harmless
            // rather than shipping an empty banner.
            [NotificationDataKeys.Title] = "Pawfront",
            [NotificationDataKeys.Body] = "Open the app to see what's new."
        };

    /// <summary>
    /// Renders the copy for a notification type, or null when the type has no
    /// template. A null return is a bug in the catalog, not a runtime condition —
    /// the dispatcher records it as a failed delivery so the gap is visible
    /// instead of a blank push going out.
    /// </summary>
    /// <param name="audience">
    /// Which app is being written to. Several events are mirrored to both parties
    /// with different wording, so copy is selected per audience — see
    /// <see cref="NotificationTemplateCatalog"/>.
    /// </param>
    public static RenderedNotification? Render(
        string notificationType,
        NotificationAudience audience,
        IReadOnlyDictionary<string, string>? data)
    {
        var template = NotificationTemplateCatalog.Find(notificationType, audience);
        if (template is null)
        {
            return null;
        }

        data = WithDerivedServiceName(data);

        return new RenderedNotification(
            Truncate(Substitute(template.TitleTemplate, data), MaxTitleLength),
            Truncate(Substitute(template.BodyTemplate, data), MaxBodyLength),
            template.Route,
            template.Category,
            data ?? EmptyData);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyData =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Fills in <c>serviceName</c> from <c>serviceType</c> + <c>serviceItemCode</c>
    /// when the enqueuer could not supply it.
    ///
    /// The grooming display names live in a C# catalog, so a T-SQL enqueue (every
    /// booking sproc and every sweep) can only emit the raw identifiers. Deriving
    /// here keeps one naming rule for all producers instead of a second, drifting
    /// copy of the 18 menu-item names in SQL. An explicit <c>serviceName</c> always
    /// wins — the create path already resolves the better label.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? WithDerivedServiceName(
        IReadOnlyDictionary<string, string>? data)
    {
        if (data is null
            || (data.TryGetValue(NotificationDataKeys.ServiceName, out var existing)
                && !string.IsNullOrWhiteSpace(existing)))
        {
            return data;
        }

        data.TryGetValue(NotificationDataKeys.ServiceType, out var serviceType);
        data.TryGetValue(NotificationDataKeys.ServiceItemCode, out var serviceItemCode);

        if (string.IsNullOrWhiteSpace(serviceType) && string.IsNullOrWhiteSpace(serviceItemCode))
        {
            return data;
        }

        return new Dictionary<string, string>(data, StringComparer.Ordinal)
        {
            [NotificationDataKeys.ServiceName] = BookingServiceLabel.Resolve(serviceType, serviceItemCode)
        };
    }

    /// <summary>
    /// Replaces every <c>{key}</c> with its data value, its fallback, or nothing.
    /// An unmatched '{' is emitted verbatim, so copy containing a stray brace can
    /// never throw on a live send.
    /// </summary>
    private static string Substitute(string template, IReadOnlyDictionary<string, string>? data)
    {
        if (template.IndexOf('{') < 0)
        {
            return template;
        }

        var builder = new StringBuilder(template.Length + 32);
        var index = 0;

        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            builder.Append(template, index, open - index);

            var key = template[(open + 1)..close];
            builder.Append(Resolve(key, data));

            index = close + 1;
        }

        // A dropped placeholder with no fallback leaves a double space behind.
        return CollapseSpaces(builder.ToString());
    }

    private static string Resolve(string key, IReadOnlyDictionary<string, string>? data)
    {
        if (data is not null
            && data.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return Fallbacks.TryGetValue(key, out var fallback) ? fallback : string.Empty;
    }

    private static string CollapseSpaces(string value)
    {
        if (!value.Contains("  ", StringComparison.Ordinal))
        {
            return value.Trim();
        }

        var builder = new StringBuilder(value.Length);
        var previousWasSpace = false;

        foreach (var character in value)
        {
            var isSpace = character == ' ';
            if (isSpace && previousWasSpace)
            {
                continue;
            }

            builder.Append(character);
            previousWasSpace = isSpace;
        }

        return builder.ToString().Trim();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
}
