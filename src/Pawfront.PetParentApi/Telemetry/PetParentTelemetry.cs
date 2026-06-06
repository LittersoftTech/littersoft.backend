using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Pawfront.PetParentApi.Telemetry;

/// <summary>
/// Singleton <see cref="ActivitySource"/> and <see cref="Meter"/> for the pet-parent
/// host. Distinct service name keeps Application Insights traces and metrics
/// separated from the provider host.
/// </summary>
public static class PetParentTelemetry
{
    public const string ServiceName = "Pawfront.PetParentApi";
    public const string ServiceVersion = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    public static class TagKeys
    {
        public const string PetParentId = "pawfront.pet_parent_id";
        public const string FirebaseUserId = "pawfront.firebase_user_id";
    }

    public static Activity? StartPetParentActivity(
        string operationName,
        Guid petParentId,
        ActivityKind kind = ActivityKind.Internal)
    {
        var activity = ActivitySource.StartActivity(operationName, kind);
        activity?.SetTag(TagKeys.PetParentId, petParentId);
        return activity;
    }

    public static Activity? StartActivity(string operationName, ActivityKind kind = ActivityKind.Internal)
    {
        return ActivitySource.StartActivity(operationName, kind);
    }
}
