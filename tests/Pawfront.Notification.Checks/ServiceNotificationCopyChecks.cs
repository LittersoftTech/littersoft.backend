using System.Text.Json;
using Pawfront.Application.Bookings;
using Pawfront.Application.Notifications;

internal static class ServiceNotificationCopyChecks
{
    // Snapshot transcribed from the supplied comparison, independent of the
    // production catalog. Includes all Services, Events and Identification cards.
    public static int Run()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "notifications-comparison.json")));
        var checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception(name);
            checks++;
        }
        foreach (var card in fixture.RootElement.EnumerateArray())
        {
            var data = new Dictionary<string, string>
            {
                ["providerName"] = "Alex", ["parentName"] = "Sam", ["petName"] = "Milo",
                ["serviceName"] = "Grooming", ["serviceDate"] = "5 Nov", ["amount"] = "40.00",
                ["hostName"] = "Jordan", ["eventTitle"] = "Puppy Picnic", ["seats"] = "10",
                ["attendeeCount"] = "3", ["attendees"] = "3 attendees", ["attendeeVerb"] = "are",
                ["totalAmount"] = "120.00"
            };
            foreach (var parameter in card.GetProperty("parameters").EnumerateObject())
                data[parameter.Name] = parameter.Value.GetString()!;
            var type = card.GetProperty("type").GetString()!;
            var code = card.GetProperty("code").GetString()!;
            var audience = Enum.Parse<NotificationAudience>(card.GetProperty("audience").GetString()!);
            var template = NotificationTemplateCatalog.Find(type, audience, data)!;
            var title = card.GetProperty("title").GetString()!;
            var body = card.GetProperty("body").GetString()!;
            Check(template is not null && template.TitleTemplate == title && template.BodyTemplate == body,
                $"Approved exact title and body: {code}");
            string Expected(string text)
            {
                foreach (var (key, value) in data) text = text.Replace("{" + key + "}", value);
                return text;
            }
            var rendered = NotificationRenderer.Render(type, audience, data)!;
            Check(rendered.Title == Expected(title) && rendered.Body == Expected(body), $"Rendered copy: {code}");
            Check(rendered.Data[NotificationDataKeys.NotificationCode] == code, $"Scenario/audience card identity: {code}");
        }
        foreach (var amount in new[] { "40.00", "CHF 40.00", "chf 40.00" })
        foreach (var type in new[] { NotificationTypes.BookingCompleted, NotificationTypes.BookingPaid })
        {
            var data = new Dictionary<string, string>
            {
                ["amount"] = amount, ["completionScenario"] = "HandOver", ["providerName"] = "Alex"
            };
            var rendered = NotificationRenderer.Render(type, NotificationAudience.PetParent, data)!;
            Check(rendered.Body.Contains("CHF 40.00") && !rendered.Body.Contains("CHF CHF"), "One currency prefix");
            Check(data["amount"] == amount, "Renderer does not mutate producer data");
        }
        foreach (var audience in new[] { NotificationAudience.PetParent, NotificationAudience.Provider })
        {
            var chat = NotificationRenderer.Render(NotificationTypes.MessageReceived, audience,
                new Dictionary<string, string> { ["senderName"] = "Actual Sender" })!;
            Check(chat.Title == "Actual Sender" && chat.Body == "Sent you a message.", "Chat sender fallback");
            var invoice = NotificationTemplateCatalog.Find(NotificationTypes.InvoiceIssued, audience)!;
            Check(invoice.Route == NotificationRoutes.Invoice, "Invoice card opens invoice");
            foreach (var legacy in new[] { NotificationTypes.BookingModificationExpired, NotificationTypes.BookingModificationTimedOut })
                Check(NotificationTemplateCatalog.Find(legacy, audience)!.Code ==
                    (audience == NotificationAudience.Provider ? "V-S3" : "P-S2"), "Expiry causes share one card");
        }
        Check(NotificationTemplateCatalog.Find(NotificationTypes.BookingRequested, NotificationAudience.Provider)!.Code ==
            NotificationTemplateCatalog.Find(NotificationTypes.NightStayBookingRequested, NotificationAudience.Provider)!.Code,
            "Single-day and night-stay requests share V-S1");
        var reverse = NotificationRenderer.Render(NotificationTypes.BookingNoShowReported, NotificationAudience.Provider,
            new Dictionary<string, string> { ["absentParty"] = "the provider" })!;
        Check(reverse.Body.Contains("the provider didn't attend"), "Reverse manual no-show stays compatible");
        Check(BookingNoShowReasons.Normalize(" otpnotshared ", BookingStatuses.ParentNoShow, BookingStatusActor.Provider)
            == "OtpNotShared", "Explicit no-show reason is normalized");
        Check(BookingNoShowReasons.Normalize(null, BookingStatuses.ParentNoShow, BookingStatusActor.Provider) is null,
            "Old no-show requests retain their state fallback");
        foreach (var (reason, status, actor) in new[]
        {
            ("Other", BookingStatuses.ParentNoShow, BookingStatusActor.Provider),
            ("NotArrived", BookingStatuses.Completed, BookingStatusActor.Provider),
            ("NotArrived", BookingStatuses.ParentNoShow, BookingStatusActor.Parent)
        })
        {
            try { BookingNoShowReasons.Normalize(reason, status, actor); throw new Exception("Invalid reason accepted"); }
            catch (ArgumentException) { checks++; }
        }
        return checks;
    }
}
