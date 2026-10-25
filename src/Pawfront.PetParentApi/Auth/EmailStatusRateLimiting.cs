using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Pawfront.Contracts.Common;

namespace Pawfront.PetParentApi.Auth;

internal static class EmailStatusRateLimiting
{
    public const string PolicyName = "parent-email-status";

    public static IServiceCollection AddEmailStatusRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                context.HttpContext.Response.Headers.CacheControl = "no-store";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new ApiResponse<object>(false, null,
                        new ApiError("RateLimitExceeded", "Please try again later.")), cancellationToken);
            };
            options.AddPolicy(PolicyName, context => RateLimitPartition.GetFixedWindowLimiter(
                // Use the server-observed IP, never a client-supplied device or forwarding header.
                context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromHours(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        });
        return services;
    }
}
