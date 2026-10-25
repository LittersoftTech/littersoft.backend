namespace Pawfront.Application.Support;

/// <summary>Accepted reason values for chat reports about a user.</summary>
public static class ReportUserReasons
{
    public const string OffensiveMessages = "Offensive or threatening messages";
    public const string UnwantedAdvertising = "Unwanted advertising";
    public const string FraudOrFakeProfile = "Fraud or fake profile";
    public const string OutsideBooking = "Asking to book outside Pawfront";
    public const string AnimalWelfare = "Concern for an animal's welfare";
    public const string SomethingElse = "Something else";

    public static readonly IReadOnlyList<string> All =
    [
        OffensiveMessages,
        UnwantedAdvertising,
        FraudOrFakeProfile,
        OutsideBooking,
        AnimalWelfare,
        SomethingElse
    ];

    public static bool IsKnown(string? reason) =>
        reason is not null && All.Contains(reason, StringComparer.Ordinal);
}
