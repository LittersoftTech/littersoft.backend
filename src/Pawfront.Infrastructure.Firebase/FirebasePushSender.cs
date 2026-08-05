using FirebaseAdmin.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Firebase;

/// <summary>
/// Sends an assembled push through FCM HTTP v1.
///
/// Messages are hybrid <c>notification</c> + <c>data</c>: the notification block
/// makes the OS display it even when the app is killed (a data-only message would
/// need the app to be running, and iOS throttles silent delivery), while the data
/// block carries the routing contract the app reads on tap.
/// </summary>
public sealed class FirebasePushSender(
    FirebaseAppRegistry registry,
    IOptions<NotificationOptions> options,
    ILogger<FirebasePushSender> logger) : IPushSender
{
    /// <summary>FCM's hard ceiling for one multicast message.</summary>
    private const int MaxTokensPerRequest = 500;

    private readonly AndroidNotificationOptions _android = options.Value.Android;

    public async Task<PushSendResult> SendAsync(PushMessage message, CancellationToken cancellationToken)
    {
        if (message.Tokens.Count == 0)
        {
            return new PushSendResult(0, [], null);
        }

        if (!registry.IsConfigured(message.Audience))
        {
            return PushSendResult.Failed(
                $"No Firebase credentials are configured for the {message.Audience} app.");
        }

        FirebaseMessaging messaging;
        try
        {
            messaging = await registry.GetMessagingAsync(message.Audience, cancellationToken);
        }
        catch (Exception exception)
        {
            // Bad or missing credentials — every send to this audience will fail
            // the same way, so surface it as a retryable batch failure rather
            // than blaming the tokens.
            logger.LogError(exception, "Could not initialise Firebase for the {Audience} app.", message.Audience);
            return PushSendResult.Failed($"Firebase initialisation failed: {exception.Message}");
        }

        var successCount = 0;
        var invalidTokens = new List<string>();

        foreach (var chunk in Chunk(message.Tokens, MaxTokensPerRequest))
        {
            var multicast = BuildMessage(message, chunk);

            try
            {
                // SendEachForMulticastAsync sends one request per token in
                // parallel. The old batch endpoint (SendMulticastAsync) was
                // retired by Google in 2024.
                var response = await messaging.SendEachForMulticastAsync(multicast, cancellationToken);

                successCount += response.SuccessCount;
                CollectInvalidTokens(response, chunk, invalidTokens);
            }
            catch (FirebaseMessagingException exception)
            {
                logger.LogError(
                    exception,
                    "FCM rejected a multicast of {TokenCount} token(s) for the {Audience} app ({ErrorCode}).",
                    chunk.Count, message.Audience, exception.MessagingErrorCode);

                return PushSendResult.Failed($"FCM error {exception.MessagingErrorCode}: {exception.Message}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Unexpected failure sending to the {Audience} app.", message.Audience);
                return PushSendResult.Failed(exception.Message);
            }
        }

        return new PushSendResult(successCount, invalidTokens, null);
    }

    private MulticastMessage BuildMessage(PushMessage message, IReadOnlyList<string> tokens) =>
        new()
        {
            Tokens = [.. tokens],
            Notification = new Notification
            {
                Title = message.Title,
                Body = message.Body,
                ImageUrl = string.IsNullOrWhiteSpace(message.ImageUrl) ? null : message.ImageUrl
            },
            // Every value is already a string — FCM rejects any other type.
            Data = new Dictionary<string, string>(message.Data),
            Android = new AndroidConfig
            {
                // These are user-initiated, time-relevant events (a booking was
                // accepted, a job is starting), so they must survive Doze.
                Priority = Priority.High,
                Notification = new AndroidNotification
                {
                    // Android 8+ DROPS a notification naming a channel the app
                    // hasn't created — this must match the app's channel exactly.
                    ChannelId = _android.ChannelId,
                    Icon = _android.Icon,
                    Color = string.IsNullOrWhiteSpace(_android.Color) ? null : _android.Color
                }
            },
            Apns = new ApnsConfig
            {
                Aps = new Aps
                {
                    Sound = "default",
                    // Lets the app update its badge/state on arrival; the alert
                    // itself is carried by the notification block above.
                    ContentAvailable = true
                }
            }
        };

    /// <summary>
    /// Separates permanently-dead tokens from transient failures.
    ///
    /// Only UNREGISTERED (app uninstalled / token rotated), INVALID_ARGUMENT
    /// (malformed token) and SENDER_ID_MISMATCH (token belongs to the other
    /// Firebase project) mean the token will never work again. UNAVAILABLE,
    /// INTERNAL and QUOTA_EXCEEDED are Firebase having a bad minute — deactivating
    /// on those would permanently silence a live device.
    /// </summary>
    private void CollectInvalidTokens(
        BatchResponse response,
        IReadOnlyList<string> tokens,
        List<string> invalidTokens)
    {
        if (response.FailureCount == 0)
        {
            return;
        }

        for (var index = 0; index < response.Responses.Count && index < tokens.Count; index++)
        {
            var sendResponse = response.Responses[index];
            if (sendResponse.IsSuccess)
            {
                continue;
            }

            var errorCode = sendResponse.Exception?.MessagingErrorCode;
            if (errorCode is MessagingErrorCode.Unregistered
                or MessagingErrorCode.InvalidArgument
                or MessagingErrorCode.SenderIdMismatch)
            {
                invalidTokens.Add(tokens[index]);
            }
            else
            {
                logger.LogWarning(
                    "Transient FCM failure for one device ({ErrorCode}); the token is kept active.",
                    errorCode?.ToString() ?? "unknown");
            }
        }
    }

    private static IEnumerable<IReadOnlyList<string>> Chunk(IReadOnlyList<string> tokens, int size)
    {
        if (tokens.Count <= size)
        {
            yield return tokens;
            yield break;
        }

        for (var offset = 0; offset < tokens.Count; offset += size)
        {
            yield return tokens.Skip(offset).Take(size).ToList();
        }
    }
}
