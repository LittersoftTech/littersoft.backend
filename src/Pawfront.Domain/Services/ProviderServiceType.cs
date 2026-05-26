namespace Pawfront.Domain.Services;

/// <summary>
/// Identifies the specific service a provider offers inside their service category.
/// Stored as a string in SQL (column <c>ProviderServices.ServiceType</c>); names here
/// must match the SQL check constraint values exactly.
/// </summary>
public static class ProviderServiceTypes
{
    public const string DayCare = "DayCare";
    public const string NightStay = "NightStay";
    public const string GroomingSession = "GroomingSession";
    public const string TrainingSession = "TrainingSession";
    public const string VetAppointment = "VetAppointment";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        DayCare, NightStay, GroomingSession, TrainingSession, VetAppointment
    };

    public static bool IsValid(string serviceType) => All.Contains(serviceType);
}
