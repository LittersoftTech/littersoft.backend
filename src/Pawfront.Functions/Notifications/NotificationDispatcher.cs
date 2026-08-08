using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pawfront.Application.Notifications;
using Pawfront.Infrastructure.Firebase;

namespace Pawfront.Functions.Notifications;

/// <summary>What one dispatch tick did, for the function's log line.</summary>
public sealed record NotificationDispatchResult(
    int Claimed,
    int Sent,
    int NoDevice,
    int Failed,
    int DeactivatedTokens)
{
    public static readonly NotificationDispatchResult Empty = new(0, 0, 0, 0, 0);

    public bool DidWork => Claimed > 0;
}

/// <summary>
/// Drains a batch of the notification outbox: claim, render, send, record.
///
/// This is the only place copy is rendered and the only place FCM is called,
/// which is what lets a notification be enqueued identically from the two API
/// hosts and from pure T-SQL in the booking sweeps.
/// </summary>
public sealed class NotificationDispatcher(
    INotificationOutboxStore outboxStore,
    IPushSender pushSender,
    IOptions<NotificationOptions> options,
    ILogger<NotificationDispatcher> logger)
{
    private readonly NotificationDispatchOptions _dispatch = options.Value.Dispatch;

    public async Task<NotificationDispatchResult> RunAsync(CancellationToken cancellationToken)
    {
        var batch = await outboxStore.ClaimAsync(
            _dispatch.BatchSize, _dispatch.MaxAttempts, _dispatch.LeaseMinutes, cancellationToken);

        if (batch.IsEmpty)
        {
            return NotificationDispatchResult.Empty;
        }

        // One lookup per (audience, recipient) rather than a scan per notification —
        // a batch commonly contains several notifications for the same person.
        var tokensByRecipient = batch.DeviceTokens
            .GroupBy(token => (token.Audience, token.RecipientId))
            .ToDictionary(group => group.Key, group => group.Select(t => t.FcmToken).ToList());

        var results = new List<NotificationDeliveryResult>(batch.Notifications.Count);
        var deadTokens = new List<DeadDeviceToken>();
        int sent = 0, noDevice = 0, failed = 0;

        foreach (var notification in batch.Notifications)
        {
            var result = await DispatchOneAsync(notification, tokensByRecipient, deadTokens, cancellationToken);
            results.Add(result);

            switch (result.Status)
            {
                case NotificationDeliveryStatuses.Sent: sent++; break;
                case NotificationDeliveryStatuses.NoDevice: noDevice++; break;
                default: failed++; break;
            }
        }

        // Record outcomes BEFORE pruning tokens: the outbox rows are the work
        // record, and re-sending on the next tick because we lost them is far
        // worse than leaving a dead token active for another five minutes.
        await outboxStore.CompleteAsync(results, _dispatch.MaxAttempts, cancellationToken);

        if (deadTokens.Count > 0)
        {
            await outboxStore.DeactivateTokensAsync(deadTokens, cancellationToken);
        }

        return new NotificationDispatchResult(
            batch.Notifications.Count, sent, noDevice, failed, deadTokens.Count);
    }

    private async Task<NotificationDeliveryResult> DispatchOneAsync(
        ClaimedNotification notification,
        IReadOnlyDictionary<(NotificationAudience, Guid), List<string>> tokensByRecipient,
        List<DeadDeviceToken> deadTokens,
        CancellationToken cancellationToken)
    {
        var data = NotificationPayloadBuilder.ParseData(notification.DataJson);

        // Every date and time in the copy is rendered in this zone. PROVISION:
        // when Provider.Providers / Parent.PetParents gain a TimeZoneId column,
        // read it onto ClaimedNotification and pass it here — nothing else has to
        // change. Until then every recipient is Swiss, which is what a null id
        // resolves to.
        var timeZone = NotificationLocalTime.Resolve(recipientTimeZoneId: null);

        var rendered = NotificationRenderer.Render(
            notification.NotificationType, notification.Audience, data, timeZone);

        if (rendered is null)
        {
            // The type has no template — a catalog gap, not a delivery problem.
            // Recorded as failed so it surfaces instead of sending a blank push.
            logger.LogError(
                "Notification {NotificationId} has type '{NotificationType}', which has no entry in " +
                "NotificationTemplateCatalog. Nothing was sent.",
                notification.NotificationId, notification.NotificationType);

            return new NotificationDeliveryResult(
                notification.NotificationId,
                NotificationDeliveryStatuses.Failed,
                null, null, null, 0,
                $"No template registered for notification type '{notification.NotificationType}'.");
        }

        if (!tokensByRecipient.TryGetValue((notification.Audience, notification.RecipientId), out var tokens)
            || tokens.Count == 0)
        {
            // Not a failure: the notification is real and belongs in the in-app
            // inbox, the recipient just has no active device right now.
            return new NotificationDeliveryResult(
                notification.NotificationId,
                NotificationDeliveryStatuses.NoDevice,
                rendered.Title, rendered.Body, rendered.Route, 0, null);
        }

        var payload = NotificationPayloadBuilder.Build(notification, rendered, DateTimeOffset.UtcNow);

        var sendResult = await pushSender.SendAsync(
            new PushMessage(
                notification.Audience,
                tokens,
                rendered.Title,
                rendered.Body,
                payload,
                notification.ImageUrl),
            cancellationToken);

        foreach (var invalidToken in sendResult.InvalidTokens)
        {
            deadTokens.Add(new DeadDeviceToken(notification.Audience, invalidToken));
        }

        if (sendResult.IsFailure)
        {
            return new NotificationDeliveryResult(
                notification.NotificationId,
                NotificationDeliveryStatuses.Failed,
                rendered.Title, rendered.Body, rendered.Route, 0, sendResult.Error);
        }

        // Every token was individually rejected as invalid — the recipient
        // effectively has no reachable device, and retrying the same dead tokens
        // would just burn attempts. Their tokens are being deactivated above.
        if (sendResult.SuccessCount == 0)
        {
            return new NotificationDeliveryResult(
                notification.NotificationId,
                NotificationDeliveryStatuses.NoDevice,
                rendered.Title, rendered.Body, rendered.Route, 0,
                "All device tokens for this recipient were rejected as invalid.");
        }

        return new NotificationDeliveryResult(
            notification.NotificationId,
            NotificationDeliveryStatuses.Sent,
            rendered.Title, rendered.Body, rendered.Route, sendResult.SuccessCount, null);
    }
}
