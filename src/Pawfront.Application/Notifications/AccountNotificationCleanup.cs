using Microsoft.Extensions.Logging;

namespace Pawfront.Application.Notifications;

public sealed record AccountNotificationCleanupJob(
    NotificationAudience Audience, Guid AccountId, string FirebaseUserId, DateTimeOffset DeletedAtUtc);

public interface IAccountNotificationCleanupStore
{
    Task<AccountNotificationCleanupJob?> GetAsync(NotificationAudience audience, Guid accountId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AccountNotificationCleanupJob>> ListDueAsync(CancellationToken cancellationToken);
    Task CompleteAsync(AccountNotificationCleanupJob job, CancellationToken cancellationToken);
    Task FailAsync(AccountNotificationCleanupJob job, CancellationToken cancellationToken);
}

public interface INotificationInboxDeleter
{
    Task DeleteAsync(AccountNotificationCleanupJob job, CancellationToken cancellationToken);
}

public interface IAccountNotificationCleanup
{
    Task TryCleanupAsync(NotificationAudience audience, Guid accountId, CancellationToken cancellationToken);
    Task RetryPendingAsync(CancellationToken cancellationToken);
}

/// <summary>SQL deletion commits first; remote inbox failures remain queued for retry.</summary>
public sealed class AccountNotificationCleanup(
    IAccountNotificationCleanupStore store, INotificationInboxDeleter deleter,
    ILogger<AccountNotificationCleanup> logger) : IAccountNotificationCleanup
{
    public async Task TryCleanupAsync(NotificationAudience audience, Guid accountId, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var job = await store.GetAsync(audience, accountId, timeout.Token);
            if (job is not null) await ProcessAsync(job, timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Account {Audience} {AccountId} was deleted; inbox cleanup remains queued.", audience, accountId);
        }
    }

    public async Task RetryPendingAsync(CancellationToken cancellationToken)
    {
        foreach (var job in await store.ListDueAsync(cancellationToken))
            await ProcessAsync(job, cancellationToken);
    }

    private async Task ProcessAsync(AccountNotificationCleanupJob job, CancellationToken cancellationToken)
    {
        try
        {
            await deleter.DeleteAsync(job, cancellationToken);
            await store.CompleteAsync(job, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Inbox cleanup for deleted {Audience} {AccountId} will be retried.", job.Audience, job.AccountId);
            await store.FailAsync(job, cancellationToken);
        }
    }
}
