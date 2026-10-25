using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.Notifications;

namespace Pawfront.Infrastructure.Sql.Notifications;

internal sealed class SqlAccountNotificationCleanupStore(
    string? configuredConnectionString, IPawfrontSecretProvider? secretProvider) : IAccountNotificationCleanupStore
{
    public async Task<AccountNotificationCleanupJob?> GetAsync(NotificationAudience audience, Guid accountId, CancellationToken ct)
        => (await ReadAsync("AND Audience=@Audience AND AccountId=@AccountId", audience, accountId, ct)).SingleOrDefault();

    public Task<IReadOnlyList<AccountNotificationCleanupJob>> ListDueAsync(CancellationToken ct)
        => ReadAsync("AND NextAttemptAtUtc<=SYSUTCDATETIME()", null, null, ct);

    public Task CompleteAsync(AccountNotificationCleanupJob job, CancellationToken ct)
        => UpdateAsync(job, "CompletedAtUtc=SYSUTCDATETIME(), FirebaseUserId=NULL", ct);

    public Task FailAsync(AccountNotificationCleanupJob job, CancellationToken ct)
        => UpdateAsync(job, "AttemptCount=AttemptCount+1, NextAttemptAtUtc=DATEADD(MINUTE,5,SYSUTCDATETIME())", ct);

    private async Task<IReadOnlyList<AccountNotificationCleanupJob>> ReadAsync(string filter, NotificationAudience? audience, Guid? accountId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("SELECT TOP (50) Audience,AccountId,FirebaseUserId,DeletedAtUtc FROM Notification.AccountNotificationCleanup WHERE CompletedAtUtc IS NULL AND FirebaseUserId IS NOT NULL " + filter + " ORDER BY NextAttemptAtUtc,AccountId", connection);
        if (audience.HasValue) command.Parameters.Add("@Audience", SqlDbType.NVarChar, 16).Value = audience.Value.ToString();
        if (accountId.HasValue) command.Parameters.AddWithValue("@AccountId", accountId.Value);
        var jobs = new List<AccountNotificationCleanupJob>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) jobs.Add(new(Enum.Parse<NotificationAudience>(reader.GetString(0)), reader.GetGuid(1), reader.GetString(2), new DateTimeOffset(reader.GetDateTime(3), TimeSpan.Zero)));
        return jobs;
    }

    private async Task UpdateAsync(AccountNotificationCleanupJob job, string changes, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("UPDATE Notification.AccountNotificationCleanup SET " + changes + " WHERE Audience=@Audience AND AccountId=@AccountId AND CompletedAtUtc IS NULL", connection);
        command.Parameters.Add("@Audience", SqlDbType.NVarChar, 16).Value = job.Audience.ToString();
        command.Parameters.AddWithValue("@AccountId", job.AccountId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cs = !string.IsNullOrWhiteSpace(configuredConnectionString) ? configuredConnectionString
            : secretProvider is not null ? await secretProvider.GetSqlConnectionStringAsync(ct)
            : throw new InvalidOperationException("SQL Server is not configured.");
        var connection = new SqlConnection(cs);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}
