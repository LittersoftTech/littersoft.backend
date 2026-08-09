using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Pawfront.ChatApi.Telemetry;

/// <summary>
/// Singleton <see cref="ActivitySource"/> and <see cref="Meter"/> for the chat
/// host. A distinct service name keeps its traces separate from the two CRUD
/// hosts', which matters more here than usual: a chat host holds long-lived
/// connections, so its request/duration profile is nothing like theirs and mixing
/// them would make both harder to read.
/// </summary>
public static class ChatTelemetry
{
    public const string ServiceName = "Pawfront.ChatApi";
    public const string ServiceVersion = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    public static class TagKeys
    {
        public const string ConversationId = "pawfront.conversation_id";
        public const string ParticipantType = "pawfront.participant_type";
        public const string ParticipantId = "pawfront.participant_id";
    }

    public static Activity? StartActivity(string operationName, ActivityKind kind = ActivityKind.Internal)
    {
        return ActivitySource.StartActivity(operationName, kind);
    }

    public static Activity? StartConversationActivity(
        string operationName,
        Guid conversationId,
        ActivityKind kind = ActivityKind.Internal)
    {
        var activity = ActivitySource.StartActivity(operationName, kind);
        activity?.SetTag(TagKeys.ConversationId, conversationId);
        return activity;
    }
}
