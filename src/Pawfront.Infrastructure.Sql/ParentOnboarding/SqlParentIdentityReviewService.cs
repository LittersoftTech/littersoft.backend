using System.Data;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Contracts.ParentOnboarding;
namespace Pawfront.Infrastructure.Sql.ParentOnboarding;

internal sealed class SqlParentIdentityReviewService(string? configuredConnectionString, IPawfrontSecretProvider? secretProvider) : IParentIdentityReviewService
{
    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cs = configuredConnectionString ?? (secretProvider is null
            ? throw new InvalidOperationException("SQL connection is not configured.")
            : await secretProvider.GetSqlConnectionStringAsync(ct));
        var connection = new SqlConnection(cs);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task<IReadOnlyList<ParentIdentityReviewItem>> ListAsync(string status, CancellationToken cancellationToken)
    {
        if (status is not ("InReview" or "Added" or "NotApproved")) throw new ArgumentException("Invalid identity status.");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT TOP (100) i.ParentIdentityId, i.PetParentId, i.IdentityType, i.IdentityPhotoUrl, i.CreatedAtUtc, i.UpdatedAtUtc, i.Status, i.RejectionReason, p.FirstName, p.LastName FROM Parent.ParentIdentities i JOIN Parent.PetParents p ON p.PetParentId=i.PetParentId WHERE i.Status=@Status ORDER BY i.UpdatedAtUtc, i.ParentIdentityId", connection);
        command.Parameters.AddWithValue("@Status", status);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<ParentIdentityReviewItem>();
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new(new(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
                new(reader.GetDateTime(4), TimeSpan.Zero), new(reader.GetDateTime(5), TimeSpan.Zero),
                reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)), reader.GetString(8), reader.GetString(9)));
        return result;
    }
    public async Task ReviewAsync(Guid identityId, ReviewParentIdentityRequest request, CancellationToken cancellationToken)
    {
        if (request.Decision is not ("Accept" or "Reject")) throw new ArgumentException("Decision must be Accept or Reject.");
        var reason = request.RejectionReason?.Trim();
        if (request.Decision == "Reject" && (string.IsNullOrEmpty(reason) || reason.Length > 2000))
            throw new ArgumentException("Reject requires a reason of 1 to 2000 characters.");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand("Parent.ReviewPetParentIdentity", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@ParentIdentityId", identityId);
        command.Parameters.Add("@ExpectedUpdatedAtUtc", SqlDbType.DateTime2).Value = request.ExpectedUpdatedAtUtc.UtcDateTime;
        command.Parameters.AddWithValue("@Status", request.Decision == "Accept" ? "Added" : "NotApproved");
        command.Parameters.AddWithValue("@RejectionReason", request.Decision == "Reject" ? reason! : DBNull.Value);
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqlException ex) when (ex.Number == 51210) { throw new IdentityReviewConflictException(); }
    }
}
