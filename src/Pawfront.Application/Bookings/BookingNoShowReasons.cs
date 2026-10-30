namespace Pawfront.Application.Bookings;

public static class BookingNoShowReasons
{
    public const string NotArrived = "NotArrived";
    public const string OtpNotShared = "OtpNotShared";

    public static string? Normalize(string? reason, string status, BookingStatusActor actor)
    {
        if (reason is null) return null;
        if (status != BookingStatuses.ParentNoShow || actor != BookingStatusActor.Provider)
            throw new ArgumentException("No-show reason is only valid when a provider reports a customer no-show.");
        if (string.Equals(reason.Trim(), NotArrived, StringComparison.OrdinalIgnoreCase)) return NotArrived;
        if (string.Equals(reason.Trim(), OtpNotShared, StringComparison.OrdinalIgnoreCase)) return OtpNotShared;
        throw new ArgumentException("No-show reason must be NotArrived or OtpNotShared.");
    }
}
