using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Firebase;

/// <summary>Revalidates provider token ownership after work was queued or claimed.</summary>
public sealed class ProviderOwnedPushSender(IPushSender sender, IProviderPushTokenReader tokenReader) : IPushSender
{
    public async Task<PushSendResult> SendAsync(PushMessage message, CancellationToken cancellationToken)
    {
        if (message.Audience != NotificationAudience.Provider)
            return await sender.SendAsync(message, cancellationToken);

        if (message.Tokens.Count == 0 || string.IsNullOrWhiteSpace(message.FirebaseUserId))
            return new PushSendResult(0, [], null);

        IReadOnlyList<string> current;
        try
        {
            current = await tokenReader.GetCurrentTokensAsync(message.FirebaseUserId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return PushSendResult.Failed("Unable to verify provider device-token ownership.");
        }

        var active = current.ToHashSet(StringComparer.Ordinal);
        var tokens = message.Tokens.Where(active.Contains).Distinct(StringComparer.Ordinal).ToArray();
        return tokens.Length == 0 ? new PushSendResult(0, [], null)
            : await sender.SendAsync(message with { Tokens = tokens }, cancellationToken);
    }
}
