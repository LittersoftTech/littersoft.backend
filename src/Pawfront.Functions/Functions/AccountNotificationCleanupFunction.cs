using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Pawfront.Application.Notifications;

namespace Pawfront.Functions.Functions;

public sealed class AccountNotificationCleanupFunction(
    IAccountNotificationCleanup cleanup, ILogger<AccountNotificationCleanupFunction> logger)
{
    [Function("AccountNotificationCleanup")]
    public async Task RunAsync([TimerTrigger("0 */5 * * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        try { await cleanup.RetryPendingAsync(cancellationToken); }
        catch (Exception exception) { logger.LogError(exception, "Account inbox cleanup will retry on the next tick."); }
    }
}
