using Pawfront.Application.Chat;
using Pawfront.Application.Notifications;

namespace Pawfront.ChatApi.Delivery;

/// <summary>
/// Drains <see cref="ChatPushQueue"/>: render, send, report.
///
/// This is the half of the instant-push design that runs in C#. The row was
/// already written pre-claimed by <c>Chat.CommitMessageAppend</c>, so all that is left
/// is to do what the 1-minute dispatcher would have done — using the SAME
/// renderer, the SAME payload builder and the SAME push sender, which is what
/// stops chat notifications drifting from every other notification in the
/// product.
///
/// Every failure path ends the same way: leave the row un-completed. Its lease
/// lapses and <c>NotificationDispatchFunction</c> takes it over. There is no
/// case in which a message is stored and its notification is lost.
/// </summary>
/// <remarks>
/// A <see cref="BackgroundService"/> is a singleton, so the scoped
/// <see cref="IInstantNotificationSender"/> is resolved from a fresh scope per
/// item rather than injected — injecting it would be a captive dependency, which
/// DI validation rejects at startup. <see cref="IPushSender"/> is genuinely a
/// singleton (it caches one Firebase app per audience) and is injected directly.
/// </remarks>
internal sealed class ChatPushWorker(
    ChatPushQueue queue,
    IPushSender pushSender,
    IServiceScopeFactory scopeFactory,
    ILogger<ChatPushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Chat push worker started.");

        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(stoppingToken))
            {
                // Per-item try/catch: one bad notification must not take the
                // worker down and stall every subsequent message's push.
                try
                {
                    await SendAsync(item, stoppingToken);
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Failed to send notification {NotificationId} instantly; its lease will lapse " +
                        "and the scheduled dispatcher will retry it.",
                        item.NotificationId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ordinary shutdown.
        }

        logger.LogInformation("Chat push worker stopped.");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        return base.StopAsync(cancellationToken);
    }

    private async Task SendAsync(ChatPushWorkItem item, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var instantNotificationSender = scope.ServiceProvider.GetRequiredService<IInstantNotificationSender>();

        var data = NotificationPayloadBuilder.ParseData(item.DataJson);

        // Swiss local, as of the 2026-08-06 change — producers hand over UTC
        // instants and the renderer localises. Chat carries no times today, but
        // going through the same call means it inherits the behaviour if it ever
        // does. PROVISION: pass a per-user zone here once the profile tables carry one.
        var timeZone = NotificationLocalTime.Resolve(recipientTimeZoneId: null);

        var rendered = NotificationRenderer.Render(
            NotificationTypes.MessageReceived, item.Audience, data, timeZone);

        if (rendered is null)
        {
            // A catalog gap rather than a delivery problem. Recorded as failed so
            // it surfaces instead of sending a blank push.
            await instantNotificationSender.CompleteAsync(
                new NotificationDeliveryResult(
                    item.NotificationId,
                    NotificationDeliveryStatuses.Failed,
                    null, null, null, 0,
                    $"No template registered for notification type '{NotificationTypes.MessageReceived}'."),
                cancellationToken);
            return;
        }

        // The payload builder wants a ClaimedNotification. Constructing one here
        // rather than re-reading the row keeps this to zero extra round trips —
        // every field it needs is already known.
        var claimed = new ClaimedNotification(
            item.NotificationId,
            item.Audience,
            item.RecipientId,
            NotificationTypes.MessageReceived,
            NotificationEntityTypes.Conversation,
            item.ConversationId,
            item.DataJson,
            ImageUrl: null,
            AttemptCount: 1,
            CreatedAtUtc: DateTimeOffset.UtcNow);

        var payload = NotificationPayloadBuilder.Build(claimed, rendered, DateTimeOffset.UtcNow);

        var result = await pushSender.SendAsync(
            new PushMessage(
                item.Audience,
                item.Tokens,
                rendered.Title,
                rendered.Body,
                payload,
                ImageUrl: null),
            cancellationToken);

        // Dead tokens are deliberately NOT deactivated here. That is the
        // dispatcher's job and it does it in bulk; doing it from the request path
        // would mean another write per message for a condition that resolves
        // itself on the next scheduled tick.
        var status = result.IsFailure || result.SuccessCount == 0
            ? NotificationDeliveryStatuses.Failed
            : NotificationDeliveryStatuses.Sent;

        await instantNotificationSender.CompleteAsync(
            new NotificationDeliveryResult(
                item.NotificationId,
                status,
                rendered.Title,
                rendered.Body,
                rendered.Route,
                result.SuccessCount,
                result.Error ?? (result.SuccessCount == 0
                    ? "All device tokens for this recipient were rejected as invalid."
                    : null)),
            cancellationToken);
    }
}
