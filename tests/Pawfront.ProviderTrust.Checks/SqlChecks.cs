using Microsoft.Data.SqlClient;
using Pawfront.Application.ProviderTrust;

static class SqlChecks
{
    public static async Task<int> RunAsync()
    {
        const string cs=@"Server=(localdb)\MSSQLLocalDB;Database=PawfrontIdentityChecks;Integrated Security=true;Encrypt=false";
        var assembly=typeof(Pawfront.Infrastructure.Sql.SqlServiceRegistration).Assembly;
        var service=(IProviderTrustService)Activator.CreateInstance(assembly.GetType("Pawfront.Infrastructure.Sql.ProviderTrust.SqlProviderTrustService")!,cs,null)!;
        var ids=new List<Guid>();var checks=0;
        void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
        await using var connection=new SqlConnection(cs);await connection.OpenAsync();
        async Task Execute(string sql,Guid id,string category="PetSitter")
        {
            await using var command=new SqlCommand(sql,connection);
            command.Parameters.AddWithValue("@Id",id);command.Parameters.AddWithValue("@Category",category);
            await command.ExecuteNonQueryAsync();
        }
        try
        {
            foreach(var category in new[]{"PetSitter","PetGroomer","PetTrainer","Vet","PetAdoptionAndSale"})
            {
                var id=Guid.NewGuid();ids.Add(id);
                await Execute("""
                    INSERT Provider.ProviderAuthIdentities(ProviderAuthIdentityId,FirebaseUserId,AuthProvider,Email,IsEmailVerified)
                    VALUES(@Id,CONVERT(NVARCHAR(36),@Id),N'EmailPassword',CONCAT(@Id,N'@trust.test.invalid'),1);
                    INSERT Provider.Providers(ProviderId,ProviderAuthIdentityId,FirstName,LastName,Gender,MobileCountryCode,MobileNumber,DateOfBirth,MobileVerifiedAtUtc)
                    VALUES(@Id,@Id,N'Trust',N'Test',N'Other',N'+41',LEFT(CONVERT(NVARCHAR(36),@Id),16),'1990-01-01',SYSUTCDATETIME());
                    INSERT Provider.ProviderServiceRegistrations(ProviderId,ServiceCategory,SubCategory,Latitude,Longitude)
                    VALUES(@Id,@Category,N'Freelancer',47,8);
                    """,id,category);
                Check(!(await service.GetBadgesAsync([id],default)).ContainsKey(id),"Verified email/mobile alone never grants a badge");
                if(category=="PetAdoptionAndSale")
                {
                    try {await service.ChangeAsync(id,"Upload","https://private.invalid/doc",null,null,null,default);throw new Exception("Unsupported document category accepted");}
                    catch(ProviderTrustInvalidException){checks++;}
                    continue;
                }
                if(category=="Vet")
                {
                    try {await service.ChangeAsync(id,"MedRegListed",null,Guid.NewGuid(),null,"Reference",default);throw new Exception("Vet without uploaded evidence gained badge");}
                    catch(ProviderTrustConflictException){checks++;}
                    var doc=await service.ChangeAsync(id,"Upload","https://private.invalid/vet",null,null,null,default);
                    Check(!(await service.GetBadgesAsync([id],default)).ContainsKey(id),"Pending vet has no badge");
                    try {await service.ChangeAsync(id,"Accept",null,doc.RevisionId,null,null,default);throw new Exception("Vet document got DocumentsAdded");}
                    catch(ProviderTrustConflictException){checks++;}
                    await service.ChangeAsync(id,"MedRegListed",null,doc.RevisionId,null,"Admin checked reference 123",default);
                    Check((await service.GetBadgesAsync([id],default))[id]=="MedRegListed","Vet gets only MedRegListed");
                    var vetReplacement=await service.ChangeAsync(id,"Upload","https://private.invalid/new-vet",null,null,null,default);
                    Check(!(await service.GetBadgesAsync([id],default)).ContainsKey(id),"Vet vetReplacement removes MedReg badge");
                    try {await service.ChangeAsync(id,"MedRegListed",null,doc.RevisionId,null,"Old reference",default);throw new Exception("Stale MedReg review accepted");}
                    catch(ProviderTrustConflictException){checks++;}
                    await service.ChangeAsync(id,"Reject",null,vetReplacement.RevisionId,"Wrong document",null,default);
                    Check(!(await service.GetBadgesAsync([id],default)).ContainsKey(id),"Rejected vet upload has no badge");
                    var vetRetry=await service.ChangeAsync(id,"Upload","https://private.invalid/vet-vetRetry",null,null,null,default);
                    await service.ChangeAsync(id,"MedRegListed",null,vetRetry.RevisionId,null,"New reference",default);
                    await service.ChangeAsync(id,"MedRegNotListed",null,vetRetry.RevisionId,null,"Rechecked reference",default);
                    Check(!(await service.GetBadgesAsync([id],default)).ContainsKey(id),"Negative MedReg lookup removes badge");
                    continue;
                }
                var upload=await service.ChangeAsync(id,"Upload","https://private.invalid/first",null,null,null,default);
                Check(upload.Status=="InReview" && !(await service.GetBadgesAsync([id],default)).ContainsKey(id),"Pending upload has no badge");
                Check((await service.ListPendingAsync(default)).Any(r=>r.ProviderId==id),"Pending document enters team queue");
                await service.ChangeAsync(id,"Accept",null,upload.RevisionId,null,null,default);
                Check((await service.GetBadgesAsync([id],default))[id]=="DocumentsAdded","Accepted document grants badge");
                var replacement=await service.ChangeAsync(id,"Upload","https://private.invalid/replacement",null,null,null,default);
                Check(replacement.RevisionId!=upload.RevisionId && replacement.RejectionReason is null && !(await service.GetBadgesAsync([id],default)).ContainsKey(id),"Replacement clears badge and changes revision");
                try {await service.ChangeAsync(id,"Accept",null,upload.RevisionId,null,null,default);throw new Exception("Stale acceptance succeeded");}
                catch(ProviderTrustConflictException){checks++;}
                await service.ChangeAsync(id,"Reject",null,replacement.RevisionId,"Blurry",null,default);
                Check(!(await service.GetBadgesAsync([id],default)).ContainsKey(id) && (await service.GetAsync(id,default))!.RejectionReason=="Blurry","Rejected document has no badge");
                var retry=await service.ChangeAsync(id,"Upload","https://private.invalid/retry",null,null,null,default);
                await service.ChangeAsync(id,"Accept",null,retry.RevisionId,null,null,default);
                Check((await service.GetBadgesAsync([id],default))[id]=="DocumentsAdded","Re-upload can be accepted");
                try {await service.ChangeAsync(id,"MedRegListed",null,null,null,"Reference",default);throw new Exception("Non-vet gained MedReg badge");}
                catch(ProviderTrustInvalidException){checks++;}
                await Execute("UPDATE Provider.ProviderServiceRegistrations SET ServiceCategory=N'Vet' WHERE ProviderId=@Id",id);
                Check(!(await service.GetBadgesAsync([id],default)).ContainsKey(id),"Category change cannot reuse earlier document acceptance");
            }
            Check((await service.GetBadgesAsync(ids,default)).Count==0,"Batch returns no badge after all evidence is invalidated");
        }
        finally
        {
            foreach(var id in ids)await Execute("DELETE Provider.ProviderTrust WHERE ProviderId=@Id; DELETE Provider.ProviderServiceRegistrations WHERE ProviderId=@Id; DELETE Provider.Providers WHERE ProviderId=@Id; DELETE Provider.ProviderAuthIdentities WHERE ProviderAuthIdentityId=@Id;",id);
        }
        return checks;
    }
}
