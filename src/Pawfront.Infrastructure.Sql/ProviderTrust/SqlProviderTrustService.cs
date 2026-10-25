using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Pawfront.Application.Configuration;
using Pawfront.Application.ProviderTrust;
namespace Pawfront.Infrastructure.Sql.ProviderTrust;

internal sealed class SqlProviderTrustService(string? configuredConnectionString, IPawfrontSecretProvider? secrets) : IProviderTrustService
{
    public async Task SyncOfferingDocumentAsync(Guid providerId, string? documentUrl, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("Provider.ChangeProviderTrust", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddWithValue("@ProviderId", providerId);
        command.Parameters.AddWithValue("@Action", "SyncOffering");
        command.Parameters.AddWithValue("@DocumentUrl", string.IsNullOrWhiteSpace(documentUrl) ? DBNull.Value : documentUrl.Trim());
        await command.ExecuteNonQueryAsync(ct);
    }
    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var cs = configuredConnectionString ?? (secrets is null ? throw new InvalidOperationException("SQL is not configured.") : await secrets.GetSqlConnectionStringAsync(ct));
        var connection = new SqlConnection(cs);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task<IReadOnlyDictionary<Guid,string>> GetBadgesAsync(IReadOnlyCollection<Guid> providerIds, CancellationToken ct)
    {
        var result = new Dictionary<Guid,string>();
        if (providerIds.Count==0) return result;
        await using var connection = await OpenAsync(ct);
        await using var command = new SqlCommand("""
            SELECT t.ProviderId, CASE WHEN t.EvidenceKind=N'MedReg' AND r.ServiceCategory=N'Vet' THEN N'MedRegListed'
                WHEN t.EvidenceKind=N'Document' AND r.ServiceCategory IN (N'PetSitter',N'PetGroomer',N'PetTrainer') THEN N'DocumentsAdded' END
            FROM Provider.ProviderTrust t JOIN Provider.ProviderServiceRegistrations r ON r.ProviderId=t.ProviderId AND r.ServiceCategory=t.ServiceCategory
            JOIN Provider.Providers p ON p.ProviderId=t.ProviderId AND p.IsDeleted=0
            JOIN OPENJSON(@Ids) WITH(Id UNIQUEIDENTIFIER '$') ids ON ids.Id=t.ProviderId
            WHERE t.Status=N'Added';
            """,connection);
        command.Parameters.AddWithValue("@Ids",JsonSerializer.Serialize(providerIds));
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct)) if(!reader.IsDBNull(1)) result[reader.GetGuid(0)]=reader.GetString(1);
        return result;
    }
    private const string Columns="ProviderId,RevisionId,ServiceCategory,EvidenceKind,Status,DocumentUrl,RejectionReason,MedRegReference,UpdatedAtUtc";
    private static ProviderTrustRecord Read(SqlDataReader r)=>new(r.GetGuid(0),r.GetGuid(1),r.GetString(2),r.GetString(3),r.GetString(4),
        r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:r.GetString(6),r.IsDBNull(7)?null:r.GetString(7),new(r.GetDateTime(8),TimeSpan.Zero));
    public async Task<ProviderTrustRecord?> GetAsync(Guid providerId,CancellationToken ct)
    {
        await using var connection=await OpenAsync(ct);
        await using var command=new SqlCommand($"SELECT {Columns} FROM Provider.ProviderTrust WHERE ProviderId=@Id",connection);
        command.Parameters.AddWithValue("@Id",providerId);
        await using var reader=await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)?Read(reader):null;
    }
    public async Task<IReadOnlyList<ProviderTrustRecord>> ListPendingAsync(CancellationToken ct)
    {
        await using var connection=await OpenAsync(ct);
        await using var command=new SqlCommand($"SELECT TOP (100) {Columns} FROM Provider.ProviderTrust WHERE Status=N'InReview' ORDER BY UpdatedAtUtc,ProviderId",connection);
        await using var reader=await command.ExecuteReaderAsync(ct);
        var result=new List<ProviderTrustRecord>();
        while(await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }
    public async Task<ProviderTrustRecord> ChangeAsync(Guid providerId,string action,string? documentUrl,Guid? revisionId,string? reason,string? reference,CancellationToken ct)
    {
        reason=reason?.Trim(); reference=reference?.Trim();
        if(action is not ("Upload" or "Accept" or "Reject" or "MedRegListed" or "MedRegNotListed")) throw new ProviderTrustInvalidException("Invalid trust action.");
        if(documentUrl?.Length>1000 || reason?.Length>2000 || reference?.Length>1000) throw new ProviderTrustInvalidException("Trust evidence exceeds the field length limit.");
        if(action=="Reject" && string.IsNullOrEmpty(reason)) throw new ProviderTrustInvalidException("Rejection requires a reason.");
        if(action.StartsWith("MedReg",StringComparison.Ordinal) && string.IsNullOrEmpty(reference)) throw new ProviderTrustInvalidException("MedReg lookup reference is required.");
        await using var connection=await OpenAsync(ct);
        await using var command=new SqlCommand("Provider.ChangeProviderTrust",connection){CommandType=CommandType.StoredProcedure};
        command.Parameters.AddWithValue("@ProviderId",providerId); command.Parameters.AddWithValue("@Action",action);
        command.Parameters.AddWithValue("@DocumentUrl",(object?)documentUrl??DBNull.Value);command.Parameters.AddWithValue("@RevisionId",(object?)revisionId??DBNull.Value);
        command.Parameters.AddWithValue("@Reason",(object?)reason??DBNull.Value);command.Parameters.AddWithValue("@Reference",(object?)reference??DBNull.Value);
        try { await using var reader=await command.ExecuteReaderAsync(ct); await reader.ReadAsync(ct); return Read(reader); }
        catch(SqlException ex) when(ex.Number is 51520 or 51522){throw new ProviderTrustConflictException(ex.Message);}
        catch(SqlException ex) when(ex.Number==51521){throw new ProviderTrustInvalidException(ex.Message);}
    }
}
