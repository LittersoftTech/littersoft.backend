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
            [NotificationDataKeys.ServiceDateWithYear] = "the booked date",
            [NotificationDataKeys.StartTime] = "the booked time",
            [NotificationDataKeys.CheckInDate] = "the check-in date",
            [NotificationDataKeys.DropOffTime] = "the drop-off time",
            [NotificationDataKeys.ServiceName] = "a service",
            [NotificationDataKeys.AcceptBy] = "the deadline shown in the app",
            [NotificationDataKeys.RespondWithin] = "the time shown in the app",
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
    /// <param name="timeZone">
    /// The zone every date and time in the copy is expressed in — the recipient's
    /// own once profiles carry one, Switzerland for everybody today. Null falls
    /// back to <see cref="NotificationLocalTime.Default"/>.
    /// </param>
    public static RenderedNotification? Render(
        string notificationType,
        NotificationAudience audience,
        IReadOnlyDictionary<string, string>? data,
        TimeZoneInfo? timeZone = null)
    {
        var template = NotificationTemplateCatalog.Find(notificationType, audience);
        if (template is null)
        {
            return null;
        }

        data = WithLocalTimes(data, timeZone ?? NotificationLocalTime.Default);
        data = WithDerivedServiceName(data);
        data = WithDerivedResponseWindow(data);

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
    /// Converts the UTC instants a producer emitted into the display strings the
    /// copy uses, in the recipient's timezone.
    ///
    /// This is THE conversion — every date and time a user reads in a notification
    /// passes through here, whether the notification was enqueued by an API host
    /// or by a T-SQL sweep. See <see cref="NotificationLocalTime"/> for why
    /// producers hand over instants instead of formatting them themselves.
    ///
    /// Derived values overwrite any same-named display key already present:
    /// after the 2026-08-06 deploy no producer emits both, and a row enqueued
    /// BEFORE it carries only the old UTC-formatted display strings and no
    /// instant, so it renders exactly as it used to. That is what makes the change
    /// safe for whatever is already sitting in the outbox.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? WithLocalTimes(
        IReadOnlyDictionary<string, string>? data,
        TimeZoneInfo timeZone)
    {
        if (data is null || data.Count == 0)
        {
            return data;
        }

        Dictionary<string, string>? localised = null;

        void Set(string key, string value) =>
            (localised ??= new Dictionary<string, string>(data, StringComparer.Ordinal))[key] = value;

        if (TryReadInstant(data, NotificationDataKeys.ServiceStartUtc, out var serviceStart))
        {
            Set(NotificationDataKeys.ServiceDate, NotificationLocalTime.FormatDate(serviceStart, timeZone));
            Set(
                NotificationDataKeys.ServiceDateWithYear,
                NotificationLocalTime.FormatDateWithYear(serviceStart, timeZone));
            Set(NotificationDataKeys.StartTime, NotificationLocalTime.FormatTime(serviceStart, timeZone));

            // A stay's service starts at drop-off on the check-in day, so the two
            // pairs of keys describe the same instant. Both are emitted because the
            // night-stay-specific copy reads {checkInDate}/{dropOffTime} while every
            // shared booking template reads {serviceDate}/{startTime}.
            if (IsNightStay(data))
            {
                Set(NotificationDataKeys.CheckInDate, NotificationLocalTime.FormatDate(serviceStart, timeZone));
                Set(NotificationDataKeys.DropOffTime, NotificationLocalTime.FormatTime(serviceStart, timeZone));
            }
        }

        if (TryReadInstant(data, NotificationDataKeys.CheckOutUtc, out var checkOut))
        {
            Set(NotificationDataKeys.CheckOutDate, NotificationLocalTime.FormatDate(checkOut, timeZone));
        }

        if (TryReadInstant(data, NotificationDataKeys.AcceptByUtc, out var acceptBy))
        {
            Set(NotificationDataKeys.AcceptBy, NotificationLocalTime.FormatDateAndTime(acceptBy, timeZone));
        }

        if (TryReadInstant(data, NotificationDataKeys.NewServiceStartUtc, out var newServiceStart))
        {
            Set(NotificationDataKeys.NewServiceDate, NotificationLocalTime.FormatDate(newServiceStart, timeZone));
            Set(NotificationDataKeys.NewStartTime, NotificationLocalTime.FormatTime(newServiceStart, timeZone));
        }

        if (TryReadInstant(data, NotificationDataKeys.NewCheckOutUtc, out var newCheckOut))
        {
            Set(NotificationDataKeys.NewCheckOutDate, NotificationLocalTime.FormatDate(newCheckOut, timeZone));

            // A stay is proposed as a date RANGE, so its copy puts the new
            // check-out where a single-day booking puts a clock time: "the new
            // timing is confirmed: 10 Aug at 15 Aug". Deliberate — one shared
            // modification template serves both booking kinds — so the override
            // must come after the {newServiceStartUtc} block above.
            if (IsNightStay(data))
            {
                Set(NotificationDataKeys.NewStartTime, NotificationLocalTime.FormatDate(newCheckOut, timeZone));
            }
        }

        if (TryReadInstant(data, NotificationDataKeys.ClosingAtUtc, out var closingAt))
        {
            Set(NotificationDataKeys.ClosingTime, NotificationLocalTime.FormatTime(closingAt, timeZone));
        }

        return localised ?? data;
    }

    private static bool TryReadInstant(
        IReadOnlyDictionary<string, string> data,
        string key,
        out DateTimeOffset instantUtc)
    {
        instantUtc = default;
        return data.TryGetValue(key, out var value) && NotificationLocalTime.TryParse(value, out instantUtc);
    }

    private static bool IsNightStay(IReadOnlyDictionary<string, string> data) =>
        (data.TryGetValue(NotificationDataKeys.IsNightStay, out var flag)
            && string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
        || (data.TryGetValue(NotificationDataKeys.BookingType, out var bookingType)
            && string.Equals(bookingType, "NightStay", StringComparison.OrdinalIgnoreCase));

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
    /// Derives <c>respondWithin</c> — how long the provider has to answer a
    /// booking request — from the deadline and the moment the booking was made.
    ///
    /// <b>Why the window is measured from CREATION, not from now.</b> The two
    /// rules that expire an unaccepted booking both run off the creation instant
    /// (BR-17) or the service start (BR-53), and quoting the window they actually
    /// grant is what makes the common case read as a clean "24 hours". Measuring
    /// against send time would render that same window as "23 hours 59 minutes",
    /// which is technically closer and reads like a mistake. The push leaves
    /// within about a minute of the booking, so the difference is not one a
    /// provider can act on — and <c>acceptByUtc</c> travels alongside for an app
    /// that wants an exact live countdown.
    ///
    /// Absent when either instant is missing (a row enqueued before this shipped)
    /// or the span has already run out; the renderer's fallback covers it.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? WithDerivedResponseWindow(
        IReadOnlyDictionary<string, string>? data)
    {
        if (data is null
            || !TryReadInstant(data, NotificationDataKeys.AcceptByUtc, out var acceptBy)
            || !TryReadInstant(data, NotificationDataKeys.CreatedAtUtc, out var createdAt))
        {
            return data;
        }

        var window = NotificationDuration.Humanise(acceptBy - createdAt);
        if (window is null)
        {
            return data;
        }

        return new Dictionary<string, string>(data, StringComparer.Ordinal)
        {
            [NotificationDataKeys.RespondWithin] = window
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
