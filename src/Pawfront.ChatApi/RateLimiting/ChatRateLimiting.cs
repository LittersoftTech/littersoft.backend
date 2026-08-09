using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Pawfront.ChatApi.Endpoints;

namespace Pawfront.ChatApi.RateLimiting;

/// <summary>
/// Per-account limits, which the OPEN chat model makes necessary: with no booking
/// required between two people, nothing else bounds how many strangers one
/// account can contact.
///
/// <b>Partitioned on the caller's Firebase uid, not their IP.</b> Abuse here is
/// per-account — an IP is shared by everyone behind one mobile carrier NAT, so
/// limiting by it would throttle innocent users while a determined sender just
/// changes network. The uid also comes straight off the validated token
/// synchronously, which the partition selector requires; a ProviderId /
/// PetParentId would need an async SQL lookup and cannot be produced here.
///
/// Two limits, because the two abuses are different shapes:
///   * message volume — noisy, but bounded to a thread the recipient can block;
///   * NEW conversations — the actual spam vector, since it reaches people who
///     have not chosen to hear from you. Much tighter.
/// </summary>
internal static class ChatRateLimiting
{
    public const string SendMessagePolicy = "chat-send";
    public const string OpenConversationPolicy = "chat-open";

    /// <summary>Comfortably above human typing speed; well below a script's.</summary>
    private const int MessagesPerMinute = 30;

    /// <summary>
    /// Ten new threads an hour. A real user opening chats with a handful of
    /// providers while shopping around never notices it.
    /// </summary>
    private const int NewConversationsPerHour = 10;

    public static IServiceCollection AddChatRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(SendMessagePolicy, PartitionByCaller(
                permitLimit: MessagesPerMinute,
                window: TimeSpan.FromMinutes(1)));

            options.AddPolicy(OpenConversationPolicy, PartitionByCaller(
                permitLimit: NewConversationsPerHour,
                window: TimeSpan.FromHours(1)));

            // Answer in the product's envelope rather than an empty 429 body, so a
            // client parses this the same way it parses every other error.
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString();
                }

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new Contracts.Common.ApiResponse<object>(
                        false,
                        default,
                        new Contracts.Common.ApiError(
                            "TooManyRequests",
                            "You're sending messages too quickly. Please wait a moment and try again.")),
                    cancellationToken);
            };
        });

        return services;
    }

    private static Func<HttpContext, RateLimitPartition<string>> PartitionByCaller(
        int permitLimit,
        TimeSpan window) =>
        httpContext =>
        {
            var key = httpContext.User.FindFirst("user_id")?.Value
                      ?? httpContext.User.FindFirst("sub")?.Value;

            // An unauthenticated request never reaches a chat handler — the policy
            // rejects it first — so this only guards the ordering. Everything
            // anonymous shares one bucket rather than getting a free pass each.
            if (string.IsNullOrWhiteSpace(key))
            {
                return RateLimitPartition.GetFixedWindowLimiter(
                    "anonymous",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = window });
            }

            // Audience-prefixed: provider and parent ids come from different
            // Firebase projects, so two users could in principle share a uid string.
            var audience = httpContext.User
                .FindFirst(Auth.AuthServiceCollectionExtensions.AudienceClaimType)?.Value ?? "unknown";

            return RateLimitPartition.GetFixedWindowLimiter(
                $"{audience}:{key}",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = window,
                    // No queueing: a rate-limited chat send should fail fast so the
                    // client can tell the user, not hold a request open.
                    QueueLimit = 0
                });
        };
}
