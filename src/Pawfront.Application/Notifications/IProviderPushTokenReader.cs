namespace Pawfront.Application.Notifications;

public interface IProviderPushTokenReader
{
    Task<IReadOnlyList<string>> GetCurrentTokensAsync(string firebaseUserId, CancellationToken cancellationToken);
}
