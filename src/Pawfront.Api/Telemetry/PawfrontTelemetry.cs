using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Pawfront.Api.Telemetry;

/// <summary>
/// Singleton <see cref="ActivitySource"/> and <see cref="Meter"/> for Pawfront's
/// domain-level telemetry. Auto-instrumentation already covers HTTP, SQL, Cosmos,
/// and Blob spans; use these helpers when you need a custom span around a
/// composite domain operation, or to emit a metric.
/// </summary>
public static class PawfrontTelemetry
{
    public const string ServiceName = "Pawfront.Api";
    public const string ServiceVersion = "1.0.0";

    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    public static class TagKeys
    {
        public const string ProviderId = "pawfront.provider_id";
        public const string ServiceCategory = "pawfront.service_category";
        public const string SubCategory = "pawfront.sub_category";
        public const string ProviderAuthIdentityId = "pawfront.provider_auth_identity_id";
        public const string FirebaseUserId = "pawfront.firebase_user_id";
    }

    /// <summary>
    /// Starts an internal activity tagged with the provider id.
    /// Returns null if no listener is subscribed (zero allocation when telemetry is off).
    /// </summary>
    public static Activity? StartProviderActivity(
        string operationName,
        Guid providerId,
        ActivityKind kind = ActivityKind.Internal)
    {
        var activity = ActivitySource.StartActivity(operationName, kind);
        activity?.SetTag(TagKeys.ProviderId, providerId);
        return activity;
    }

    public static Activity? StartActivity(string operationName, ActivityKind kind = ActivityKind.Internal)
    {
        return ActivitySource.StartActivity(operationName, kind);
    }
}
