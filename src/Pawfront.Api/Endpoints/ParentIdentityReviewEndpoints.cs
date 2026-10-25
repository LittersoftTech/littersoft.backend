using Microsoft.AspNetCore.Authentication.JwtBearer;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Contracts.ParentOnboarding;
namespace Pawfront.Api.Endpoints;

internal static class ParentIdentityReviewEndpoints
{
    public static IEndpointRouteBuilder MapParentIdentityReviewEndpoints(this IEndpointRouteBuilder builder)
    {
        // Only signed Firebase tokens with the server-issued admin custom claim.
        var group = builder.MapGroup("/admin/parent-identities").RequireAuthorization(policy =>
            policy.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser().RequireClaim("admin", "true"));
        group.MapGet("", async (string? status, IParentIdentityReviewService service, CancellationToken ct) =>
        {
            try { return ApiResults.Ok(await service.ListAsync(status ?? "InReview", ct)); }
            catch (ArgumentException ex) { return ApiResults.BadRequest("InvalidRequest", ex.Message); }
        });
        group.MapPost("/{identityId:guid}/review", async (Guid identityId, ReviewParentIdentityRequest request, IParentIdentityReviewService service, CancellationToken ct) =>
        {
            try { await service.ReviewAsync(identityId, request, ct); return Results.NoContent(); }
            catch (ArgumentException ex) { return ApiResults.BadRequest("InvalidRequest", ex.Message); }
            catch (IdentityReviewConflictException ex) { return ApiResults.Conflict("IdentityReviewConflict", ex.Message); }
        });
        return builder;
    }
}
