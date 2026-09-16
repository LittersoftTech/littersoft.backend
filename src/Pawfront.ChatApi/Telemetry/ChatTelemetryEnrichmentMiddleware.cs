using System.Diagnostics;

namespace Pawfront.ChatApi.Telemetry;

/// <summary>
/// Decorates the current ASP.NET Core request <see cref="Activity"/> with
/// <c>pawfront.conversation_id</c> when the route exposes a
/// <c>{conversationId}</c> segment. Mirrors the two CRUD hosts' enrichment
/// middleware.
/// </summary>
internal sealed class ChatTelemetryEnrichmentMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (Activity.Current is { } activity
            && context.GetRouteValue("conversationId") is string conversationIdRaw
            && Guid.TryParse(conversationIdRaw, out var conversationId))
        {
            activity.SetTag(ChatTelemetry.TagKeys.ConversationId, conversationId);
        }

        await next(context);
    }
}
