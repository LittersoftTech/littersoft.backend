using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pawfront.Application.ProviderTrust;
using Pawfront.Contracts.Common;
using Pawfront.Contracts.Providers;

var checks=0;
void Check(bool value,string name){if(!value)throw new Exception(name);checks++;}
var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
var sitter=Guid.NewGuid();var vet=Guid.NewGuid();var pending=Guid.NewGuid();
var service=new TrustReader(new Dictionary<Guid,string>{{sitter,"DocumentsAdded"},{vet,"MedRegListed"}});
using var provider=new ServiceCollection().AddSingleton<IProviderTrustService>(service).BuildServiceProvider();
var context=new DefaultEndpointFilterInvocationContext(new DefaultHttpContext{RequestServices=provider});
var filter=new ProviderTrustBadgeFilter();
var cards=new[]{sitter,vet,pending,sitter}.Select(id=>JsonSerializer.Deserialize<ProviderSearchResultResponse>(JsonSerializer.Serialize(new{providerId=id},options),options)!).ToArray();
var result=Results.Ok(new ApiResponse<ProviderSearchResultResponse[]>(true,cards,null));
await filter.InvokeAsync(context,_=>ValueTask.FromResult<object?>(result));
Check(service.Calls==1 && service.LastCount==3,"One batched read for a search page, deduplicated");
Check(cards[0].Badge=="DocumentsAdded" && cards[1].Badge=="MedRegListed" && cards[2].Badge is null,"Exact badge values and null");
var wire=JsonSerializer.Serialize(((IValueHttpResult)result).Value,options);
Check(wire.Contains("\"badge\":null"),"Null badge is explicit on the wire");
foreach(var type in new[]{typeof(ProviderSearchResultResponse),typeof(ProviderPublicProfileResponse)})
{
    foreach(var property in type.GetProperties())
        Check(!new[]{"DocumentUrl","DocumentType","MedRegReference","EvidenceKind"}.Contains(property.Name),"Public trust response contains no evidence field");
}
var profile=JsonSerializer.Deserialize<ProviderPublicProfileResponse>(JsonSerializer.Serialize(new{providerId=sitter},options),options)!;
var profileResult=Results.Ok(new ApiResponse<ProviderPublicProfileResponse>(true,profile,null));
await filter.InvokeAsync(context,_=>ValueTask.FromResult<object?>(profileResult));
Check(profile.Badge=="DocumentsAdded","Profile badge at top level of data");
service.Badges.Clear();
await filter.InvokeAsync(context,_=>ValueTask.FromResult<object?>(profileResult));
Check(profile.Badge is null,"Replacing or rejecting removes previous badge immediately");
checks+=await AdminChecks.RunAsync();
if(args.Contains("--sql"))checks+=await SqlChecks.RunAsync();
Console.WriteLine($"Passed {checks} provider trust checks.");

sealed class TrustReader(Dictionary<Guid,string> badges):IProviderTrustService
{
    public Task SyncOfferingDocumentAsync(Guid id,string? url,CancellationToken ct)=>Task.CompletedTask;
    public Dictionary<Guid,string> Badges=badges;public int Calls,LastCount;
    public Task<IReadOnlyDictionary<Guid,string>> GetBadgesAsync(IReadOnlyCollection<Guid> ids,CancellationToken ct){Calls++;LastCount=ids.Count;return Task.FromResult<IReadOnlyDictionary<Guid,string>>(Badges);}
    public Task<ProviderTrustRecord?> GetAsync(Guid id,CancellationToken ct)=>Task.FromResult<ProviderTrustRecord?>(null);
    public Task<IReadOnlyList<ProviderTrustRecord>> ListPendingAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<ProviderTrustRecord>>([]);
    public Task<ProviderTrustRecord> ChangeAsync(Guid id,string action,string? url,Guid? revision,string? reason,string? reference,CancellationToken ct)=>throw new NotImplementedException();
}
