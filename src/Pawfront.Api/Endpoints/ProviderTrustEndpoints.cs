using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Pawfront.Api.Auth;
using Pawfront.Application.ProviderOnboarding;
using Pawfront.Application.ProviderTrust;
using Pawfront.Application.Storage;
namespace Pawfront.Api.Endpoints;

internal static class ProviderTrustEndpoints
{
    public static IEndpointRouteBuilder MapProviderTrustOwnerEndpoints(this IEndpointRouteBuilder builder)
    {
        var group=builder.MapGroup("/providers/{providerId:guid}/trust-document");
        group.AddEndpointFilter(async (context,next)=>
        {
            var ct=context.HttpContext.RequestAborted;
            var id=Guid.Parse(context.HttpContext.Request.RouteValues["providerId"]!.ToString()!);
            var onboarding=context.HttpContext.RequestServices.GetRequiredService<IProviderOnboardingService>();
            try
            {
                var caller=await onboarding.ResolveProviderByFirebaseUidAsync(FirebaseClaims.GetFirebaseUserId(context.HttpContext.User),ct);
                if(caller.ProviderId!=id) return ApiResults.Forbidden("Forbidden","You can only manage your own trust document.");
            }
            catch(ProviderAuthIdentityForFirebaseUserNotFoundException){return ApiResults.Forbidden("Forbidden","Provider account was not found.");}
            context.HttpContext.Response.Headers.CacheControl="no-store";
            return await next(context);
        });
        group.MapGet("",async(Guid providerId,IProviderTrustService service,CancellationToken ct)=>
        {
            var record=await service.GetAsync(providerId,ct);
            return record is null?ApiResults.NotFound("TrustDocumentNotFound","No trust evidence has been recorded."):ApiResults.Ok(record);
        });
        group.MapPost("",Upload).DisableAntiforgery();
        return builder;
    }
    public static IEndpointRouteBuilder MapProviderTrustAdminEndpoints(this IEndpointRouteBuilder builder)
    {
        var group=builder.MapGroup("/admin/provider-trust").RequireAuthorization(policy=>policy
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme).RequireAuthenticatedUser().RequireClaim("admin","true"));
        group.AddEndpointFilter(async(context,next)=> { context.HttpContext.Response.Headers.CacheControl="no-store"; return await next(context); });
        group.MapGet("",async(IProviderTrustService service,CancellationToken ct)=>ApiResults.Ok(await service.ListPendingAsync(ct)));
        group.MapGet("/{providerId:guid}",async(Guid providerId,IProviderTrustService service,CancellationToken ct)=>
        {
            var record=await service.GetAsync(providerId,ct);
            return record is null?ApiResults.NotFound("TrustDocumentNotFound","No trust evidence has been recorded."):ApiResults.Ok(record);
        });
        group.MapPost("/{providerId:guid}/review",async(Guid providerId,ReviewProviderDocumentRequest request,IProviderTrustService service,CancellationToken ct)=>
        {
            if(request.Decision is not ("Accept" or "Reject")) return ApiResults.BadRequest("InvalidRequest","Decision must be Accept or Reject.");
            try { return ApiResults.Ok(await service.ChangeAsync(providerId,request.Decision,null,request.RevisionId,request.RejectionReason,null,ct)); }
            catch(ProviderTrustInvalidException ex){return ApiResults.BadRequest("InvalidRequest",ex.Message);}
            catch(ProviderTrustConflictException ex){return ApiResults.Conflict("ProviderTrustConflict",ex.Message);}
        });
        group.MapPost("/{providerId:guid}/medreg",async(Guid providerId,RecordMedRegRequest request,IProviderTrustService service,CancellationToken ct)=>
        {
            try { return ApiResults.Ok(await service.ChangeAsync(providerId,request.Listed?"MedRegListed":"MedRegNotListed",null,request.RevisionId,null,request.Reference,ct)); }
            catch(ProviderTrustInvalidException ex){return ApiResults.BadRequest("InvalidRequest",ex.Message);}
            catch(ProviderTrustConflictException ex){return ApiResults.Conflict("ProviderTrustConflict",ex.Message);}
        });
        return builder;
    }
    private static async Task<IResult> Upload(Guid providerId,IFormFile file,IPawfrontBlobStorage blobs,IProviderTrustService service,CancellationToken ct)
    {
        if(file.Length==0 || file.Length>10*1024*1024 || file.ContentType is not ("application/pdf" or "image/jpeg" or "image/png" or "image/webp"))
            return ApiResults.BadRequest("InvalidFile","Upload a PDF, JPEG, PNG or WebP file up to 10 MB.");
        await using var stream=file.OpenReadStream();
        var url=await blobs.UploadAsync(BlobUploadKind.ProviderDocument,providerId,file.FileName,stream,file.ContentType,ct);
        try { return ApiResults.Ok(await service.ChangeAsync(providerId,"Upload",url,null,null,null,ct)); }
        catch(ProviderTrustInvalidException ex){return ApiResults.BadRequest("InvalidRequest",ex.Message);}
        catch(ProviderTrustConflictException ex){return ApiResults.Conflict("ProviderTrustConflict",ex.Message);}
    }
}
