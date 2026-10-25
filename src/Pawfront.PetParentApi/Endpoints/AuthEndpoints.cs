using System.Net.Mail;
using Pawfront.Application.ParentOnboarding;
using Pawfront.Contracts.ParentOnboarding;

namespace Pawfront.PetParentApi.Endpoints;

internal static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/auth/email-status", GetEmailStatus)
            .AllowAnonymous()
            .RequireRateLimiting(Auth.EmailStatusRateLimiting.PolicyName)
            .Produces<Pawfront.Contracts.Common.ApiResponse<EmailStatusResponse>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status429TooManyRequests);
        return builder;
    }

    private static async Task<IResult> GetEmailStatus(
        EmailStatusRequest request,
        HttpContext context,
        IParentEmailStatusReader reader,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var email = request.Email?.Trim();
        if (string.IsNullOrEmpty(email) || email.Length > 320
            || !MailAddress.TryCreate(email, out var address)
            || !string.Equals(address.Address, email, StringComparison.Ordinal)
            || !address.Host.Contains('.') || email.Any(char.IsWhiteSpace))
        {
            return ApiResults.BadRequest("InvalidEmail", "A valid email address is required.");
        }

        return ApiResults.Ok(new EmailStatusResponse(
            await reader.IsRegisteredAsync(email, cancellationToken)));
    }
}
