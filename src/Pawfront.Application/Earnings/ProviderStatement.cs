using Pawfront.Application.Bookings;

namespace Pawfront.Application.Earnings;

public static class ProviderStatement
{
    public const int MaxDays = 30;

    public static string? ValidateRange(DateOnly? from, DateOnly? to)
    {
        if (from is null || to is null)
            return "Both 'from' and 'to' are required for a statement.";
        if (from > to)
            return "'from' must be on or before 'to'.";
        if (to.Value.DayNumber - from.Value.DayNumber + 1 > MaxDays)
            return $"The statement range must not exceed {MaxDays} days, including both 'from' and 'to'.";
        return null;
    }

    public static string? Group(string status)
    {
        if (status == BookingStatuses.Paid) return "Received";
        if (status == BookingStatuses.PaymentNotReceived) return "Unpaid";
        if (status == BookingStatuses.Expired) return "Ignored";
        if (BookingStatuses.Cancelled.Contains(status)) return "Unpaid";
        if (BookingStatuses.ConfirmedEquivalent.Contains(status)
            || BookingStatuses.ModificationRequested.Contains(status)
            || status is BookingStatuses.StartJob or BookingStatuses.InProgress
                or BookingStatuses.JobStarted or BookingStatuses.Ending or BookingStatuses.Completed)
            return "Pending";
        return null;
    }

    public static IReadOnlyList<string> Statuses { get; } =
        BookingStatuses.All.Where(s => Group(s) is not null).ToArray();

    // Cash is received directly by the provider; commission is not withheld.
    // Failed/ignored jobs show potential gross, with no commission charged.
    public static decimal? Fee(ProviderEarningsBookingRow row) =>
        row.GrossAmount is null ? null :
        row.IsPrivate || string.Equals(row.PaymentMethod, "Cash", StringComparison.OrdinalIgnoreCase)
            || Group(row.Status) is "Unpaid" or "Ignored" ? 0m : row.PawfrontFee ?? 0m;

    public static decimal? Net(ProviderEarningsBookingRow row) => row.GrossAmount - Fee(row);
}
